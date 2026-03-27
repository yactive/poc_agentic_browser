using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgenticBrowser;

public partial class Form1 : Form
{
    private CancellationTokenSource? _cts;
    private const string SettingsFile = "agentic_browser_settings.json";
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgenticBrowser", "session.log");

    // Playwright objects
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    // Tracking
    private string _lastActionKey = "";
    private int _repeatCount;
    private int _consecutiveFailCount;
    private string _lastPageUrl = "";
    private int _samePageCount;
    private List<string> _recentActions = new();
    private const int MaxSnapshotChars = 12000;

    // Hybrid mode — plan storage
    private static readonly string PlansDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgenticBrowser", "plans");
    private List<RecordedStep>? _recordingSteps;
    private string? _hybridModelOverride; // Overrides cboModel in hybrid mode

    private class RecordedStep
    {
        public string Action { get; set; } = "";
        public JObject Input { get; set; } = new();
        public string Result { get; set; } = "";
        public string Url { get; set; } = "";
    }

    private class SavedPlan
    {
        public string Description { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string SourceTask { get; set; } = "";
        public List<string> Steps { get; set; } = new(); // Generic step descriptions
    }

    public Form1()
    {
        InitializeComponent();
        btnExecute.Click += BtnExecute_Click;
        btnStop.Click += BtnStop_Click;
        btnLaunchChrome.Click += BtnLaunchChrome_Click;
        btnClear.Click += (_, _) => { txtOutput.Clear(); picScreenshot.Image = null; };
        this.Load += Form1_Load;
        this.FormClosing += Form1_Closing;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"\n=== App started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
        catch { }
    }

    private void Form1_Load(object? sender, EventArgs e) => LoadSettings();

    private void Form1_Closing(object? sender, FormClosingEventArgs e)
    {
        SaveSettings();
        DisconnectPlaywright();
    }

    // ==================== Settings ====================

