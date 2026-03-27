using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgenticBrowser;

public class AutomationEngine : IDisposable
{
    private readonly EngineSettings _settings;
    private CancellationTokenSource? _cts;

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
    private readonly List<string> _recentFailedActions = new();
    private readonly List<string> _recentActions = new();

    // Hybrid mode
    private static readonly string PlansDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgenticBrowser", "plans");
    private List<RecordedStep>? _recordingSteps;
    private string? _hybridModelOverride;

    // Events
    public event Action<string, string>? OnLogMessage;        // (message, colorHex)
    public event Action<byte[]>? OnScreenshotCaptured;
    public event Action<int, int, string>? OnStepStarted;     // (stepNum, maxSteps, actionName)
    public event Action<int, bool, string>? OnStepCompleted;  // (stepNum, success, result)
    public event Action<bool, string>? OnTaskCompleted;       // (success, message)
    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnRunningChanged;

    private class RecordedStep
    {
        public string Action { get; set; } = "";
        public JObject Input { get; set; } = new();
        public string Result { get; set; } = "";
        public string Url { get; set; } = "";
        public string AiReasoning { get; set; } = ""; // Why the AI chose this action
    }

    private class SavedPlan
    {
        public string Description { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string SourceTask { get; set; } = "";
        public List<string> Steps { get; set; } = new();
        // NEW: concrete action recordings for each step, so Flash knows exactly what tool+params to use
        public List<ActionHint>? ActionHints { get; set; }
    }

    private class ActionHint
    {
        public string Tool { get; set; } = "";
        public string ParamsJson { get; set; } = "{}";
        public string Description { get; set; } = ""; // what this action did
    }

    public AutomationEngine(EngineSettings settings)
    {
        _settings = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"\n=== Engine started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
        catch { }
    }

    public void UpdateSettings(EngineSettings settings)
    {
        settings.ClaudeApiKey = _settings.ClaudeApiKey.Length > 0 ? settings.ClaudeApiKey : _settings.ClaudeApiKey;
        // Copy all relevant fields
        _settings.ClaudeApiKey = settings.ClaudeApiKey;
        _settings.GeminiApiKey = settings.GeminiApiKey;
        _settings.SelectedModel = settings.SelectedModel;
        _settings.ModeIndex = settings.ModeIndex;
        _settings.Port = settings.Port;
        _settings.MaxSteps = settings.MaxSteps;
        _settings.TargetUrl = settings.TargetUrl;
        _settings.Headless = settings.Headless;
        _settings.Instruction = settings.Instruction;
    }

    // ==================== Public Methods ====================

