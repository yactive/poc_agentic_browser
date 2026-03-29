using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AgenticBrowser;

public partial class MainView : System.Windows.Controls.UserControl
{
    private readonly List<byte[]> _screenshots = new();
    private int _screenshotIndex = -1;

    // Events for MainForm to handle
    public event EventHandler? ExecuteClicked;
    public event EventHandler? StopClicked;
    public event EventHandler? LaunchChromeClicked;
    public event EventHandler? ClearLogClicked;
    public event EventHandler? ContinueAuthClicked;
    public event EventHandler? ClearPlansClicked;

    private bool _initialized;

    public MainView()
    {
        InitializeComponent();

        // Auto-save settings whenever key fields change (after initial load)
        Loaded += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _initialized = true;
                TxtClaudeKey.PasswordChanged += (_, _) => AutoSaveSettings();
                TxtGeminiKey.PasswordChanged += (_, _) => AutoSaveSettings();
                TxtPort.LostFocus += (_, _) => AutoSaveSettings();
                TxtMaxSteps.LostFocus += (_, _) => AutoSaveSettings();
                TxtTargetUrl.LostFocus += (_, _) => AutoSaveSettings();
                CboModel.SelectionChanged += (_, _) => AutoSaveSettings();
                CboMode.SelectionChanged += (_, _) => AutoSaveSettings();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;

    private void AutoSaveSettings()
    {
        if (!_initialized) return;
        _autoSaveTimer?.Stop();
        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _autoSaveTimer.Tick += (s, e) =>
        {
            _autoSaveTimer.Stop();
            try { GetCurrentSettings().Save(); } catch { }
        };
        _autoSaveTimer.Start();
    }

    // ==================== Button Handlers ====================

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Execute_Click(object sender, RoutedEventArgs e) => ExecuteClicked?.Invoke(this, EventArgs.Empty);
    private void Stop_Click(object sender, RoutedEventArgs e) => StopClicked?.Invoke(this, EventArgs.Empty);
    private void LaunchChrome_Click(object sender, RoutedEventArgs e) => LaunchChromeClicked?.Invoke(this, EventArgs.Empty);
    private void ContinueAuth_Click(object sender, RoutedEventArgs e) => ContinueAuthClicked?.Invoke(this, EventArgs.Empty);
    private void ClearPlans_Click(object sender, RoutedEventArgs e) => ClearPlansClicked?.Invoke(this, EventArgs.Empty);
    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Inlines.Clear();
        ClearLogClicked?.Invoke(this, EventArgs.Empty);
    }