    private void LoadSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SettingsFile);
        if (!File.Exists(path)) return;
        try
        {
            var json = JObject.Parse(File.ReadAllText(path));
            txtApiKey.Text = json["apiKey"]?.ToString() ?? "";
            txtGeminiKey.Text = json["geminiKey"]?.ToString() ?? "";
            cboModel.SelectedIndex = Math.Min(json["modelIndex"]?.ToObject<int>() ?? 0, cboModel.Items.Count - 1);
            nudPort.Value = json["port"]?.ToObject<int>() ?? 9222;
            txtTargetUrl.Text = json["targetUrl"]?.ToString() ?? "";
            chkHeadless.Checked = json["headless"]?.ToObject<bool>() ?? false;
            nudMaxSteps.Value = json["maxSteps"]?.ToObject<int>() ?? 25;
            cboMode.SelectedIndex = Math.Min(json["modeIndex"]?.ToObject<int>() ?? 0, cboMode.Items.Count - 1);
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var json = new JObject
            {
                ["apiKey"] = txtApiKey.Text,
                ["geminiKey"] = txtGeminiKey.Text,
                ["modelIndex"] = cboModel.SelectedIndex,
                ["port"] = (int)nudPort.Value,
                ["targetUrl"] = txtTargetUrl.Text,
                ["headless"] = chkHeadless.Checked,
                ["maxSteps"] = (int)nudMaxSteps.Value,
                ["modeIndex"] = cboMode.SelectedIndex,
            };
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, SettingsFile), json.ToString(Formatting.Indented));
        }
        catch { }
    }

    // ==================== Hybrid Plan Management ====================

    private static List<SavedPlan> LoadAllPlans()
    {
        if (!Directory.Exists(PlansDir)) return new();
        var plans = new List<SavedPlan>();
        foreach (var file in Directory.GetFiles(PlansDir, "*.json"))
        {
            try { plans.Add(JsonConvert.DeserializeObject<SavedPlan>(File.ReadAllText(file))!); }
            catch { }
        }
        return plans;
    }

    private static void SavePlan(SavedPlan plan)
    {
        Directory.CreateDirectory(PlansDir);
        var id = Guid.NewGuid().ToString("N")[..8];
        var json = JsonConvert.SerializeObject(plan, Formatting.Indented);
        File.WriteAllText(Path.Combine(PlansDir, $"plan_{id}.json"), json);
    }

    /// <summary>Ask Sonnet to check if the task matches any existing plan (cheap, ~100 tokens).</summary>
    private async Task<SavedPlan?> MatchPlanWithAI(string taskText, List<SavedPlan> plans, CancellationToken ct)
    {
        if (plans.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Given these saved WORKFLOW STRATEGIES:");
        for (int i = 0; i < plans.Count; i++)
            sb.AppendLine($"{i + 1}. {plans[i].Description}");
        sb.AppendLine();
        sb.AppendLine("Is the following task the same TYPE of workflow as any strategy above?");
        sb.AppendLine("Ignore specific data (names, phone numbers, text) — only match the WORKFLOW PATTERN.");
        sb.AppendLine();
        sb.AppendLine($"Task: {taskText}");
        sb.AppendLine();
        sb.AppendLine("Reply with ONLY the number (e.g. '1'), or 'none'.");

        var response = await AskClaude(sb.ToString(), ct);
        response = response.Trim().TrimEnd('.');

        if (int.TryParse(response, out int idx) && idx >= 1 && idx <= plans.Count)
            return plans[idx - 1];
        return null;
    }

    /// <summary>Ask Sonnet to generalize the recorded steps into a reusable strategy.</summary>
    private async Task<SavedPlan> CreateGenericPlan(string taskText, List<RecordedStep> steps, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A browser automation task completed successfully. Create a GENERIC reusable workflow strategy.");
        sb.AppendLine("Remove ALL specific data (names, phone numbers, emails, text content).");
        sb.AppendLine("Keep only the workflow pattern so it can be reused with DIFFERENT data.");
        sb.AppendLine();
        sb.AppendLine($"Original task: {taskText}");
        sb.AppendLine("\nActions taken:");
        foreach (var s in steps)
            sb.AppendLine($"- [{s.Action}] {s.Result} (on {s.Url})");
        sb.AppendLine();
        sb.AppendLine("Reply in this EXACT format (no markdown, no extra text):");
        sb.AppendLine("DESCRIPTION: <one-line workflow description, e.g. 'Search for candidate by phone in Zoho Recruit and add a note'>");
        sb.AppendLine("STEPS:");
        sb.AppendLine("1. <generic step, e.g. 'Navigate to the Candidates module'>");
        sb.AppendLine("2. <generic step>");
        sb.AppendLine("...");

        var response = await AskClaude(sb.ToString(), ct);
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var description = "";
        var genericSteps = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DESCRIPTION:", StringComparison.OrdinalIgnoreCase))
                description = trimmed[12..].Trim();
            else if (trimmed.Length >= 3 && char.IsDigit(trimmed[0]) && trimmed.Contains('.'))
            {
                var dotIdx = trimmed.IndexOf('.');
                if (dotIdx > 0 && dotIdx < 4)
                    genericSteps.Add(trimmed[(dotIdx + 1)..].Trim());
            }
        }

        return new SavedPlan
        {
            Description = description.Length > 0 ? description : "Unnamed workflow",
            CreatedAt = DateTime.UtcNow,
            SourceTask = taskText,
            Steps = genericSteps,
        };
    }

    /// <summary>Quick Sonnet call for plan matching/creation (text only, cheap).</summary>
    private async Task<string> AskClaude(string prompt, CancellationToken ct)
    {
        var body = new JObject
        {
            ["model"] = "claude-sonnet-4-6",
            ["max_tokens"] = 500,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "user", ["content"] = prompt }
            }
        };

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("x-api-key", txtApiKey.Text.Trim());
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("https://api.anthropic.com/v1/messages", content, ct);
        var respBody = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
        return respBody["content"]?[0]?["text"]?.ToString() ?? "";
    }

    private static string FormatPlanForPrompt(SavedPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROVEN STRATEGY (follow these steps in order — adapt to the actual page):");
        for (int i = 0; i < plan.Steps.Count; i++)
            sb.AppendLine($"  Step {i + 1}: {plan.Steps[i]}");
        sb.AppendLine("\nFollow this strategy step by step. Execute ONE action per turn.");
        sb.AppendLine("Use the EXACT data from the TASK above (names, numbers, text).");
        sb.AppendLine("If an element can't be found exactly, adapt (click_text, press Enter, etc.).");
        sb.AppendLine("When all steps are done or task is complete, call 'done'.");
        return sb.ToString();
    }

    // ==================== UI Helpers ====================

    private void AppendOutput(string text, Color color)
    {
        if (InvokeRequired) { Invoke(() => AppendOutput(text, color)); return; }
        txtOutput.SelectionStart = txtOutput.TextLength;
        txtOutput.SelectionLength = 0;
        txtOutput.SelectionColor = color;
        txtOutput.AppendText(text);
        txtOutput.ScrollToCaret();
        LogToFile(text);
    }

    private void AppendSection(string title)
    {
        AppendOutput($"\n{"".PadRight(60, '\u2500')}\n  {title}\n{"".PadRight(60, '\u2500')}\n", Color.Cyan);
    }

    private void SetRunning(bool running)
    {
        if (InvokeRequired) { Invoke(() => SetRunning(running)); return; }
        btnExecute.Enabled = !running;
        btnStop.Enabled = running;
        btnLaunchChrome.Enabled = !running;
        lblStatus.Text = running ? "Running..." : "Ready";
    }

    private void UpdateScreenshot(byte[] screenshotBytes)
    {
        if (InvokeRequired) { Invoke(() => UpdateScreenshot(screenshotBytes)); return; }
        try
        {
            using var ms = new MemoryStream(screenshotBytes);
            var oldImage = picScreenshot.Image;
            picScreenshot.Image = Image.FromStream(ms);
            oldImage?.Dispose();
        }
        catch (Exception ex)
        {
            AppendOutput($"[Screenshot display error: {ex.Message}]\n", Color.DimGray);
        }
    }

    private void LogToFile(string text)
    {
        try { File.AppendAllText(LogFile, text); } catch { }
    }

    // ==================== Chrome Launch ====================

    private async void BtnLaunchChrome_Click(object? sender, EventArgs e)
    {
        var chromePath = FindChromeExecutable();
        if (chromePath == null)
        {
            MessageBox.Show("Could not find Chrome. Please install Google Chrome.", "Chrome Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var port = (int)nudPort.Value;
        if (await IsChromeDebugPortOpen(port))
        {
            AppendOutput($"\n[Chrome already running on port {port} \u2014 ready!]\n", Color.LimeGreen);
            lblStatus.Text = $"Chrome on port {port} \u2014 ready";
            return;
        }

        await LaunchChromeDedicatedProfile(chromePath, port);
    }

    private async Task LaunchChromeDedicatedProfile(string chromePath, int port)
    {
        try
        {
            var profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgenticBrowser", $"ChromeProfile_{port}");
            Directory.CreateDirectory(profileDir);
            var isFirstLaunch = !File.Exists(Path.Combine(profileDir, "Local State"));

            var args = $"--remote-debugging-port={port} --remote-allow-origins=* --user-data-dir=\"{profileDir}\"";
            if (chkHeadless.Checked)
                args += " --headless=new --disable-gpu";

            AppendOutput($"\n[Launching Chrome on port {port}{(chkHeadless.Checked ? " (headless)" : "")}...]\n", Color.FromArgb(180, 220, 255));
            AppendOutput($"[Profile: {profileDir}]\n", Color.DimGray);

            var psi = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = args,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            AppendOutput($"[Chrome PID: {proc?.Id}]\n", Color.DimGray);

            lblStatus.Text = "Waiting for Chrome debug port...";
            var ready = await WaitForDebugPort(port, timeoutSeconds: 20);
            if (ready)
            {
                AppendOutput($"[Chrome debug port {port} READY!]\n", Color.LimeGreen);
                if (isFirstLaunch && !chkHeadless.Checked)
                    AppendOutput("[FIRST TIME: Log into your accounts in this Chrome window.]\n", Color.Orange);
                else
                    AppendOutput("[Ready to Execute!]\n", Color.FromArgb(180, 220, 255));
                lblStatus.Text = $"Chrome on port {port} \u2014 ready";
            }
            else
            {
                AppendOutput($"[Chrome port {port} not responding after 20s.]\n", Color.OrangeRed);
                lblStatus.Text = "Chrome not responding";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch Chrome: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task<bool> WaitForDebugPort(int port, int timeoutSeconds = 20)
    {
        for (int i = 0; i < timeoutSeconds * 2; i++)
        {
            if (await IsChromeDebugPortOpen(port)) return true;
            await Task.Delay(500);
            if (i % 4 == 3)
                AppendOutput($"[Waiting for port {port}... ({(i + 1) / 2}s)]\n", Color.DimGray);
        }
        return false;
    }

    private static async Task<bool> IsChromeDebugPortOpen(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://127.0.0.1:{port}/json/version");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string? FindChromeExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ==================== Playwright Connection ====================

    private async Task<bool> ConnectPlaywright(int port)
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            AppendOutput("[Playwright engine created]\n", Color.DimGray);

            _browser = await _playwright.Chromium.ConnectOverCDPAsync(
                $"http://127.0.0.1:{port}",
                new BrowserTypeConnectOverCDPOptions { Timeout = 15000 });

            _context = _browser.Contexts.Count > 0 ? _browser.Contexts[0] : null;
            if (_context == null)
            {
                AppendOutput("[ERROR: No browser context found]\n", Color.OrangeRed);
                return false;
            }

            // Find the active page
            var pages = _context.Pages;
            _page = pages.FirstOrDefault(p =>
            {
                try { return p.Url != "about:blank" && p.Url != "chrome://newtab/"; }
                catch { return false; }
            }) ?? pages.FirstOrDefault();

            if (_page == null)
                _page = await _context.NewPageAsync();

            // Track new pages (popups, new tabs)
            _context.Page += (_, newPage) =>
            {
                AppendOutput($"[New page opened: {newPage.Url}]\n", Color.DimGray);
                _page = newPage;
            };

            var title = "";
            try { title = await _page.TitleAsync(); } catch { title = "(unknown)"; }
            AppendOutput($"[Connected! Page: {_page.Url} | Title: {title} | {pages.Count} tabs]\n", Color.LimeGreen);
            return true;
        }
        catch (Exception ex)
        {
            AppendOutput($"[Playwright connect error: {ex.Message}]\n", Color.OrangeRed);
            return false;
        }
    }

    private void DisconnectPlaywright()
    {
        try { _browser?.DisposeAsync().AsTask().Wait(3000); } catch { }
        try { _playwright?.Dispose(); } catch { }
        _page = null;
        _context = null;
        _browser = null;
        _playwright = null;
    }

    // ==================== Page State ====================

    private async Task<JObject?> GetPageState(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_page == null || _context == null) return null;

        try
        {
            // Re-acquire the active page (it may have changed after navigation)
            var pages = _context.Pages;
            if (pages.Count > 0)
            {
                var activePage = pages.FirstOrDefault(p => p.Url != "about:blank" && p.Url != "chrome://newtab/");
                if (activePage != null && activePage != _page)
                {
                    AppendOutput($"[Switching to active page: {activePage.Url}]\n", Color.DimGray);
                    _page = activePage;
                }
            }

            // Wait for page to settle
            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 15000 });
            }
            catch (Exception e)
            {
                AppendOutput($"[waitForLoadState timed out, continuing: {e.Message}]\n", Color.DimGray);
            }

            // Small extra wait for dynamic content
            await Task.Delay(1000, ct);

            // Take screenshot (JPEG, low quality for token savings)
            byte[]? screenshotBytes = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    screenshotBytes = await _page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Jpeg,
                        Quality = 30,
                        Scale = ScreenshotScale.Css,
                        Timeout = 15000,
                    });
                    break;
                }
                catch (Exception e)
                {
                    AppendOutput($"[Screenshot attempt {attempt + 1} failed: {e.Message}]\n", Color.DimGray);
                    if (attempt == 2) throw;
                    // On retry, try re-acquiring the page
                    try
                    {
                        var retryPages = _context.Pages;
                        var activePage = retryPages.FirstOrDefault(p => p.Url != "about:blank") ?? retryPages.FirstOrDefault();
                        if (activePage != null) _page = activePage;
                        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 });
                    }
                    catch { }
                    await Task.Delay(2000, ct);
                }
            }

            if (screenshotBytes == null) return null;
            AppendOutput($"[Screenshot size: {screenshotBytes.Length / 1024}KB]\n", Color.DimGray);

            // Update screenshot in UI
            UpdateScreenshot(screenshotBytes);

            var screenshotBase64 = Convert.ToBase64String(screenshotBytes);

            // Get accessibility snapshot
            string ariaSnapshot = "";
            try
            {
                ariaSnapshot = await _page.Locator("body").First.AriaSnapshotAsync(new() { Timeout = 10000 });
            }
            catch (Exception e)
            {
                AppendOutput($"[ariaSnapshot failed: {e.Message}, falling back to manual extraction]\n", Color.DimGray);
                try
                {
                    ariaSnapshot = await _page.EvaluateAsync<string>(@"() => {
                        const selectors = 'a[href],button,input,select,textarea,[role=""button""],[role=""link""],[role=""textbox""],[role=""checkbox""],[role=""tab""],[role=""menuitem""],[role=""option""],[contenteditable=""true""]';
                        const results = [];
                        document.querySelectorAll(selectors).forEach(el => {
                            const rect = el.getBoundingClientRect();
                            if (rect.width === 0 && rect.height === 0) return;
                            if (rect.bottom < -50 || rect.top > window.innerHeight + 200) return;
                            const tag = el.tagName.toLowerCase();
                            const role = el.getAttribute('role') || (tag === 'a' ? 'link' : tag === 'button' ? 'button' : tag === 'input' ? 'textbox' : tag);
                            const name = el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.getAttribute('name') || (el.innerText || '').trim().substring(0, 50) || '';
                            if (!name) return;
                            const type = el.getAttribute('type') || '';
                            const value = (el.value || '').substring(0, 30);
                            let line = `- ${role} ""${name}""`;
                            if (type) line += ` type=${type}`;
                            if (value) line += ` value=""${value}""`;
                            if (el.disabled) line += ' disabled';
                            results.push(line);
                        });
                        return results.join('\n');
                    }") ?? "";
                    if (string.IsNullOrEmpty(ariaSnapshot))
                        ariaSnapshot = "(no interactive elements found)";
                }
                catch (Exception e2)
                {
                    AppendOutput($"[Manual DOM extraction also failed: {e2.Message}]\n", Color.DimGray);
                    ariaSnapshot = "(Could not extract page structure)";
                }
            }

            var url = _page.Url;
            var title = await _page.TitleAsync();

            // Get scroll info
            var scrollInfo = await _page.EvaluateAsync<string>(@"() => {
                const top = window.scrollY;
                const totalHeight = document.documentElement.scrollHeight;
                const viewHeight = window.innerHeight;
                const pageNum = Math.round(top / viewHeight) + 1;
                const totalPages = Math.max(1, Math.round(totalHeight / viewHeight));
                return `Scroll: page ${pageNum} of ${totalPages}`;
            }");

            var snapshotLen = ariaSnapshot?.Length ?? 0;
            // Truncate huge snapshots — AI can't process 60K+ chars effectively
            if (snapshotLen > MaxSnapshotChars)
            {
                ariaSnapshot = ariaSnapshot!.Substring(0, MaxSnapshotChars) +
                    $"\n\n... [TRUNCATED: snapshot was {snapshotLen} chars, showing first {MaxSnapshotChars}. Use the screenshot to identify elements not listed here. Try click_text or click_selector for elements you can see in the screenshot but not in this tree.]";
                AppendOutput($"[Snapshot truncated: {snapshotLen} → {MaxSnapshotChars} chars]\n", Color.Orange);
            }
            AppendOutput($"[Page: {url} | snapshot: {snapshotLen} chars | {scrollInfo}]\n", Color.DimGray);

            return new JObject
            {
                ["url"] = url,
                ["title"] = title,
                ["ariaSnapshot"] = ariaSnapshot,
                ["screenshotBase64"] = screenshotBase64,
                ["scrollInfo"] = scrollInfo,
            };
        }
        catch (Exception ex)
        {
            AppendOutput($"[get_state exception: {ex.Message}]\n", Color.OrangeRed);
            return null;
        }
    }

    // ==================== Action Execution ====================

    private ILocator ResolveLocator(JObject action)
    {
        if (_page == null) throw new Exception("No page connected");

        var selector = action["selector"]?.ToString();
        if (selector != null)
            return _page.Locator(selector).Nth(action["nth"]?.ToObject<int>() ?? 0);

        var label = action["label"]?.ToString();
        if (label != null)
            return _page.GetByLabel(label, new() { Exact = action["exact"]?.ToObject<bool>() ?? false })
                .Nth(action["nth"]?.ToObject<int>() ?? 0);

        var placeholder = action["placeholder"]?.ToString();
        if (placeholder != null)
            return _page.GetByPlaceholder(placeholder, new() { Exact = action["exact"]?.ToObject<bool>() ?? false })
                .Nth(action["nth"]?.ToObject<int>() ?? 0);

        var role = action["role"]?.ToString();
        if (role != null)
        {
            var ariaRole = ParseAriaRole(role);
            var opts = new PageGetByRoleOptions();
            var name = action["name"]?.ToString();
            if (name != null) opts.Name = name;
            if (action["exact"] != null) opts.Exact = action["exact"].ToObject<bool>();
            return _page.GetByRole(ariaRole, opts).Nth(action["nth"]?.ToObject<int>() ?? 0);
        }

        var text = action["text"]?.ToString();
        if (text != null)
            return _page.GetByText(text, new() { Exact = action["exact"]?.ToObject<bool>() ?? false })
                .Nth(action["nth"]?.ToObject<int>() ?? 0);

        throw new Exception("No locator strategy provided (need role, label, placeholder, selector, or text)");
    }

    private static AriaRole ParseAriaRole(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "alert" => AriaRole.Alert,
            "alertdialog" => AriaRole.Alertdialog,
            "application" => AriaRole.Application,
            "article" => AriaRole.Article,
            "banner" => AriaRole.Banner,
            "blockquote" => AriaRole.Blockquote,
            "button" => AriaRole.Button,
            "caption" => AriaRole.Caption,
            "cell" => AriaRole.Cell,
            "checkbox" => AriaRole.Checkbox,
            "code" => AriaRole.Code,
            "columnheader" => AriaRole.Columnheader,
            "combobox" => AriaRole.Combobox,
            "complementary" => AriaRole.Complementary,
            "contentinfo" => AriaRole.Contentinfo,
            "definition" => AriaRole.Definition,
            "deletion" => AriaRole.Deletion,
            "dialog" => AriaRole.Dialog,
            "directory" => AriaRole.Directory,
            "document" => AriaRole.Document,
            "emphasis" => AriaRole.Emphasis,
            "feed" => AriaRole.Feed,
            "figure" => AriaRole.Figure,
            "form" => AriaRole.Form,
            "generic" => AriaRole.Generic,
            "grid" => AriaRole.Grid,
            "gridcell" => AriaRole.Gridcell,
            "group" => AriaRole.Group,
            "heading" => AriaRole.Heading,
            "img" => AriaRole.Img,
            "insertion" => AriaRole.Insertion,
            "link" => AriaRole.Link,
            "list" => AriaRole.List,
            "listbox" => AriaRole.Listbox,
            "listitem" => AriaRole.Listitem,
            "log" => AriaRole.Log,
            "main" => AriaRole.Main,
            "marquee" => AriaRole.Marquee,
            "math" => AriaRole.Math,
            "meter" => AriaRole.Meter,
            "menu" => AriaRole.Menu,
            "menubar" => AriaRole.Menubar,
            "menuitem" => AriaRole.Menuitem,
            "menuitemcheckbox" => AriaRole.Menuitemcheckbox,
            "menuitemradio" => AriaRole.Menuitemradio,
            "navigation" => AriaRole.Navigation,
            "none" => AriaRole.None,
            "note" => AriaRole.Note,
            "option" => AriaRole.Option,
            "paragraph" => AriaRole.Paragraph,
            "presentation" => AriaRole.Presentation,
            "progressbar" => AriaRole.Progressbar,
            "radio" => AriaRole.Radio,
            "radiogroup" => AriaRole.Radiogroup,
            "region" => AriaRole.Region,
            "row" => AriaRole.Row,
            "rowgroup" => AriaRole.Rowgroup,
            "rowheader" => AriaRole.Rowheader,
            "scrollbar" => AriaRole.Scrollbar,
            "search" => AriaRole.Search,
            "searchbox" => AriaRole.Searchbox,
            "separator" => AriaRole.Separator,
            "slider" => AriaRole.Slider,
            "spinbutton" => AriaRole.Spinbutton,
            "status" => AriaRole.Status,
            "strong" => AriaRole.Strong,
            "subscript" => AriaRole.Subscript,
            "superscript" => AriaRole.Superscript,
            "switch" => AriaRole.Switch,
            "tab" => AriaRole.Tab,
            "table" => AriaRole.Table,
            "tablist" => AriaRole.Tablist,
            "tabpanel" => AriaRole.Tabpanel,
            "term" => AriaRole.Term,
            "textbox" => AriaRole.Textbox,
            "time" => AriaRole.Time,
            "timer" => AriaRole.Timer,
            "toolbar" => AriaRole.Toolbar,
            "tooltip" => AriaRole.Tooltip,
            "tree" => AriaRole.Tree,
            "treegrid" => AriaRole.Treegrid,
            "treeitem" => AriaRole.Treeitem,
            _ => AriaRole.Generic,
        };
    }

    private async Task<(bool ok, string message)> ExecuteAction(string actionType, JObject input)
    {
        if (_page == null) return (false, "No page connected");

        try
        {
            switch (actionType)
            {
                case "click":
                {
                    var locator = ResolveLocator(input);
                    try { await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 3000 }); } catch { }
                    await locator.ClickAsync(new() { Timeout = 5000 });
                    await _page.WaitForTimeoutAsync(1000);
                    return (true, $"Clicked {input["role"] ?? "element"} \"{input["name"] ?? ""}\"");
                }

                case "fill":
                {
                    var locator = ResolveLocator(input);
                    var text = input["text"]!.ToString();
                    try
                    {
                        await locator.FillAsync(text, new() { Timeout = 5000 });
                    }
                    catch
                    {
                        // Fallback: click + type
                        await locator.ClickAsync(new() { Timeout = 3000 });
                        if (input["clear"]?.ToObject<bool>() != false)
                        {
                            await _page.Keyboard.PressAsync("Control+a");
                            await _page.Keyboard.PressAsync("Delete");
                        }
                        await _page.Keyboard.TypeAsync(text, new() { Delay = 30 });
                    }
                    await _page.WaitForTimeoutAsync(500);
                    return (true, $"Filled \"{text[..Math.Min(text.Length, 30)]}\" into {input["role"] ?? "element"} \"{input["name"] ?? ""}\"");
                }

                case "navigate":
                {
                    var url = input["url"]!.ToString();
                    await _page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                    await _page.WaitForTimeoutAsync(2000);
                    return (true, $"Navigated to {url}");
                }

                case "scroll":
                {
                    var direction = input["direction"]?.ToString() ?? "down";
                    var amount = input["pages"]?.ToObject<int>() ?? 1;
                    await _page.EvaluateAsync($@"({{ dir, pg }}) => {{
                        const distance = pg * window.innerHeight * 0.8 * (dir === 'down' ? 1 : -1);
                        window.scrollBy({{ top: distance, behavior: 'smooth' }});
                    }}", new { dir = direction, pg = amount });
                    await _page.WaitForTimeoutAsync(800);
                    return (true, $"Scrolled {direction} {amount} page(s)");
                }

                case "press":
                {
                    var key = input["key"]!.ToString();
                    await _page.Keyboard.PressAsync(key);
                    await _page.WaitForTimeoutAsync(500);
                    return (true, $"Pressed: {key}");
                }

                case "type":
                {
                    var text = input["text"]!.ToString();
                    if (text.Length > 100)
                    {
                        // For long text, paste via evaluate instead of typing char by char
                        await _page.EvaluateAsync(@"(text) => {
                            const el = document.activeElement;
                            if (el) {
                                if (el.getAttribute('contenteditable') !== null || el.isContentEditable) {
                                    document.execCommand('insertText', false, text);
                                } else {
                                    const proto = el.tagName === 'TEXTAREA'
                                        ? window.HTMLTextAreaElement.prototype
                                        : window.HTMLInputElement.prototype;
                                    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                                    if (setter) {
                                        setter.call(el, (el.value || '') + text);
                                        el.dispatchEvent(new Event('input', { bubbles: true }));
                                        el.dispatchEvent(new Event('change', { bubbles: true }));
                                    }
                                }
                            }
                        }", text);
                    }
                    else
                    {
                        await _page.Keyboard.TypeAsync(text, new() { Delay = 30 });
                    }
                    await _page.WaitForTimeoutAsync(500);
                    return (true, $"Typed: \"{text[..Math.Min(text.Length, 30)]}...\" ({text.Length} chars)");
                }

                case "select_option":
                {
                    var locator = ResolveLocator(input);
                    var value = input["value"]!.ToString();
                    await locator.SelectOptionAsync(value, new() { Timeout = 5000 });
                    await _page.WaitForTimeoutAsync(500);
                    return (true, $"Selected \"{value}\"");
                }

                case "check":
                {
                    var locator = ResolveLocator(input);
                    await locator.CheckAsync(new() { Timeout = 5000 });
                    return (true, $"Checked {input["role"] ?? "checkbox"} \"{input["name"] ?? ""}\"");
                }

                case "uncheck":
                {
                    var locator = ResolveLocator(input);
                    await locator.UncheckAsync(new() { Timeout = 5000 });
                    return (true, $"Unchecked {input["role"] ?? "checkbox"} \"{input["name"] ?? ""}\"");
                }

                case "go_back":
                {
                    await _page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });
                    await _page.WaitForTimeoutAsync(1000);
                    return (true, "Navigated back");
                }

                case "wait":
                {
                    var ms = input["ms"]?.ToObject<int>() ?? 2000;
                    await _page.WaitForTimeoutAsync(ms);
                    return (true, $"Waited {ms}ms");
                }

                case "new_tab":
                {
                    _page = await _context!.NewPageAsync();
                    var url = input["url"]?.ToString();
                    if (url != null)
                        await _page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                    await _page.WaitForTimeoutAsync(1000);
                    return (true, $"Opened new tab{(url != null ? ": " + url : "")}");
                }

                case "switch_tab":
                {
                    var pages = _context!.Pages;
                    var index = input["index"]!.ToObject<int>();
                    if (index >= 0 && index < pages.Count)
                    {
                        _page = pages[index];
                        await _page.BringToFrontAsync();
                        return (true, $"Switched to tab [{index}]: {_page.Url}");
                    }
                    return (false, $"Tab index {index} out of range ({pages.Count} tabs)");
                }

                case "click_text":
                {
                    var text = input["text"]!.ToString();
                    var exact = input["exact"]?.ToObject<bool>() ?? false;
                    await _page.GetByText(text, new() { Exact = exact }).First.ClickAsync(new() { Timeout = 5000 });
                    await _page.WaitForTimeoutAsync(1000);
                    return (true, $"Clicked text \"{text}\"");
                }

                case "click_selector":
                {
                    var sel = input["selector"]!.ToString();
                    await _page.Locator(sel).First.ClickAsync(new() { Timeout = 5000 });
                    await _page.WaitForTimeoutAsync(1000);
                    return (true, $"Clicked selector {sel}");
                }

                default:
                    return (false, $"Unknown action type: {actionType}");
            }
        }
        catch (Exception e)
        {
            return (false, e.Message.Length > 300 ? e.Message[..300] : e.Message);
        }
    }

    // ==================== Execute Button ====================

    private async void BtnExecute_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtInstruction.Text))
        {
            MessageBox.Show("Please enter instructions.", "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var isHybrid = cboMode.SelectedIndex == 1;
        var selectedModel = cboModel.SelectedItem?.ToString() ?? "";

        if (isHybrid)
        {
            // Hybrid needs both keys
            if (string.IsNullOrWhiteSpace(txtApiKey.Text))
            {
                MessageBox.Show("Hybrid mode requires a Claude API key (for learning).", "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(txtGeminiKey.Text))
            {
                MessageBox.Show("Hybrid mode requires a Gemini API key (for replay).", "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        else if (selectedModel.StartsWith("gemini"))
        {
            if (string.IsNullOrWhiteSpace(txtGeminiKey.Text))
            {
                MessageBox.Show("Please enter your Google Gemini API key.", "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(txtApiKey.Text))
            {
                MessageBox.Show("Please enter your Anthropic API key.", "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        var port = (int)nudPort.Value;
        if (!await IsChromeDebugPortOpen(port))
        {
            MessageBox.Show($"Chrome debug port {port} is not active.\nClick 'Launch Chrome' first.", "Chrome Not Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        SetRunning(true);

        try
        {
            await RunAgenticLoop(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendOutput("\nStopped by user.\n", Color.Yellow);
        }
        catch (Exception ex)
        {
            AppendOutput($"\nFatal error: {ex.Message}\n", Color.OrangeRed);
            LogToFile($"\nException: {ex}\n");
        }
        finally
        {
            SetRunning(false);
            lblIteration.Text = "";
        }
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        lblStatus.Text = "Stopping...";
    }

    // ==================== Agentic Loop ====================

    private async Task RunAgenticLoop(CancellationToken ct)
    {
        var port = (int)nudPort.Value;
        var maxSteps = (int)nudMaxSteps.Value;

        // Connect Playwright to Chrome
        AppendSection("Connecting to Chrome");
        DisconnectPlaywright(); // clean up any previous connection
        if (!await ConnectPlaywright(port))
            return;

        // Navigate to target URL if specified
        var targetUrl = txtTargetUrl.Text.Trim();
        if (!string.IsNullOrEmpty(targetUrl) && _page != null)
        {
            AppendOutput($"[Navigating to {targetUrl}...]\n", Color.FromArgb(180, 220, 255));
            try
            {
                await _page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                await _page.WaitForTimeoutAsync(2000);
            }
            catch (Exception ex)
            {
                AppendOutput($"[Navigation warning: {ex.Message}]\n", Color.Orange);
            }
        }

        // Get initial page state
        AppendSection("Getting Initial Page State");
        var state = await GetPageState(ct);
        if (state == null) return;

        // Build conversation messages
        var messages = new List<object>();
        var instruction = txtInstruction.Text.Trim();
        var taskText = $"Task: {instruction}";
        if (!string.IsNullOrEmpty(targetUrl))
            taskText += $"\nTarget URL: {targetUrl}";

        // Hybrid mode — check for cached plan
        var isHybridMode = cboMode.SelectedIndex == 1;
        SavedPlan? cachedPlan = null;
        _recordingSteps = null;

        if (isHybridMode)
        {
            var allPlans = LoadAllPlans();
            AppendOutput($"\n[HYBRID] {allPlans.Count} saved strategy(s) on disk. Checking for match...\n", Color.FromArgb(255, 200, 50));

            if (allPlans.Count > 0)
            {
                try
                {
                    lblStatus.Text = "Matching task against saved strategies...";
                    cachedPlan = await MatchPlanWithAI(instruction, allPlans, ct);
                }
                catch (Exception ex)
                {
                    AppendOutput($"[HYBRID] Match check failed: {ex.Message}\n", Color.OrangeRed);
                }
            }

            if (cachedPlan != null)
            {
                AppendOutput($"[HYBRID] Matched: \"{cachedPlan.Description}\" ({cachedPlan.Steps.Count} steps)\n", Color.FromArgb(255, 200, 50));
                AppendOutput("[HYBRID] Executing with Gemini Flash...\n", Color.FromArgb(255, 200, 50));
                taskText += "\n\n" + FormatPlanForPrompt(cachedPlan);
            }
            else
            {
                AppendOutput("[HYBRID] No matching strategy — learning with Sonnet (will save on success)\n", Color.FromArgb(255, 200, 50));
                _recordingSteps = new List<RecordedStep>();
            }
        }

        // Initial user message with screenshot + accessibility tree
        messages.Add(new
        {
            role = "user",
            content = new object[]
            {
                new { type = "text", text = taskText },
                new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = state["screenshotBase64"]!.ToString() } },
                new { type = "text", text = FormatPageState(state) },
            }
        });

        // Agentic loop — reset tracking
        _lastActionKey = "";
        _repeatCount = 0;
        _consecutiveFailCount = 0;
        _recentActions = new();
        _lastPageUrl = "";
        _samePageCount = 0;

        // Hybrid mode — set model override
        if (isHybridMode)
        {
            _hybridModelOverride = cachedPlan != null
                ? "gemini-2.5-flash"      // Replay with Flash
                : "claude-sonnet-4-6";    // Learn with Sonnet
        }
        else
        {
            _hybridModelOverride = null;
        }

        for (int step = 1; step <= maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            lblIteration.Text = $"Step {step}/{maxSteps}";
            AppendSection($"Step {step} \u2014 Thinking");

            // Call Claude with tools
            lblStatus.Text = $"Step {step}: Asking AI...";
            var response = await CallAIWithTools(messages, step == maxSteps, ct);

            // Extract text and tool_use from response
            var contentBlocks = response["content"] as JArray;
            if (contentBlocks == null)
            {
                AppendOutput("[ERROR: No content in Claude response]\n", Color.OrangeRed);
                break;
            }

            // Add assistant message to conversation
            messages.Add(new { role = "assistant", content = contentBlocks });

            // Display thinking text
            foreach (var block in contentBlocks)
            {
                if (block["type"]?.ToString() == "text")
                    AppendOutput($"AI: {block["text"]}\n", Color.FromArgb(180, 220, 255));
            }

            // Find tool_use block
            var toolUse = contentBlocks.FirstOrDefault(b => b["type"]?.ToString() == "tool_use");
            if (toolUse == null)
            {
                AppendOutput("[No action returned \u2014 task may be complete]\n", Color.Yellow);
                break;
            }

            var actionName = toolUse["name"]!.ToString();
            var actionInput = toolUse["input"] as JObject ?? new JObject();
            var toolUseId = toolUse["id"]!.ToString();
            var actionKey = $"{actionName}:{actionInput.ToString(Formatting.None)}";

            AppendOutput($"Action: {actionName}", Color.FromArgb(180, 255, 180));
            AppendOutput($" {actionInput.ToString(Formatting.None)}\n", Color.FromArgb(140, 200, 140));

            // Detect repeated actions (exact same)
            if (actionKey == _lastActionKey)
                _repeatCount++;
            else
                _repeatCount = 1;
            _lastActionKey = actionKey;

            if (_repeatCount >= 3)
            {
                AppendOutput($"\n[STOPPED: Same action repeated {_repeatCount} times. AI is stuck.]\n", Color.OrangeRed);
                break;
            }

            // Detect cycling patterns (e.g. trying Search link over and over with slight variations)
            _recentActions.Add(actionKey);
            if (_recentActions.Count > 8)
            {
                // Check if the same action NAME has been attempted 5+ times in last 8 steps
                var recentNames = _recentActions.Skip(_recentActions.Count - 8)
                    .Select(a => a.Split(':')[0]).ToList();
                var mostCommon = recentNames.GroupBy(n => n).OrderByDescending(g => g.Count()).First();
                if (mostCommon.Count() >= 5 && mostCommon.Key != "wait")
                {
                    AppendOutput($"\n[STOPPED: Action '{mostCommon.Key}' attempted {mostCommon.Count()} times in last 8 steps with different params. AI is stuck in a loop.]\n", Color.OrangeRed);
                    break;
                }

                // Check if same target element is being clicked/filled repeatedly (regardless of action name)
                var recentTargets = _recentActions.Skip(_recentActions.Count - 8)
                    .Select(a =>
                    {
                        var colonIdx = a.IndexOf(':');
                        if (colonIdx < 0) return a;
                        try { var j = JObject.Parse(a.Substring(colonIdx + 1)); return j["name"]?.ToString() ?? j["text"]?.ToString() ?? a; }
                        catch { return a; }
                    }).Where(t => !string.IsNullOrEmpty(t)).ToList();
                var topTarget = recentTargets.GroupBy(t => t).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (topTarget != null && topTarget.Count() >= 5)
                {
                    AppendOutput($"\n[STOPPED: Target '{topTarget.Key}' targeted {topTarget.Count()} times in last 8 steps. AI is stuck.]\n", Color.OrangeRed);
                    break;
                }
            }

            // Check for "done" action
            if (actionName == "done")
            {
                var success = actionInput["success"]?.ToObject<bool>() ?? false;
                var message = actionInput["message"]?.ToString() ?? "";
                AppendOutput($"\nTask {(success ? "COMPLETED" : "FAILED")}: {message}\n",
                    success ? Color.LimeGreen : Color.OrangeRed);

                // Save generic plan on success (hybrid learning mode)
                if (success && _recordingSteps != null && _recordingSteps.Count > 0)
                {
                    try
                    {
                        AppendOutput("[HYBRID] Generalizing steps into reusable strategy...\n", Color.FromArgb(255, 200, 50));
                        var genericPlan = await CreateGenericPlan(instruction, _recordingSteps, ct);
                        SavePlan(genericPlan);
                        AppendOutput($"[HYBRID] Strategy saved: \"{genericPlan.Description}\" ({genericPlan.Steps.Count} steps)\n", Color.FromArgb(255, 200, 50));
                    }
                    catch (Exception ex)
                    {
                        AppendOutput($"[HYBRID] Failed to save strategy: {ex.Message}\n", Color.OrangeRed);
                    }
                }

                messages.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "tool_result", tool_use_id = toolUseId, content = "Acknowledged." }
                    }
                });
                break;
            }

            // Execute the action directly via Playwright
            lblStatus.Text = $"Step {step}: Executing {actionName}...";
            AppendOutput($"[Executing: {actionName}...]\n", Color.DimGray);

            var (execOk, execMsg) = await ExecuteAction(actionName, actionInput);
            if (!execOk)
            {
                _consecutiveFailCount++;
                var hint = actionName == "click"
                    ? " Try a DIFFERENT approach: use 'click_text' with visible text, 'press' Enter to submit, or 'click_selector' with a CSS selector."
                    : " Use the accessibility tree to find the correct role and name.";
                if (_consecutiveFailCount >= 3)
                    hint += $" CRITICAL: You have failed {_consecutiveFailCount} times in a row. You MUST try a completely different strategy NOW.";
                execMsg = $"ERROR: {execMsg}.{hint}";
            }
            else
            {
                _consecutiveFailCount = 0;

                // Record step for hybrid plan
                if (_recordingSteps != null)
                {
                    _recordingSteps.Add(new RecordedStep
                    {
                        Action = actionName,
                        Input = (JObject)actionInput.DeepClone(),
                        Result = execMsg,
                        Url = _lastPageUrl,
                    });
                }
            }

            AppendOutput($"Result: {execMsg}\n", execOk ? Color.White : Color.OrangeRed);

            // Wait for page to settle
            await Task.Delay(500, ct);

            // Get new page state
            lblStatus.Text = $"Step {step}: Capturing page state...";
            state = await GetPageState(ct);
            if (state == null)
            {
                AppendOutput("[ERROR: Could not get page state]\n", Color.OrangeRed);
                break;
            }

            // Detect lack of progress
            var currentUrl = state["url"]?.ToString() ?? "";
            if (currentUrl == _lastPageUrl)
                _samePageCount++;
            else
                _samePageCount = 0;
            _lastPageUrl = currentUrl;

            var stuckWarning = "";
            if (_samePageCount >= 5)
                stuckWarning = "\n\nCRITICAL: You have been on the SAME page for 5+ steps and are clearly stuck. You MUST either: (1) try click_text or click_selector with CSS, (2) navigate to a completely different URL, or (3) call 'done' with success=false. Do NOT keep trying the same failing approach.";
            else if (_samePageCount >= 3)
                stuckWarning = "\n\nWARNING: You have been on the same page for multiple steps. Try a DIFFERENT approach: use click_text with visible text, click_selector with a CSS selector, or press keyboard shortcuts. Do not keep retrying failed role-based selectors.";

            // Build user message: tool_result + task reminder + new state
            messages.Add(new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "tool_result", tool_use_id = toolUseId, content = execMsg + stuckWarning },
                    new { type = "text", text = $"TASK REMINDER (use exact values from this): {taskText}\n\nPage state after action:" },
                    new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = state["screenshotBase64"]!.ToString() } },
                    new { type = "text", text = FormatPageState(state) },
                }
            });

            // Trim old screenshots to manage token usage
            TrimOldHistory(messages, 2);
        }

        _hybridModelOverride = null;
        _recordingSteps = null;
        AppendSection("Agent Finished");
        DisconnectPlaywright();
    }

    private static string FormatPageState(JObject state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {state["url"]}");
        sb.AppendLine($"Title: {state["title"]}");
        sb.AppendLine($"{state["scrollInfo"]}");
        sb.AppendLine();
        sb.AppendLine("Page accessibility tree (use role + name to target elements):");
        sb.AppendLine(state["ariaSnapshot"]?.ToString() ?? "(empty)");
        return sb.ToString();
    }

    /// <summary>
    /// Aggressively trim old messages to save tokens:
    /// Remove screenshots and DOM text from all but the last N user messages.
    /// </summary>
    private static void TrimOldHistory(List<object> messages, int keepScreenshots = 2)
    {
        var userMsgIndices = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            var json = JObject.FromObject(messages[i]);
            if (json["role"]?.ToString() != "user") continue;
            var content = json["content"] as JArray;
            if (content != null && content.Count > 0)
                userMsgIndices.Add(i);
        }

        if (userMsgIndices.Count <= 2) return;

        for (int ui = 0; ui < userMsgIndices.Count; ui++)
        {
            var msgIndex = userMsgIndices[ui];
            var isRecent = ui >= userMsgIndices.Count - keepScreenshots;
            if (isRecent) continue;

            var json = JObject.FromObject(messages[msgIndex]);
            var content = json["content"] as JArray;
            if (content == null) continue;

            var filtered = new JArray();
            foreach (var block in content)
            {
                var type = block["type"]?.ToString();
                if (type == "tool_result") { filtered.Add(block); continue; }
                if (type == "image") continue;
                if (type == "text")
                {
                    var text = block["text"]?.ToString() ?? "";
                    if (text.Contains("accessibility tree") || text.Length > 500) continue;
                    filtered.Add(block);
                    continue;
                }
                filtered.Add(block);
            }

            if (filtered.Count == 0)
                filtered.Add(new JObject { ["type"] = "text", ["text"] = "[earlier step \u2014 details trimmed]" });

            messages[msgIndex] = new { role = "user", content = filtered };
        }
    }

    // ==================== Claude API with Tools ====================

    private string BuildSystemPrompt()
    {
        return @"You are a browser automation agent. You see a screenshot and an accessibility tree of the page.
Target elements using their role and name from the accessibility tree (e.g., role=""button"", name=""Compose"").

RULES:
1. If the current URL doesn't match the task, use 'navigate' first.
2. Use 'click' with role+name to click elements. Use 'fill' with role+name to type into inputs.
3. If 'click' fails with timeout, IMMEDIATELY switch to 'click_text' (visible text) or 'press' Enter. Do NOT retry click with different role/name variations — that rarely works.
4. Use 'press' for keyboard shortcuts (Enter, Tab, Escape, Control+a, etc.). After filling a search box, try pressing Enter.
5. Use 'type' to type text into the currently focused element.
6. NEVER repeat a failing action or try slight variations of it. Switch to a completely different approach.
7. When done, call 'done'. If stuck after 3 attempts, call 'done' with success=false.
8. Respond with ONE tool call per step. No explanation needed.
9. Use EXACTLY the data from the user's task. NEVER make up, guess, or modify phone numbers, emails, names, addresses, or any other user-provided data.
10. After a field's value changes, its accessible name may change too. Use the UPDATED accessibility tree.";
    }

    private static List<object> BuildTools()
    {
        return new List<object>
        {
            new {
                name = "click",
                description = "Click an element by its accessible role and name from the accessibility tree.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        role = new { type = "string", description = "ARIA role (button, link, textbox, menuitem, tab, checkbox, etc.)" },
                        name = new { type = "string", description = "Accessible name of the element" },
                        nth = new { type = "integer", description = "0-based index if multiple matches (default: 0)" },
                    },
                    required = new[] { "role", "name" }
                }
            },
            new {
                name = "fill",
                description = "Type text into an input/textarea identified by role+name or label.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        role = new { type = "string", description = "ARIA role (textbox, combobox, searchbox, etc.)" },
                        name = new { type = "string", description = "Accessible name or label of the input" },
                        text = new { type = "string", description = "Text to type" },
                        label = new { type = "string", description = "Alternative: find by label text instead of role+name" },
                        clear = new { type = "boolean", description = "Clear field first (default: true)" },
                        nth = new { type = "integer", description = "0-based index if multiple matches" },
                    },
                    required = new[] { "text" }
                }
            },
            new {
                name = "click_text",
                description = "Click an element by its visible text content. Use when role+name targeting doesn't work.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        text = new { type = "string", description = "Visible text to click" },
                        exact = new { type = "boolean", description = "Exact match (default: false = substring)" },
                    },
                    required = new[] { "text" }
                }
            },
            new {
                name = "navigate",
                description = "Navigate to a URL.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        url = new { type = "string", description = "Full URL" },
                    },
                    required = new[] { "url" }
                }
            },
            new {
                name = "press",
                description = "Press a keyboard key or shortcut.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        key = new { type = "string", description = "Key to press: Enter, Tab, Escape, Control+a, Control+Enter, ArrowDown, etc." },
                    },
                    required = new[] { "key" }
                }
            },
            new {
                name = "type",
                description = "Type text into the currently focused element (character by character for short text, instant paste for long text).",
                input_schema = new {
                    type = "object",
                    properties = new {
                        text = new { type = "string", description = "Text to type" },
                    },
                    required = new[] { "text" }
                }
            },
            new {
                name = "scroll",
                description = "Scroll the page.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        direction = new { type = "string", @enum = new[] { "up", "down" } },
                        pages = new { type = "integer", description = "Viewport-heights to scroll (default: 1)" },
                    },
                    required = new[] { "direction" }
                }
            },
            new {
                name = "select_option",
                description = "Select an option from a dropdown.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        role = new { type = "string", description = "Role of the select element" },
                        name = new { type = "string", description = "Name of the select element" },
                        value = new { type = "string", description = "Option value or text to select" },
                    },
                    required = new[] { "value" }
                }
            },
            new {
                name = "go_back",
                description = "Browser back button.",
                input_schema = new { type = "object", properties = new { } }
            },
            new {
                name = "wait",
                description = "Wait for page to settle.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        ms = new { type = "integer", description = "Milliseconds (default: 2000)" },
                    },
                }
            },
            new {
                name = "done",
                description = "Task complete or cannot be completed.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        success = new { type = "boolean" },
                        message = new { type = "string", description = "What was done or why it failed" },
                    },
                    required = new[] { "success", "message" }
                }
            },
        };
    }

    private async Task<JObject> CallAIWithTools(List<object> messages, bool forceComplete, CancellationToken ct)
    {
        var model = _hybridModelOverride ?? cboModel.SelectedItem?.ToString() ?? "";
        if (model.StartsWith("gemini"))
            return await CallGeminiWithTools(messages, forceComplete, ct, model);
        return await CallClaudeWithTools(messages, forceComplete, ct, model);
    }

    // ==================== Gemini API ====================

    private async Task<JObject> CallGeminiWithTools(List<object> messages, bool forceComplete, CancellationToken ct, string? modelOverride = null)
    {
        var model = modelOverride ?? cboModel.SelectedItem?.ToString() ?? "gemini-2.5-flash";
        var apiKey = txtGeminiKey.Text.Trim();

        // Convert tools to Gemini format
        var geminiTools = BuildGeminiTools(forceComplete);

        // Convert messages to Gemini contents
        var contents = ConvertMessagesToGemini(messages);

        var body = new JObject
        {
            ["system_instruction"] = new JObject
            {
                ["parts"] = new JArray { new JObject { ["text"] = BuildSystemPrompt() } }
            },
            ["contents"] = contents,
            ["tools"] = geminiTools,
            ["toolConfig"] = new JObject
            {
                ["functionCallingConfig"] = new JObject { ["mode"] = "AUTO" }
            },
            ["generationConfig"] = new JObject
            {
                ["maxOutputTokens"] = 8192,
                ["thinkingConfig"] = new JObject
                {
                    ["thinkingBudget"] = 2048,
                },
            },
        };

        var json = body.ToString(Formatting.None);

        // Retry loop for rate limits
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(3);

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.PostAsync(url, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var geminiResponse = JObject.Parse(responseBody);
                return ConvertGeminiResponseToClaude(geminiResponse);
            }

            if ((int)response.StatusCode == 429)
            {
                var waitSeconds = 30 + (attempt * 30);
                AppendOutput($"[Rate limited \u2014 waiting {waitSeconds}s ({attempt + 1}/5)...]\n", Color.Yellow);
                lblStatus.Text = $"Rate limited \u2014 retrying in {waitSeconds}s...";
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                continue;
            }

            var truncatedReq = json.Length > 500 ? json[..500] + "..." : json;
            LogToFile($"\nGemini API Error ({response.StatusCode}):\nRequest: {truncatedReq}\nResponse: {responseBody}\n");
            throw new Exception($"Gemini API error ({response.StatusCode}): {responseBody[..Math.Min(responseBody.Length, 300)]}");
        }

        throw new Exception("Gemini API rate limit: exceeded 5 retries.");
    }

    private JArray ConvertMessagesToGemini(List<object> messages)
    {
        var contents = new JArray();
        var toolNameMap = new Dictionary<string, string>(); // tool_use_id -> tool name

        foreach (var msg in messages)
        {
            var msgJson = JObject.FromObject(msg);
            var role = msgJson["role"]?.ToString();
            var contentArray = msgJson["content"] as JArray;
            if (contentArray == null) continue;

            var geminiRole = role == "assistant" ? "model" : "user";
            var parts = new JArray();

            foreach (var block in contentArray)
            {
                var type = block["type"]?.ToString();
                switch (type)
                {
                    case "text":
                        var text = block["text"]?.ToString();
                        if (!string.IsNullOrEmpty(text))
                            parts.Add(new JObject { ["text"] = text });
                        break;

                    case "image":
                        var mimeType = block["source"]?["media_type"]?.ToString() ?? "image/jpeg";
                        var data = block["source"]?["data"]?.ToString();
                        if (!string.IsNullOrEmpty(data))
                        {
                            parts.Add(new JObject
                            {
                                ["inlineData"] = new JObject
                                {
                                    ["mimeType"] = mimeType,
                                    ["data"] = data
                                }
                            });
                        }
                        break;

                    case "tool_use":
                        var toolName = block["name"]?.ToString();
                        var toolId = block["id"]?.ToString();
                        if (toolId != null && toolName != null)
                            toolNameMap[toolId] = toolName;
                        parts.Add(new JObject
                        {
                            ["functionCall"] = new JObject
                            {
                                ["name"] = toolName,
                                ["args"] = block["input"] ?? new JObject()
                            }
                        });
                        break;

                    case "tool_result":
                        var resultToolId = block["tool_use_id"]?.ToString();
                        var funcName = (resultToolId != null && toolNameMap.ContainsKey(resultToolId))
                            ? toolNameMap[resultToolId]
                            : "unknown";
                        parts.Add(new JObject
                        {
                            ["functionResponse"] = new JObject
                            {
                                ["name"] = funcName,
                                ["response"] = new JObject
                                {
                                    ["result"] = block["content"]?.ToString() ?? ""
                                }
                            }
                        });
                        break;
                }
            }

            if (parts.Count > 0)
                contents.Add(new JObject { ["role"] = geminiRole, ["parts"] = parts });
        }

        return contents;
    }

    private static JObject ConvertGeminiResponseToClaude(JObject geminiResponse)
    {
        var candidate = geminiResponse["candidates"]?[0];
        var parts = candidate?["content"]?["parts"] as JArray;

        var claudeContent = new JArray();

        if (parts != null)
        {
            foreach (var part in parts)
            {
                // Skip thinking/reasoning parts (Gemini 2.5)
                if (part["thought"]?.ToObject<bool>() == true)
                    continue;

                if (part["text"] != null)
                {
                    claudeContent.Add(new JObject
                    {
                        ["type"] = "text",
                        ["text"] = part["text"]
                    });
                }
                else if (part["functionCall"] != null)
                {
                    var fc = part["functionCall"];
                    claudeContent.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = $"gemini_{Guid.NewGuid():N}",
                        ["name"] = fc!["name"],
                        ["input"] = fc["args"] ?? new JObject()
                    });
                }
            }
        }

        if (claudeContent.Count == 0)
        {
            // Gemini returned nothing useful — check for errors
            var finishReason = candidate?["finishReason"]?.ToString() ?? "UNKNOWN";
            claudeContent.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = $"[Gemini returned no action. Finish reason: {finishReason}]"
            });
        }

        return new JObject { ["content"] = claudeContent };
    }

    private JArray BuildGeminiTools(bool forceComplete)
    {
        var claudeTools = BuildTools();
        if (forceComplete)
        {
            claudeTools = claudeTools.Where(t =>
            {
                var j = JObject.FromObject(t);
                return j["name"]?.ToString() == "done";
            }).ToList();
        }

        var declarations = new JArray();
        foreach (var tool in claudeTools)
        {
            var j = JObject.FromObject(tool);
            var decl = new JObject
            {
                ["name"] = j["name"],
                ["description"] = j["description"],
                ["parameters"] = ConvertSchemaToGemini(j["input_schema"] as JObject)
            };
            declarations.Add(decl);
        }

        return new JArray { new JObject { ["functionDeclarations"] = declarations } };
    }

    private static JObject? ConvertSchemaToGemini(JObject? schema)
    {
        if (schema == null) return null;

        var result = new JObject();
        foreach (var prop in schema.Properties())
        {
            switch (prop.Name)
            {
                case "type":
                    result["type"] = prop.Value.ToString().ToUpperInvariant();
                    break;
                case "properties" when prop.Value is JObject props:
                    var geminiProps = new JObject();
                    foreach (var p in props.Properties())
                        geminiProps[p.Name] = ConvertSchemaToGemini(p.Value as JObject);
                    result["properties"] = geminiProps;
                    break;
                default:
                    result[prop.Name] = prop.Value.DeepClone();
                    break;
            }
        }
        return result;
    }

    // ==================== Claude API with Tools ====================

    private async Task<JObject> CallClaudeWithTools(List<object> messages, bool forceComplete, CancellationToken ct, string? modelOverride = null)
    {
        var model = modelOverride ?? cboModel.SelectedItem?.ToString() ?? "claude-sonnet-4-6";
        var tools = BuildTools();

        if (forceComplete)
        {
            tools = tools.Where(t =>
            {
                var j = JObject.FromObject(t);
                return j["name"]?.ToString() == "done";
            }).ToList();
        }

        var body = new
        {
            model,
            max_tokens = 1024,
            system = BuildSystemPrompt(),
            tools,
            tool_choice = new { type = "auto" },
            messages,
        };

        var json = JsonConvert.SerializeObject(body, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });

        // Retry loop for rate limits
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.Add("x-api-key", txtApiKey.Text.Trim());
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.PostAsync("https://api.anthropic.com/v1/messages", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return JObject.Parse(responseBody);

            if ((int)response.StatusCode == 429)
            {
                var waitSeconds = 30 + (attempt * 30);
                var retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter.HasValue)
                    waitSeconds = (int)retryAfter.Value.TotalSeconds + 5;

                AppendOutput($"[Rate limited \u2014 waiting {waitSeconds}s before retry ({attempt + 1}/5)...]\n", Color.Yellow);
                lblStatus.Text = $"Rate limited \u2014 retrying in {waitSeconds}s...";

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                continue;
            }

            var truncatedReq = json.Length > 500 ? json[..500] + "..." : json;
            LogToFile($"\nAPI Error ({response.StatusCode}):\nRequest: {truncatedReq}\nResponse: {responseBody}\n");
            throw new Exception($"Claude API error ({response.StatusCode}): {responseBody[..Math.Min(responseBody.Length, 300)]}");
        }

        throw new Exception("Claude API rate limit: exceeded 5 retries. Try again in a few minutes or upgrade your API plan.");
    }
}