    public async Task ExecuteTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.Instruction))
            throw new InvalidOperationException("Please enter instructions.");

        var isHybrid = _settings.ModeIndex == 1;
        var selectedModel = _settings.SelectedModel;

        if (isHybrid)
        {
            if (string.IsNullOrWhiteSpace(_settings.ClaudeApiKey))
                throw new InvalidOperationException("Hybrid mode requires a Claude API key (for learning).");
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
                throw new InvalidOperationException("Hybrid mode requires a Gemini API key (for replay).");
        }
        else if (selectedModel.StartsWith("gemini"))
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
                throw new InvalidOperationException("Please enter your Google Gemini API key.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_settings.ClaudeApiKey))
                throw new InvalidOperationException("Please enter your Anthropic API key.");
        }

        if (!await IsChromeDebugPortOpen(_settings.Port))
        {
            Log("[Chrome not running — launching automatically...]\n", "#B4DCFF");
            await LaunchChromeAsync();
            if (!await IsChromeDebugPortOpen(_settings.Port))
                throw new InvalidOperationException($"Chrome debug port {_settings.Port} is not active after auto-launch.");
        }

        _cts = new CancellationTokenSource();
        OnRunningChanged?.Invoke(true);

        try
        {
            await RunAgenticLoop(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("\nStopped by user.\n", "#FFFF00");
        }
        catch (Exception ex)
        {
            Log($"\nError: {(ex.Message.Length > 150 ? ex.Message[..150] + "..." : ex.Message)}\n", "#FFA07A");
            LogToFile($"\nException: {ex}\n");
        }
        finally
        {
            OnRunningChanged?.Invoke(false);
            OnStatusChanged?.Invoke("Ready");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        OnStatusChanged?.Invoke("Stopping...");
    }

    public async Task LaunchChromeAsync()
    {
        var chromePath = FindChromeExecutable();
        if (chromePath == null)
            throw new InvalidOperationException("Could not find Chrome. Please install Google Chrome.");

        var port = _settings.Port;
        if (await IsChromeDebugPortOpen(port))
        {
            Log($"\n[Chrome already running on port {port} \u2014 ready!]\n", "#32CD32");
            OnStatusChanged?.Invoke($"Chrome on port {port} \u2014 ready");
            return;
        }

        await LaunchChromeDedicatedProfile(chromePath, port);
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

    private async Task<SavedPlan> CreateGenericPlan(string taskText, List<RecordedStep> steps, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A browser automation task completed successfully. Create a GENERIC reusable workflow strategy.");
        sb.AppendLine("Remove ALL specific data (names, phone numbers, emails, text content).");
        sb.AppendLine("Keep only the workflow pattern so it can be reused with DIFFERENT data.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL REQUIREMENTS:");
        sb.AppendLine("1. Include DECISION POINTS — steps where the AI checked something and made a decision based on what it saw.");
        sb.AppendLine("   Example: 'CHECK the applications page — if applications exist, click on the first one. If no applications, go back to candidate.'");
        sb.AppendLine("2. Include VERIFICATION steps — where the AI should check the screenshot to confirm an action worked.");
        sb.AppendLine("   Example: 'VERIFY the note was saved by checking the notes count increased.'");
        sb.AppendLine("3. Remove trial-and-error, retries, and backtracking. Keep only the OPTIMAL path.");
        sb.AppendLine("4. Steps that inspect/check page content should say 'CHECK screenshot for...' so the replaying AI knows to look.");
        sb.AppendLine();
        sb.AppendLine($"Original task: {taskText}");
        sb.AppendLine("\nRecorded actions with AI reasoning:");
        for (int i = 0; i < steps.Count; i++)
        {
            sb.AppendLine($"  {i}: [{steps[i].Action}] {steps[i].Result} (on {steps[i].Url})");
            if (!string.IsNullOrEmpty(steps[i].AiReasoning))
                sb.AppendLine($"     REASONING: {steps[i].AiReasoning[..Math.Min(steps[i].AiReasoning.Length, 200)]}");
        }
        sb.AppendLine();
        sb.AppendLine("Reply in this EXACT format (no markdown, no extra text):");
        sb.AppendLine("DESCRIPTION: <one-line workflow description>");
        sb.AppendLine("STEPS:");
        sb.AppendLine("1. <generic step with decision logic if applicable> ACTION_INDEX=<index of recorded action>");
        sb.AppendLine("2. CHECK <what to look for in screenshot> — IF <condition> THEN <action> ELSE <alternative> ACTION_INDEX=<index>");
        sb.AppendLine("3. <generic step> ACTION_INDEX=<index>");
        sb.AppendLine("...");
        sb.AppendLine();
        sb.AppendLine("Each step must reference exactly ONE recorded action index. Include CHECK/VERIFY steps for decisions.");

        var response = await AskClaude(sb.ToString(), ct);
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var description = "";
        var genericSteps = new List<string>();
        var selectedIndices = new List<int>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DESCRIPTION:", StringComparison.OrdinalIgnoreCase))
            {
                description = trimmed[12..].Trim();
            }
            else if (trimmed.Length >= 3 && char.IsDigit(trimmed[0]) && trimmed.Contains('.'))
            {
                var dotIdx = trimmed.IndexOf('.');
                if (dotIdx > 0 && dotIdx < 4)
                {
                    var stepText = trimmed[(dotIdx + 1)..].Trim();
                    // Extract ACTION_INDEX=N
                    var idxMatch = System.Text.RegularExpressions.Regex.Match(stepText, @"ACTION_INDEX=(\d+)");
                    if (idxMatch.Success && int.TryParse(idxMatch.Groups[1].Value, out int actionIdx))
                    {
                        selectedIndices.Add(actionIdx);
                        // Remove the ACTION_INDEX from the step text
                        stepText = stepText.Replace(idxMatch.Value, "").Trim().TrimEnd(',', '.').Trim();
                    }
                    genericSteps.Add(stepText);
                }
            }
        }

        // Build action hints from ONLY the selected steps (the optimal path)
        var hints = new List<ActionHint>();
        if (selectedIndices.Count > 0)
        {
            foreach (var idx in selectedIndices)
            {
                if (idx >= 0 && idx < steps.Count)
                {
                    hints.Add(new ActionHint
                    {
                        Tool = steps[idx].Action,
                        ParamsJson = steps[idx].Input.ToString(Formatting.None),
                        Description = steps[idx].Result,
                    });
                }
            }
        }

        // Fallback: if AI didn't return indices, deduplicate manually
        if (hints.Count == 0)
        {
            var seen = new HashSet<string>();
            foreach (var s in steps)
            {
                // Skip go_back and wait actions
                if (s.Action == "go_back" || s.Action == "wait") continue;
                // Deduplicate by action+key params
                var key = $"{s.Action}:{s.Input["role"] ?? s.Input["selector"] ?? ""}{s.Input["name"] ?? ""}";
                if (seen.Add(key))
                {
                    hints.Add(new ActionHint
                    {
                        Tool = s.Action,
                        ParamsJson = s.Input.ToString(Formatting.None),
                        Description = s.Result,
                    });
                }
            }
        }

        Log($"[HYBRID] Plan distilled: {steps.Count} recorded actions → {hints.Count} essential steps\n", "#FFC832");

        return new SavedPlan
        {
            Description = description.Length > 0 ? description : "Unnamed workflow",
            CreatedAt = DateTime.UtcNow,
            SourceTask = taskText,
            Steps = genericSteps,
            ActionHints = hints,
        };
    }

    private async Task<string> AskClaude(string prompt, CancellationToken ct)
    {
        var body = new JObject
        {
            ["model"] = "claude-sonnet-4-6",
            ["max_tokens"] = 1000,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "user", ["content"] = prompt }
            }
        };

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("x-api-key", _settings.ClaudeApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("https://api.anthropic.com/v1/messages", content, ct);
        var respBody = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
        return respBody["content"]?[0]?["text"]?.ToString() ?? "";
    }

    private static string FormatPlanForPrompt(SavedPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROVEN STRATEGY (follow these steps — adapt based on what you see):");
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            sb.AppendLine($"  Step {i + 1}: {plan.Steps[i]}");
            // Add concrete action hint if available
            if (plan.ActionHints != null && i < plan.ActionHints.Count)
            {
                var hint = plan.ActionHints[i];
                // Strip specific data values from params but keep the structure/selectors
                var genericParams = GenericizeParams(hint.ParamsJson);
                sb.AppendLine($"    → ACTION: {hint.Tool} {genericParams}");
            }
        }
        sb.AppendLine("\nIMPORTANT:");
        sb.AppendLine("- Follow this strategy step by step. Execute ONE action per turn.");
        sb.AppendLine("- Use the EXACT data from the TASK above (names, phone numbers, text) — NOT from the strategy.");
        sb.AppendLine("- The ACTION hints show which tool and selector pattern worked before. Use the SAME tool and selector structure.");
        sb.AppendLine("- Steps that say CHECK or VERIFY: LOOK at the screenshot carefully before proceeding. Make decisions based on what you actually SEE.");
        sb.AppendLine("- IF/THEN steps: Follow the condition — don't blindly follow the action if the condition doesn't match.");
        sb.AppendLine("- If a 'click' action hint shows role+name, try that first. If it fails, IMMEDIATELY try click_text or click_selector.");
        sb.AppendLine("- When all steps are done, call 'done' with success=true.");
        return sb.ToString();
    }

    /// <summary>
    /// Strip specific user data (phone numbers, names, long text) from action params
    /// but keep structural info (roles, selectors, field names).
    /// </summary>
    private static string GenericizeParams(string paramsJson)
    {
        try
        {
            var obj = JObject.Parse(paramsJson);
            // Replace long text values with placeholders, keep short structural values
            foreach (var prop in obj.Properties().ToList())
            {
                var val = prop.Value.ToString();
                if (prop.Name == "text" || prop.Name == "value")
                    obj[prop.Name] = "<FROM_TASK>";
                else if (val.Length > 60)
                    obj[prop.Name] = "<FROM_TASK>";
            }
            return obj.ToString(Formatting.None);
        }
        catch { return paramsJson; }
    }

    // ==================== Logging ====================

    private void Log(string text, string colorHex)
    {
        OnLogMessage?.Invoke(text, colorHex);
        LogToFile(text);
    }

    private void LogSection(string title)
    {
        Log($"\n{"".PadRight(60, '\u2500')}\n  {title}\n{"".PadRight(60, '\u2500')}\n", "#00FFFF");
    }

    private void LogToFile(string text)
    {
        try { File.AppendAllText(LogFile, text); } catch { }
    }

    // ==================== Chrome Launch ====================

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
            if (_settings.Headless)
                args += " --headless=new --disable-gpu";

            Log($"\n[Launching Chrome on port {port}{(_settings.Headless ? " (headless)" : "")}...]\n", "#B4DCFF");
            Log($"[Profile: {profileDir}]\n", "#696969");

            var psi = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = args,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            Log($"[Chrome PID: {proc?.Id}]\n", "#696969");

            OnStatusChanged?.Invoke("Waiting for Chrome debug port...");
            var ready = await WaitForDebugPort(port, timeoutSeconds: 20);
            if (ready)
            {
                Log($"[Chrome debug port {port} READY!]\n", "#32CD32");
                if (isFirstLaunch && !_settings.Headless)
                    Log("[FIRST TIME: Log into your accounts in this Chrome window.]\n", "#FFA500");
                else
                    Log("[Ready to Execute!]\n", "#B4DCFF");
                OnStatusChanged?.Invoke($"Chrome on port {port} \u2014 ready");
            }
            else
            {
                Log($"[Chrome port {port} not responding after 20s]\n", "#FFA500");
                OnStatusChanged?.Invoke("Chrome not responding");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch Chrome: {ex.Message}");
        }
    }

    private async Task<bool> WaitForDebugPort(int port, int timeoutSeconds = 20)
    {
        for (int i = 0; i < timeoutSeconds * 2; i++)
        {
            if (await IsChromeDebugPortOpen(port)) return true;
            await Task.Delay(500);
            if (i % 4 == 3)
                Log($"[Waiting for port {port}... ({(i + 1) / 2}s)]\n", "#696969");
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
            Log("[Playwright engine created]\n", "#696969");

            _browser = await _playwright.Chromium.ConnectOverCDPAsync(
                $"http://127.0.0.1:{port}",
                new BrowserTypeConnectOverCDPOptions { Timeout = 15000 });

            _context = _browser.Contexts.Count > 0 ? _browser.Contexts[0] : null;
            if (_context == null)
            {
                Log("[No browser context found]\n", "#FFA500");
                return false;
            }

            var pages = _context.Pages;
            _page = pages.FirstOrDefault(p =>
            {
                try { return p.Url != "about:blank" && p.Url != "chrome://newtab/"; }
                catch { return false; }
            }) ?? pages.FirstOrDefault();

            if (_page == null)
                _page = await _context.NewPageAsync();

            _context.Page += (_, newPage) =>
            {
                Log($"[New page opened: {newPage.Url}]\n", "#696969");
                _page = newPage;
            };

            var title = "";
            try { title = await _page.TitleAsync(); } catch { title = "(unknown)"; }
            Log($"[Connected! Page: {_page.Url} | Title: {title} | {pages.Count} tabs]\n", "#32CD32");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[Connect error: {(ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message)}]\n", "#FFA500");
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
            var pages = _context.Pages;
            if (pages.Count > 0)
            {
                var activePage = pages.FirstOrDefault(p => p.Url != "about:blank" && p.Url != "chrome://newtab/");
                if (activePage != null && activePage != _page)
                {
                    Log($"[Switching to active page: {activePage.Url}]\n", "#696969");
                    _page = activePage;
                }
            }

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 15000 });
            }
            catch (Exception e)
            {
                Log($"[waitForLoadState timed out, continuing: {e.Message}]\n", "#696969");
            }

            await Task.Delay(1000, ct);

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
                    Log($"[Screenshot attempt {attempt + 1} failed: {e.Message}]\n", "#696969");
                    if (attempt == 2) throw;
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
            Log($"[Screenshot size: {screenshotBytes.Length / 1024}KB]\n", "#696969");

            OnScreenshotCaptured?.Invoke(screenshotBytes);

            var screenshotBase64 = Convert.ToBase64String(screenshotBytes);

            string ariaSnapshot = "";
            try
            {
                ariaSnapshot = await _page.Locator("body").First.AriaSnapshotAsync(new() { Timeout = 10000 });
            }
            catch (Exception e)
            {
                Log($"[ariaSnapshot failed: {e.Message}, falling back to manual extraction]\n", "#696969");
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
                    Log($"[Manual DOM extraction also failed: {e2.Message}]\n", "#696969");
                    ariaSnapshot = "(Could not extract page structure)";
                }
            }

            var url = _page.Url;
            var title = await _page.TitleAsync();

            var scrollInfo = await _page.EvaluateAsync<string>(@"() => {
                const top = window.scrollY;
                const totalHeight = document.documentElement.scrollHeight;
                const viewHeight = window.innerHeight;
                const pageNum = Math.round(top / viewHeight) + 1;
                const totalPages = Math.max(1, Math.round(totalHeight / viewHeight));
                return `Scroll: page ${pageNum} of ${totalPages}`;
            }");

            // Extract clickable elements with CSS selectors (catches elements missing from ARIA tree)
            string clickableElements = "";
            try
            {
                clickableElements = await _page.EvaluateAsync<string>(@"() => {
                    const results = [];
                    const els = document.querySelectorAll('a, button, input, select, textarea, [role=button], [role=link], [role=textbox], [role=tab], [role=menuitem], [onclick], [data-action], [class*=btn], [class*=icon], [class*=search], [class*=filter]');
                    const seen = new Set();
                    els.forEach(el => {
                        if (seen.has(el)) return;
                        seen.add(el);
                        const rect = el.getBoundingClientRect();
                        if (rect.width < 5 || rect.height < 5) return;
                        if (rect.bottom < 0 || rect.top > window.innerHeight + 100) return;
                        if (results.length >= 150) return;

                        const tag = el.tagName.toLowerCase();
                        const id = el.id;
                        const cls = Array.from(el.classList).join('.');
                        let selector = tag;
                        if (id) selector = '#' + id;
                        else if (cls) selector = tag + '.' + cls.substring(0, 80);

                        const name = el.getAttribute('aria-label') || el.getAttribute('title') || el.placeholder || (el.innerText || '').trim().substring(0, 40) || '';
                        const type = el.type || '';
                        const role = el.getAttribute('role') || '';

                        let line = `[${selector}]`;
                        if (role) line += ` role=${role}`;
                        if (type) line += ` type=${type}`;
                        if (name) line += ` ""${name}""`;
                        else line += ' (unnamed)';
                        line += ` @${Math.round(rect.x)},${Math.round(rect.y)} ${Math.round(rect.width)}x${Math.round(rect.height)}`;

                        results.push(line);
                    });
                    return results.join('\n');
                }") ?? "";
            }
            catch (Exception e3)
            {
                Log($"[Clickable extraction failed: {e3.Message}]\n", "#696969");
            }

            // Smart snapshot truncation: keep interactive elements, drop deep nesting
            const int maxSnapshotChars = 8000;
            var snapshotLen = ariaSnapshot?.Length ?? 0;
            if (snapshotLen > maxSnapshotChars)
            {
                ariaSnapshot = SmartTruncateSnapshot(ariaSnapshot!, maxSnapshotChars);
                Log($"[Snapshot smart-truncated: {snapshotLen} → {ariaSnapshot.Length} chars]\n", "#FFA500");
            }
            Log($"[Page: {url} | snapshot: {snapshotLen} chars | clickable: {clickableElements.Length} chars | {scrollInfo}]\n", "#696969");

            return new JObject
            {
                ["url"] = url,
                ["title"] = title,
                ["ariaSnapshot"] = ariaSnapshot,
                ["clickableElements"] = clickableElements,
                ["screenshotBase64"] = screenshotBase64,
                ["scrollInfo"] = scrollInfo,
            };
        }
        catch (Exception ex)
        {
            Log($"[Page state error: {(ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message)}]\n", "#696969");
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
                    try
                    {
                        try { await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 3000 }); } catch { }
                        await locator.ClickAsync(new() { Timeout = 5000 });
                        await _page.WaitForTimeoutAsync(1000);
                        return (true, $"Clicked {input["role"] ?? "element"} \"{input["name"] ?? ""}\"");
                    }
                    catch (Exception clickEx) when (clickEx.Message.Contains("Timeout") || clickEx.Message.Contains("waiting for") || clickEx.Message.Contains("strict mode"))
                    {
                        // Auto-fallback: the element wasn't found by role+name.
                        // Use universal JS search across ALL HTML attributes.
                        var name = input["name"]?.ToString() ?? "";
                        Log($"[Fallback: trying alternatives for \"{name}\"...]\n", "#696969");

                        if (!string.IsNullOrEmpty(name))
                        {
                            // Universal JS-based element finder: searches id, class, aria-label, title, placeholder, text, data-* attributes
                            try
                            {
                                var clicked = await _page.EvaluateAsync<bool>(@"(keyword) => {
                                    const kw = keyword.toLowerCase().replace(/\s+/g, '');
                                    const candidates = [];

                                    // Search ALL visible interactive elements
                                    const allEls = document.querySelectorAll('a, button, input, select, textarea, img, span, div, li, svg, i, [role], [onclick], [data-action], [tabindex]');
                                    for (const el of allEls) {
                                        const rect = el.getBoundingClientRect();
                                        if (rect.width < 3 || rect.height < 3) continue;
                                        if (rect.bottom < 0 || rect.top > window.innerHeight + 50) continue;

                                        // Check ALL attributes for keyword match
                                        const id = (el.id || '').toLowerCase();
                                        const cls = (el.className || '').toString().toLowerCase();
                                        const ariaLabel = (el.getAttribute('aria-label') || '').toLowerCase();
                                        const title = (el.getAttribute('title') || '').toLowerCase();
                                        const placeholder = (el.getAttribute('placeholder') || '').toLowerCase();
                                        const text = (el.innerText || '').trim().toLowerCase().substring(0, 100);
                                        const dataAction = (el.getAttribute('data-action') || '').toLowerCase();
                                        const role = (el.getAttribute('role') || '').toLowerCase();
                                        const name = (el.getAttribute('name') || '').toLowerCase();

                                        const allAttrs = id + ' ' + cls + ' ' + ariaLabel + ' ' + title + ' ' + placeholder + ' ' + text + ' ' + dataAction + ' ' + role + ' ' + name;

                                        if (allAttrs.includes(kw) || allAttrs.replace(/[\s\-_]+/g, '').includes(kw)) {
                                            let score = 0;
                                            if (id.includes(kw)) score += 10;
                                            if (ariaLabel.includes(kw)) score += 8;
                                            if (title.includes(kw)) score += 7;
                                            if (text === kw || text === keyword.toLowerCase()) score += 9;
                                            if (cls.includes(kw)) score += 5;
                                            if (placeholder.includes(kw)) score += 6;
                                            // Prefer smaller/more specific elements
                                            if (rect.width < 100 && rect.height < 100) score += 3;
                                            // Prefer interactive tags
                                            const tag = el.tagName.toLowerCase();
                                            if (['a','button','input'].includes(tag)) score += 4;
                                            candidates.push({ el, score });
                                        }
                                    }

                                    if (candidates.length === 0) return false;
                                    candidates.sort((a, b) => b.score - a.score);
                                    const best = candidates[0].el;
                                    best.scrollIntoView({ block: 'center' });
                                    best.click();
                                    return true;
                                }", name);

                                if (clicked)
                                {
                                    await _page.WaitForTimeoutAsync(1000);
                                    Log($"[Fallback: clicked element matching \"{name}\"]\n", "#696969");
                                    return (true, $"Clicked element matching \"{name}\" (via JS search)");
                                }
                            }
                            catch { }

                            // Try click by visible text as last resort
                            try
                            {
                                await _page.GetByText(name, new() { Exact = false }).First.ClickAsync(new() { Timeout = 3000 });
                                await _page.WaitForTimeoutAsync(1000);
                                return (true, $"Clicked text \"{name}\"");
                            }
                            catch { }
                        }
                        throw; // re-throw if all fallbacks failed
                    }
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

                case "fill_selector":
                {
                    var sel = input["selector"]!.ToString();
                    var text = input["text"]!.ToString();
                    try
                    {
                        await _page.Locator(sel).First.FillAsync(text, new() { Timeout = 5000 });
                    }
                    catch
                    {
                        await _page.Locator(sel).First.ClickAsync(new() { Timeout = 3000 });
                        await _page.Keyboard.PressAsync("Control+a");
                        await _page.Keyboard.PressAsync("Delete");
                        await _page.Keyboard.TypeAsync(text, new() { Delay = 30 });
                    }
                    await _page.WaitForTimeoutAsync(500);
                    return (true, $"Filled \"{text[..Math.Min(text.Length, 30)]}\" into {sel}");
                }

                case "get_text":
                {
                    var sel = input["selector"]?.ToString() ?? "body";
                    var maxLen = input["maxLength"]?.ToObject<int>() ?? 2000;
                    var text = await _page.Locator(sel).First.InnerTextAsync(new() { Timeout = 5000 });
                    if (text.Length > maxLen) text = text[..maxLen] + "...";
                    return (true, $"Text content:\n{text}");
                }

                default:
                    return (false, $"Unknown action type: {actionType}");
            }
        }
        catch (Exception e)
        {
            // Extract a short, clean error message (strip Playwright verbose stack traces)
            var msg = e.Message;
            var newlineIdx = msg.IndexOf('\n');
            if (newlineIdx > 0) msg = msg[..newlineIdx];
            // Strip "Timeout 5000ms exceeded" verbosity
            var callLogIdx = msg.IndexOf("Call log:", StringComparison.OrdinalIgnoreCase);
            if (callLogIdx > 0) msg = msg[..callLogIdx].TrimEnd();
            if (msg.Length > 150) msg = msg[..150] + "...";
            return (false, msg);
        }
    }

    // ==================== Agentic Loop ====================

    private async Task RunAgenticLoop(CancellationToken ct)
    {
        var port = _settings.Port;
        var maxSteps = _settings.MaxSteps;

        LogSection("Connecting to Chrome");
        DisconnectPlaywright();
        if (!await ConnectPlaywright(port))
            return;

        var targetUrl = _settings.TargetUrl;
        if (!string.IsNullOrEmpty(targetUrl) && _page != null)
        {
            Log($"[Navigating to {targetUrl}...]\n", "#B4DCFF");
            try
            {
                await _page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                await _page.WaitForTimeoutAsync(2000);
            }
            catch (Exception ex)
            {
                Log($"[Navigation warning: {ex.Message}]\n", "#FFA500");
            }
        }

        LogSection("Getting Initial Page State");
        var state = await GetPageState(ct);
        if (state == null) return;

        var messages = new List<object>();
        var instruction = _settings.Instruction;
        var taskText = $"Task: {instruction}";
        if (!string.IsNullOrEmpty(targetUrl))
            taskText += $"\nTarget URL: {targetUrl}";

        var isHybridMode = _settings.ModeIndex == 1;
        SavedPlan? cachedPlan = null;
        _recordingSteps = null;

        if (isHybridMode)
        {
            var allPlans = LoadAllPlans();
            Log($"\n[HYBRID] {allPlans.Count} saved strategy(s) on disk. Checking for match...\n", "#FFC832");

            if (allPlans.Count > 0)
            {
                try
                {
                    OnStatusChanged?.Invoke("Matching task against saved strategies...");
                    cachedPlan = await MatchPlanWithAI(instruction, allPlans, ct);
                }
                catch (Exception ex)
                {
                    Log($"[HYBRID] Match check failed: {ex.Message}\n", "#696969");
                }
            }

            if (cachedPlan != null)
            {
                Log($"[HYBRID] Matched: \"{cachedPlan.Description}\" ({cachedPlan.Steps.Count} steps)\n", "#FFC832");
                Log("[HYBRID] Executing with Gemini Flash...\n", "#FFC832");
                taskText += "\n\n" + FormatPlanForPrompt(cachedPlan);
            }
            else
            {
                Log("[HYBRID] No matching strategy — learning with Sonnet (will save on success)\n", "#FFC832");
                _recordingSteps = new List<RecordedStep>();
            }
        }

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

        _lastActionKey = "";
        _repeatCount = 0;
        _consecutiveFailCount = 0;
        _lastPageUrl = "";
        _samePageCount = 0;
        _recentFailedActions.Clear();
        _recentActions.Clear();

        if (isHybridMode)
        {
            _hybridModelOverride = cachedPlan != null
                ? "gemini-2.5-flash"
                : "claude-sonnet-4-6";
        }
        else
        {
            _hybridModelOverride = null;
        }

        for (int step = 1; step <= maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            OnStepStarted?.Invoke(step, maxSteps, "thinking");
            LogSection($"Step {step} \u2014 Thinking");

            OnStatusChanged?.Invoke($"Step {step}: Asking AI...");
            var response = await CallAIWithTools(messages, step == maxSteps, ct);

            var contentBlocks = response["content"] as JArray;
            if (contentBlocks == null)
            {
                Log("[No content in AI response]\n", "#FFA500");
                break;
            }

            messages.Add(new { role = "assistant", content = contentBlocks });

            // Capture AI reasoning text (for plan recording)
            var aiReasoning = "";
            foreach (var block in contentBlocks)
            {
                if (block["type"]?.ToString() == "text")
                {
                    var txt = block["text"]?.ToString() ?? "";
                    Log($"AI: {txt}\n", "#B4DCFF");
                    aiReasoning += txt + " ";
                }
            }
            aiReasoning = aiReasoning.Trim();

            var toolUse = contentBlocks.FirstOrDefault(b => b["type"]?.ToString() == "tool_use");
            if (toolUse == null)
            {
                Log("[No action returned \u2014 task may be complete]\n", "#FFFF00");
                break;
            }

            var actionName = toolUse["name"]!.ToString();
            var actionInput = toolUse["input"] as JObject ?? new JObject();
            var toolUseId = toolUse["id"]!.ToString();
            var actionKey = $"{actionName}:{actionInput.ToString(Formatting.None)}";

            OnStepStarted?.Invoke(step, maxSteps, actionName);
            Log($"Action: {actionName}", "#B4FFB4");
            Log($" {actionInput.ToString(Formatting.None)}\n", "#8CC88C");

            if (actionKey == _lastActionKey)
                _repeatCount++;
            else
                _repeatCount = 1;
            _lastActionKey = actionKey;

            if (_repeatCount >= 3)
            {
                Log($"\n[Stopped: same action repeated {_repeatCount}x]\n", "#FFA500");
                OnStepCompleted?.Invoke(step, false, "Stuck in loop");
                break;
            }

            // Detect cycling patterns (same action name or target with slight variations)
            _recentActions.Add(actionKey);
            if (_recentActions.Count >= 8)
            {
                var last8 = _recentActions.Skip(_recentActions.Count - 8).ToList();
                var actionNames = last8.Select(a => a.Split(':')[0]).ToList();
                var topAction = actionNames.GroupBy(n => n).OrderByDescending(g => g.Count()).First();
                if (topAction.Count() >= 6 && topAction.Key != "wait" && topAction.Key != "done")
                {
                    Log($"\n[Stopped: '{topAction.Key}' cycling {topAction.Count()}x in 8 steps]\n", "#FFA500");
                    OnTaskCompleted?.Invoke(false, $"Stuck cycling on '{topAction.Key}'");
                    break;
                }

                // Check for navigation cycling
                var last6 = _recentActions.Skip(_recentActions.Count - 6).ToList();
                var targets = last6.Select(a =>
                {
                    var ci = a.IndexOf(':');
                    if (ci < 0) return a;
                    try { var j = JObject.Parse(a[(ci + 1)..]); return j["name"]?.ToString() ?? j["url"]?.ToString() ?? ""; }
                    catch { return ""; }
                }).Where(t => !string.IsNullOrEmpty(t)).ToList();
                var topTarget = targets.GroupBy(t => t).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (topTarget != null && topTarget.Count() >= 4)
                {
                    Log($"\n[Stopped: target '{topTarget.Key}' repeated {topTarget.Count()}x]\n", "#FFA500");
                    OnTaskCompleted?.Invoke(false, $"Stuck cycling on '{topTarget.Key}'");
                    break;
                }
            }

            if (actionName == "done")
            {
                var success = actionInput["success"]?.ToObject<bool>() ?? false;
                var message = actionInput["message"]?.ToString() ?? "";

                // Verification: when AI claims success, take a screenshot and extract page text to verify
                if (success && step >= 2)
                {
                    Log("[Verifying...]\n", "#696969");
                    var verifyState = await GetPageState(ct);
                    if (verifyState != null)
                    {
                        // Extract visible text from the page so AI can verify even if screenshot truncates content
                        string pageText = "";
                        try
                        {
                            pageText = await _page!.EvaluateAsync<string>(@"() => {
                                // Get main content text, skipping nav/header noise
                                const main = document.querySelector('main, [role=main], .content, #content, .main-content') || document.body;
                                return main.innerText.substring(0, 3000);
                            }") ?? "";
                        }
                        catch { }

                        var verifyPrompt = $"You just claimed the task is DONE. VERIFY by looking at this screenshot AND the page text below.\n\nOriginal task: {instruction}\nYour claim: {message}\n\nPAGE TEXT (full content, not truncated by screenshot):\n{pageText}\n\nIs the task ACTUALLY completed? Reply with ONLY one word: 'VERIFIED' or 'NOT_DONE'.";

                        // Must provide tool_result for the done tool_use before asking verification
                        messages.Add(new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "tool_result", tool_use_id = toolUseId, content = "Checking... verify the task is actually done." },
                            }
                        });

                        // Add verification question as a separate user message
                        messages.Add(new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = verifyState["screenshotBase64"]!.ToString() } },
                                new { type = "text", text = verifyPrompt },
                            }
                        });

                        // Call WITHOUT tools so the AI just returns text (no tool_use that needs tool_result)
                        var verifyResponse = await CallAIVerify(messages, ct);
                        var verifyText = verifyResponse.Trim();

                        if (verifyText.Contains("NOT_DONE", StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"[Verification failed — continuing]\n", "#FFA500");
                            messages.Add(new { role = "assistant", content = new JArray { new JObject { ["type"] = "text", ["text"] = verifyText } } });
                            messages.Add(new
                            {
                                role = "user",
                                content = new object[]
                                {
                                    new { type = "text", text = "The task is NOT done yet. Continue working. Use the page state and clickable elements below." },
                                    new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = verifyState["screenshotBase64"]!.ToString() } },
                                    new { type = "text", text = FormatPageState(verifyState) },
                                }
                            });
                            continue; // keep going in the loop
                        }

                        Log("[Verification passed]\n", "#32CD32");
                    }
                }

                Log($"\nTask {(success ? "completed" : "did not complete")}: {message}\n",
                    success ? "#32CD32" : "#FFA07A");
                OnTaskCompleted?.Invoke(success, message);

                if (success && _recordingSteps != null && _recordingSteps.Count > 0)
                {
                    try
                    {
                        Log("[HYBRID] Generalizing steps into reusable strategy...\n", "#FFC832");
                        var cleanedSteps = CleanRecordedSteps(_recordingSteps);
                        Log($"[HYBRID] Cleaned {_recordingSteps.Count} steps → {cleanedSteps.Count} (removed retries/backtracking)\n", "#696969");
                        var genericPlan = await CreateGenericPlan(instruction, cleanedSteps, ct);
                        SavePlan(genericPlan);
                        Log($"[HYBRID] Strategy saved: \"{genericPlan.Description}\" ({genericPlan.Steps.Count} steps)\n", "#FFC832");
                    }
                    catch (Exception ex)
                    {
                        Log($"[HYBRID] Failed to save strategy: {ex.Message}\n", "#696969");
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

            OnStatusChanged?.Invoke($"Step {step}: Executing {actionName}...");
            Log($"[Executing: {actionName}...]\n", "#696969");

            var (execOk, execMsg) = await ExecuteAction(actionName, actionInput);
            if (!execOk)
            {
                _consecutiveFailCount++;
                var failKey = $"{actionName}:{actionInput["name"] ?? actionInput["text"] ?? actionInput["role"] ?? ""}";
                _recentFailedActions.Add(failKey);

                var hint = actionName == "click"
                    ? " Try a DIFFERENT approach: use 'click_text' with visible text, 'press' Enter to submit, or 'click_selector' with a CSS selector."
                    : " Use the accessibility tree to find the correct role and name.";

                if (_consecutiveFailCount >= 3)
                    hint += $" CRITICAL: You have failed {_consecutiveFailCount} times in a row. You MUST try a completely different strategy NOW.";

                // Detect repeated failures on the same target even with slightly different params
                var recentSameTarget = _recentFailedActions.Count(f => f == failKey);
                if (recentSameTarget >= 3)
                {
                    hint += $" ABSOLUTE STOP: You have tried '{failKey}' {recentSameTarget} times and it ALWAYS fails. This element does NOT exist or is NOT accessible this way. You MUST use a completely different method (click_selector with CSS, navigate to a direct URL, or use keyboard shortcuts).";
                }

                // If total failures are too high, force stop
                if (_consecutiveFailCount >= 5)
                {
                    Log($"\n[Stopped after {_consecutiveFailCount} consecutive failures]\n", "#FFA500");
                    OnTaskCompleted?.Invoke(false, "Too many consecutive failures");
                    break;
                }

                execMsg = $"ERROR: {execMsg}.{hint}";
            }
            else
            {
                _consecutiveFailCount = 0;
                _recentFailedActions.Clear();
                if (_recordingSteps != null)
                {
                    _recordingSteps.Add(new RecordedStep
                    {
                        Action = actionName,
                        Input = (JObject)actionInput.DeepClone(),
                        Result = execMsg,
                        Url = _lastPageUrl,
                        AiReasoning = aiReasoning,
                    });
                }
            }

            // For first 1-2 failures, show dimly as "trying alternative" — only show as error on 3+ consecutive fails
            if (!execOk && _consecutiveFailCount <= 2)
            {
                // Show as a dim retry, not a scary error
                OnStepCompleted?.Invoke(step, true, $"Trying alternative... ({execMsg.Replace("ERROR: ", "")})");
                Log($"Result: Trying alternative ({execMsg.Replace("ERROR: ", "")})\n", "#696969");
            }
            else
            {
                OnStepCompleted?.Invoke(step, execOk, execMsg);
                Log($"Result: {execMsg}\n", execOk ? "#FFFFFF" : "#FFA07A");
            }

            await Task.Delay(500, ct);

            OnStatusChanged?.Invoke($"Step {step}: Capturing page state...");
            state = await GetPageState(ct);
            if (state == null)
            {
                Log("[Could not get page state]\n", "#FFA500");
                break;
            }

            var currentUrl = state["url"]?.ToString() ?? "";
            if (currentUrl == _lastPageUrl)
                _samePageCount++;
            else
                _samePageCount = 0;
            _lastPageUrl = currentUrl;

            var stuckWarning = "";
            if (_samePageCount >= 5)
                stuckWarning = "\n\nWARNING: You have been on the same page for 5+ steps with no progress. If you cannot find the element, try: 1) click_selector with a CSS selector, 2) navigate to a direct URL, 3) use keyboard shortcut Ctrl+F to search. If truly stuck, call 'done' with success=false.";
            else if (_samePageCount >= 3)
                stuckWarning = "\n\nWARNING: You have been on the same page for multiple steps. Try a COMPLETELY DIFFERENT approach — use click_selector with CSS, or use keyboard shortcuts.";

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

            TrimOldHistory(messages, 2);
        }

        _hybridModelOverride = null;
        _recordingSteps = null;
        LogSection("Agent Finished");
        DisconnectPlaywright();
    }

    /// <summary>
    /// Clean recorded steps to remove retries, backtracking, and redundant navigation.
    /// Keeps only the "happy path" so Flash doesn't replay wasted attempts.
    /// </summary>
    private static List<RecordedStep> CleanRecordedSteps(List<RecordedStep> steps)
    {
        var cleaned = new List<RecordedStep>();

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // Skip go_back if the next step navigates back to where we came from
            // (back-and-forth pattern: click A → go_back → click A again)
            if (step.Action == "go_back")
            {
                // Check if this is part of a retry pattern — skip it
                continue;
            }

            // Skip consecutive duplicate actions (same action + same target)
            if (cleaned.Count > 0)
            {
                var prev = cleaned[^1];
                if (prev.Action == step.Action && prev.Input.ToString() == step.Input.ToString())
                    continue; // exact duplicate
            }

            // Skip if this navigates to the same URL we're already on
            if (step.Action == "navigate" && cleaned.Count > 0 && cleaned[^1].Url == step.Url)
                continue;

            // Skip scroll actions that are just exploratory (scroll down then up)
            if (step.Action == "scroll" && i + 1 < steps.Count && steps[i + 1].Action == "scroll")
            {
                var dir1 = step.Input["direction"]?.ToString();
                var dir2 = steps[i + 1].Input["direction"]?.ToString();
                if (dir1 != dir2) continue; // skip the first scroll if directions differ (exploratory)
            }

            cleaned.Add(step);
        }

        // Second pass: remove "click X → click X" where X is the same link visited earlier
        // This catches patterns like: click Applications → go_back → click Applications
        var final = new List<RecordedStep>();
        var visitedTargets = new HashSet<string>();
        foreach (var step in cleaned)
        {
            var targetKey = $"{step.Action}:{step.Input.ToString(Formatting.None)}";
            if (step.Action is "click" or "click_text" or "click_selector")
            {
                if (visitedTargets.Contains(targetKey))
                    continue; // already visited this exact target
                visitedTargets.Add(targetKey);
            }
            final.Add(step);
        }

        return final;
    }

    /// <summary>
    /// Smart snapshot truncation: prioritize interactive elements (links, buttons, inputs, search, etc.)
    /// over deeply nested structure and decorative content.
    /// </summary>
    private static string SmartTruncateSnapshot(string snapshot, int maxChars)
    {
        var lines = snapshot.Split('\n');
        var interactive = new List<string>();
        var structural = new List<string>();

        // Interactive keywords that indicate actionable elements
        var interactiveRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "link", "button", "textbox", "checkbox", "radio", "combobox", "menuitem",
            "tab", "option", "searchbox", "search", "switch", "slider", "spinbutton",
            "input", "select", "textarea", "menu", "menubar", "toolbar"
        };

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart(' ', '-');
            var firstWord = trimmed.Split(' ', '"', ':')[0].ToLowerInvariant();

            if (interactiveRoles.Contains(firstWord) ||
                trimmed.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("[role=", StringComparison.OrdinalIgnoreCase))
            {
                interactive.Add(line);
            }
            else
            {
                structural.Add(line);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== INTERACTIVE ELEMENTS (links, buttons, inputs, search) ===");
        foreach (var line in interactive)
        {
            sb.AppendLine(line);
            if (sb.Length > maxChars * 0.7) break; // 70% for interactive
        }

        sb.AppendLine("\n=== PAGE STRUCTURE (headings, sections, text) ===");
        foreach (var line in structural)
        {
            sb.AppendLine(line);
            if (sb.Length > maxChars) break;
        }

        if (sb.Length < snapshot.Length)
            sb.AppendLine($"\n[Snapshot reorganized: {interactive.Count} interactive + {structural.Count} structural elements from {lines.Length} total lines]");

        return sb.ToString();
    }

    private static string FormatPageState(JObject state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {state["url"]}");
        sb.AppendLine($"Title: {state["title"]}");
        sb.AppendLine($"{state["scrollInfo"]}");
        sb.AppendLine();

        // Clickable elements with CSS selectors (most reliable for targeting)
        var clickable = state["clickableElements"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(clickable))
        {
            sb.AppendLine("CLICKABLE ELEMENTS WITH CSS SELECTORS (use click_selector with the [selector] value):");
            sb.AppendLine(clickable);
            sb.AppendLine();
        }

        sb.AppendLine("ACCESSIBILITY TREE (use role + name with click/fill tools):");
        sb.AppendLine(state["ariaSnapshot"]?.ToString() ?? "(empty)");
        return sb.ToString();
    }

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

    // ==================== AI API ====================

    private string BuildSystemPrompt()
    {
        return @"You are a browser automation agent. You see a screenshot, an accessibility tree, AND a list of clickable elements with CSS selectors.

STRATEGY — THINK BEFORE ACTING:
Before clicking through menus step by step, ALWAYS consider:
1. Can I navigate DIRECTLY to what I need via URL? Many web apps support direct URLs (e.g., /search?q=term, /module/record/id). Try constructing a direct URL first.
2. Is there a GLOBAL SEARCH on the page? Most web apps have a search bar or icon in the header. Use it to find records directly instead of browsing through lists and filters.
3. Only if direct URL and global search don't work, fall back to navigating through menus.

TARGETING ELEMENTS — TWO METHODS:
1. By role+name using 'click' or 'fill' (from the ACCESSIBILITY TREE)
2. By CSS selector using 'click_selector' (from the CLICKABLE ELEMENTS list — look for [selector] values)
The CLICKABLE ELEMENTS list shows elements that may be MISSING from the accessibility tree (unnamed icons, custom widgets, etc.). ALWAYS check it when you can't find something in the accessibility tree.

RULES:
1. PREFER direct navigation: use 'navigate' to go directly to URLs when possible. Construct URLs based on the app's URL patterns.
2. Use 'click' with role+name OR 'click_selector' with CSS selectors. Check BOTH the accessibility tree AND the clickable elements list.
3. **CRITICAL**: If 'click' fails with timeout even ONCE, do NOT retry with different role/name variations. Instead:
   - Check the CLICKABLE ELEMENTS list for matching selectors
   - Use 'click_selector' with a CSS selector from that list
   - Or use 'click_text' with visible text from the screenshot
4. Use 'press' for keyboard shortcuts (Enter, Tab, Escape, Control+a, etc.). After filling a search box, press Enter.
5. NEVER repeat a failing action. Each failed attempt MUST use a DIFFERENT approach.
6. When done, call 'done'. If stuck after 3 failed attempts, call 'done' with success=false.
7. Respond with ONE tool call per step. No explanation needed.
8. Use EXACTLY the data from the user's task. NEVER make up or modify user-provided data.
9. LOOK AT THE SCREENSHOT carefully. It is MORE reliable than the accessibility tree. Use 'click_selector' for elements visible in screenshot but not in the tree.
10. For SEARCH: Look in the CLICKABLE ELEMENTS list for elements with 'search' in their id/class (e.g., [#sSearch], [.search-icon]). Use click_selector to click them.";
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
                description = "Type text into the currently focused element.",
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
                name = "click_selector",
                description = "Click an element by CSS selector. Use selectors from the CLICKABLE ELEMENTS list (e.g., '#sSearch', 'a.menu-icon-search', '[class*=search]'). PREFERRED when the accessibility tree doesn't have the element.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        selector = new { type = "string", description = "CSS selector (e.g., '#myId', '.myClass', 'a[class*=search]')" },
                    },
                    required = new[] { "selector" }
                }
            },
            new {
                name = "fill_selector",
                description = "Type text into an input identified by CSS selector.",
                input_schema = new {
                    type = "object",
                    properties = new {
                        selector = new { type = "string", description = "CSS selector of the input element" },
                        text = new { type = "string", description = "Text to type" },
                    },
                    required = new[] { "selector", "text" }
                }
            },
            new {
                name = "get_text",
                description = "Read the text content of an element. Use to verify content that is cut off in the screenshot (e.g., notes, messages, long text).",
                input_schema = new {
                    type = "object",
                    properties = new {
                        selector = new { type = "string", description = "CSS selector (default: 'body' for full page text)" },
                        maxLength = new { type = "integer", description = "Max characters to return (default: 2000)" },
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
        var model = _hybridModelOverride ?? _settings.SelectedModel;
        if (model.StartsWith("gemini"))
            return await CallGeminiWithTools(messages, forceComplete, ct, model);
        return await CallClaudeWithTools(messages, forceComplete, ct, model);
    }

    /// <summary>
    /// Call AI WITHOUT tools — just get a text response. Used for verification.
    /// </summary>
    private async Task<string> CallAIVerify(List<object> messages, CancellationToken ct)
    {
        var model = _hybridModelOverride ?? _settings.SelectedModel;
        if (model.StartsWith("gemini"))
        {
            // For Gemini, call without tools
            var contents = ConvertMessagesToGemini(messages);
            var body = new JObject
            {
                ["system_instruction"] = new JObject
                {
                    ["parts"] = new JArray { new JObject { ["text"] = "You are verifying if a browser automation task was completed. Reply with ONLY 'VERIFIED' or 'NOT_DONE'." } }
                },
                ["contents"] = contents,
                ["generationConfig"] = new JObject { ["maxOutputTokens"] = 100 },
            };
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_settings.GeminiApiKey}";
            var resp = await http.PostAsync(url, new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct);
            var respBody = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
            return respBody["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? "VERIFIED";
        }
        else
        {
            // For Claude, call without tools
            var reqBody = new
            {
                model,
                max_tokens = 100,
                system = "You are verifying if a browser automation task was completed. Reply with ONLY 'VERIFIED' or 'NOT_DONE'.",
                messages,
            };
            var json = JsonConvert.SerializeObject(reqBody, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("x-api-key", _settings.ClaudeApiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var resp = await http.PostAsync("https://api.anthropic.com/v1/messages", new StringContent(json, Encoding.UTF8, "application/json"), ct);
            var respBody = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
            return respBody["content"]?[0]?["text"]?.ToString() ?? "VERIFIED";
        }
    }

    // ==================== Gemini API ====================

    private async Task<JObject> CallGeminiWithTools(List<object> messages, bool forceComplete, CancellationToken ct, string? modelOverride = null)
    {
        var model = modelOverride ?? _settings.SelectedModel;
        var apiKey = _settings.GeminiApiKey;

        var geminiTools = BuildGeminiTools(forceComplete);
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
                Log($"[Rate limited \u2014 waiting {waitSeconds}s ({attempt + 1}/5)...]\n", "#FFFF00");
                OnStatusChanged?.Invoke($"Rate limited \u2014 retrying in {waitSeconds}s...");
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
        var toolNameMap = new Dictionary<string, string>();

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

    // ==================== Claude API ====================

    private async Task<JObject> CallClaudeWithTools(List<object> messages, bool forceComplete, CancellationToken ct, string? modelOverride = null)
    {
        var model = modelOverride ?? _settings.SelectedModel;
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

        for (int attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.Add("x-api-key", _settings.ClaudeApiKey);
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

                Log($"[Rate limited \u2014 waiting {waitSeconds}s before retry ({attempt + 1}/5)...]\n", "#FFFF00");
                OnStatusChanged?.Invoke($"Rate limited \u2014 retrying in {waitSeconds}s...");

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                continue;
            }

            var truncatedReq = json.Length > 500 ? json[..500] + "..." : json;
            LogToFile($"\nAPI Error ({response.StatusCode}):\nRequest: {truncatedReq}\nResponse: {responseBody}\n");
            throw new Exception($"Claude API error ({response.StatusCode}): {responseBody[..Math.Min(responseBody.Length, 300)]}");
        }

        throw new Exception("Claude API rate limit: exceeded 5 retries.");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        DisconnectPlaywright();
    }
}