    private void PrevScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshots.Count == 0) return;
        _screenshotIndex = Math.Max(0, _screenshotIndex - 1);
        ShowScreenshotAt(_screenshotIndex);
    }

    private void NextScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshots.Count == 0) return;
        _screenshotIndex = Math.Min(_screenshots.Count - 1, _screenshotIndex + 1);
        ShowScreenshotAt(_screenshotIndex);
    }

    // ==================== Settings ====================

    public EngineSettings GetCurrentSettings()
    {
        return new EngineSettings
        {
            ClaudeApiKey = TxtClaudeKey.Password,
            GeminiApiKey = TxtGeminiKey.Password,
            SelectedModel = "claude-sonnet-4-6",
            ModeIndex = 1, // Always Hybrid
            Port = int.TryParse(TxtPort.Text, out var p) ? p : 9222,
            MaxSteps = int.TryParse(TxtMaxSteps.Text, out var s) ? s : 25,
            TargetUrl = TxtTargetUrl.Text,
            Headless = ChkHeadless.IsChecked == true,
            Instruction = TxtInstruction.Text.Trim(),
        };
    }

    public void ApplySettings(EngineSettings settings)
    {
        TxtClaudeKey.Password = settings.ClaudeApiKey;
        TxtGeminiKey.Password = settings.GeminiApiKey;
        TxtPort.Text = settings.Port.ToString();
        TxtMaxSteps.Text = settings.MaxSteps.ToString();
        TxtTargetUrl.Text = settings.TargetUrl;
        ChkHeadless.IsChecked = settings.Headless;

        // Find matching model in combo
        for (int i = 0; i < CboModel.Items.Count; i++)
        {
            if ((CboModel.Items[i] as ComboBoxItem)?.Content?.ToString() == settings.SelectedModel)
            {
                CboModel.SelectedIndex = i;
                break;
            }
        }
        CboMode.SelectedIndex = Math.Min(settings.ModeIndex, CboMode.Items.Count - 1);
    }

    // ==================== UI Update Methods ====================

    public void AppendLog(string text, string colorHex)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AppendLog(text, colorHex)); return; }

        var brush = ParseBrush(colorHex);
        TxtLog.Inlines.Add(new Run(text) { Foreground = brush });

        // Auto-scroll to bottom
        LogScroller.ScrollToEnd();
    }

    public void SetScreenshot(byte[] imageBytes)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetScreenshot(imageBytes)); return; }

        _screenshots.Add(imageBytes);
        _screenshotIndex = _screenshots.Count - 1;
        ShowScreenshotAt(_screenshotIndex);
    }

    private void ShowScreenshotAt(int index)
    {
        if (index < 0 || index >= _screenshots.Count) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(_screenshots[index]);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            ImgScreenshot.Source = bmp;
            TxtScreenshotIndex.Text = $"Screenshot {index + 1} of {_screenshots.Count}";
        }
        catch { }
    }

    public void AddStep(int stepNum, string actionType, string description, bool isActive)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddStep(stepNum, actionType, description, isActive)); return; }

        // Deactivate previous active step
        foreach (var child in StepsPanel.Children)
        {
            if (child is Border b && b.Tag?.ToString() == "active")
            {
                b.BorderBrush = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
                b.Background = ParseBrush("#12122A");
                b.Tag = "done";

                // Update icon to checkmark
                if (b.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is DockPanel dp)
                {
                    if (dp.Children.Count > 0 && dp.Children[0] is TextBlock icon)
                    {
                        icon.Text = "\u2714";
                        icon.Foreground = ParseBrush("#34D399");
                    }
                    if (dp.Children.Count > 1 && dp.Children[1] is TextBlock stepLabel)
                    {
                        stepLabel.Foreground = ParseBrush("#E8E8F0");
                    }
                }
            }
        }

        // Create new step card
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 3, 0, 3),
            Tag = isActive ? "active" : "done",
        };

        if (isActive)
        {
            border.BorderBrush = ParseBrush("#00CFFF");
            border.BorderThickness = new Thickness(1.5);
            border.Background = ParseBrush("#0800CFFF");
        }
        else
        {
            border.Background = ParseBrush("#12122A");
        }

        var stack = new StackPanel();
        var dock = new DockPanel();

        var iconText = new TextBlock
        {
            Text = isActive ? "\u25CB" : "\u2714",
            Foreground = isActive ? ParseBrush("#00CFFF") : ParseBrush("#34D399"),
            FontSize = 15,
            Margin = new Thickness(0, 0, 10, 0),
        };
        dock.Children.Add(iconText);

        var stepText = new TextBlock
        {
            Text = $"Step {stepNum}",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = isActive ? ParseBrush("#00CFFF") : ParseBrush("#E8E8F0"),
        };
        dock.Children.Add(stepText);

        var actionLabel = new TextBlock
        {
            Text = actionType,
            FontSize = 12,
            Foreground = ParseBrush("#8B5CF6"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(actionLabel, Dock.Right);
        dock.Children.Insert(0, actionLabel); // Insert at 0 so it docks right

        stack.Children.Add(dock);

        if (!string.IsNullOrEmpty(description))
        {
            var descText = new TextBlock
            {
                Text = description,
                FontSize = 12.5,
                Foreground = isActive ? ParseBrush("#00CFFF") : ParseBrush("#9090B0"),
                Margin = new Thickness(25, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            stack.Children.Add(descText);
        }

        border.Child = stack;
        StepsPanel.Children.Add(border);
        StepsScroller.ScrollToEnd();
    }

    public void CompleteStep(int stepNum, bool success, string result)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => CompleteStep(stepNum, success, result)); return; }

        // Find and update the step card
        foreach (var child in StepsPanel.Children)
        {
            if (child is Border b && b.Tag?.ToString() == "active")
            {
                b.Tag = "done";
                b.BorderBrush = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
                b.Background = ParseBrush("#12122A");

                if (b.Child is StackPanel sp)
                {
                    // Update icon
                    if (sp.Children.Count > 0 && sp.Children[0] is DockPanel dp)
                    {
                        // Icon is at index 1 (action label is at 0 due to DockRight)
                        foreach (var dChild in dp.Children)
                        {
                            if (dChild is TextBlock tb && (tb.Text == "\u25CB" || tb.Text == "\u2714" || tb.Text == "\u2718"))
                            {
                                tb.Text = success ? "\u2714" : "\u2718";
                                tb.Foreground = success ? ParseBrush("#34D399") : ParseBrush("#F87171");
                            }
                            else if (dChild is TextBlock lbl && lbl.FontWeight == FontWeights.Bold && lbl.Text.StartsWith("Step"))
                            {
                                lbl.Foreground = ParseBrush("#E8E8F0");
                            }
                        }
                    }

                    // Add result description
                    if (!string.IsNullOrEmpty(result))
                    {
                        var descText = new TextBlock
                        {
                            Text = result,
                            FontSize = 12.5,
                            Foreground = success ? ParseBrush("#9090B0") : ParseBrush("#F87171"),
                            Margin = new Thickness(25, 4, 0, 0),
                            TextWrapping = TextWrapping.Wrap,
                        };
                        sp.Children.Add(descText);
                    }
                }
                break;
            }
        }
    }

    public void SetProgress(int current, int max)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetProgress(current, max)); return; }

        if (max <= 0) { ProgressFill.Width = 0; return; }

        // Get the parent width
        var parent = ProgressFill.Parent as Border;
        var parentWidth = parent?.ActualWidth ?? 800;
        ProgressFill.Width = Math.Min(parentWidth, parentWidth * current / max);

        TxtStepCounter.Text = $"Step {current} of {max}";
    }

    public void SetStatus(string status, bool isRunning = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetStatus(status, isRunning)); return; }
        TxtStatusLabel.Text = status;
    }

    public void SetRunning(bool running)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetRunning(running)); return; }

        BtnExecute.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        BtnLaunchChrome.IsEnabled = !running;

        if (running)
        {
            StatusDot.Fill = ParseBrush("#00CFFF");
            TxtStatusLabel.Text = "Running";
            TxtStatusLabel.Foreground = ParseBrush("#00CFFF");
            StatusBadge.Background = ParseBrush("#1500CFFF");
        }
        else
        {
            StatusDot.Fill = ParseBrush("#606085");
            TxtStatusLabel.Text = "Ready";
            TxtStatusLabel.Foreground = ParseBrush("#606085");
            StatusBadge.Background = ParseBrush("#15606085");
            TxtStepCounter.Text = "";
        }
    }

    public void SetTaskCompleted(bool success, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetTaskCompleted(success, message)); return; }

        StatusDot.Fill = success ? ParseBrush("#34D399") : ParseBrush("#F87171");
        TxtStatusLabel.Text = success ? "Completed" : "Failed";
        TxtStatusLabel.Foreground = success ? ParseBrush("#34D399") : ParseBrush("#F87171");
        StatusBadge.Background = success ? ParseBrush("#1534D399") : ParseBrush("#15F87171");
    }

    public void ClearSteps()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ClearSteps); return; }
        StepsPanel.Children.Clear();
        _screenshots.Clear();
        _screenshotIndex = -1;
        ImgScreenshot.Source = null;
        TxtScreenshotIndex.Text = "No screenshots";
        ProgressFill.Width = 0;
    }

    public void SetConnectionStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetConnectionStatus(text)); return; }
        TxtConnectionStatus.Text = text;
    }

    public void AddHistoryItem(string taskName, bool success, int stepCount, DateTime when)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddHistoryItem(taskName, success, stepCount, when)); return; }

        var dock = new DockPanel();

        var icon = new TextBlock
        {
            Text = success ? "\u2714" : "\u2718",
            Foreground = success ? ParseBrush("#34D399") : ParseBrush("#F87171"),
            FontSize = 14,
            Width = 24,
        };
        dock.Children.Add(icon);

        var timeText = new TextBlock
        {
            Text = when.Date == DateTime.Today ? when.ToString("HH:mm") : "Yesterday",
            FontSize = 11,
            Foreground = ParseBrush("#606085"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0),
        };
        DockPanel.SetDock(timeText, Dock.Right);
        dock.Children.Insert(0, timeText);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = taskName.Length > 30 ? taskName[..30] + "..." : taskName,
            FontSize = 13,
            Foreground = ParseBrush("#E8E8F0"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{stepCount} steps" + (success ? "" : " \u00B7 Failed"),
            FontSize = 11,
            Foreground = success ? ParseBrush("#606085") : ParseBrush("#F87171"),
        });
        dock.Children.Add(stack);

        var item = new ListBoxItem
        {
            Content = dock,
            Style = (Style)FindResource("HistItem"),
        };
        LstHistory.Items.Insert(0, item); // newest first
    }

    public void ShowAuthContinueButton(bool show)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ShowAuthContinueButton(show)); return; }
        BtnContinueAuth.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public int StepCount => StepsPanel.Children.Count;

    // ==================== Helpers ====================

    private static SolidColorBrush ParseBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }
}
