using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace AgenticBrowser;

public class MainForm : Form
{
    private readonly MainView _view;
    private AutomationEngine? _engine;

    public MainForm()
    {
        Text = "Active Worker";
        Width = 1500;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(1100, 700);
        BackColor = System.Drawing.Color.FromArgb(13, 13, 26);

        _view = new MainView();

        var host = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = _view
        };
        Controls.Add(host);

        // Load saved settings
        var settings = EngineSettings.Load();
        _view.ApplySettings(settings);

        // Wire view events
        _view.ExecuteClicked += async (s, e) => await OnExecute();
        _view.StopClicked += (s, e) => _engine?.Stop();
        _view.LaunchChromeClicked += async (s, e) => await OnLaunchChrome();

        FormClosing += (s, e) =>
        {
            var currentSettings = _view.GetCurrentSettings();
            currentSettings.Save();
            _engine?.Dispose();
        };
    }

    private async Task OnExecute()
    {
        var settings = _view.GetCurrentSettings();
        if (string.IsNullOrWhiteSpace(settings.Instruction))
        {
            MessageBox.Show("Please enter instructions.", "Input Required",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _engine?.Dispose();
        _engine = new AutomationEngine(settings);
        WireEngineEvents(_engine);

        _view.ClearSteps();
        _view.SetRunning(true);

        try
        {
            await _engine.ExecuteTaskAsync();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Cannot Execute",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Invoke(() => _view.SetRunning(false));
        }
    }

    private async Task OnLaunchChrome()
    {
        var settings = _view.GetCurrentSettings();
        _engine?.Dispose();
        _engine = new AutomationEngine(settings);
        WireEngineEvents(_engine);

        try
        {
            await _engine.LaunchChromeAsync();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Chrome Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void WireEngineEvents(AutomationEngine engine)
    {
        engine.OnLogMessage += (msg, color) =>
        {
            if (InvokeRequired)
                Invoke(() => _view.AppendLog(msg, color));
            else
                _view.AppendLog(msg, color);
        };

        engine.OnScreenshotCaptured += (bytes) =>
        {
            if (InvokeRequired)
                Invoke(() => _view.SetScreenshot(bytes));
            else
                _view.SetScreenshot(bytes);
        };

        engine.OnStepStarted += (step, max, action) =>
        {
            if (InvokeRequired)
                Invoke(() =>
                {
                    _view.AddStep(step, action, "", true);
                    _view.SetProgress(step, max);
                });
            else
            {
                _view.AddStep(step, action, "", true);
                _view.SetProgress(step, max);
            }
        };

        engine.OnStepCompleted += (step, success, result) =>
        {
            if (InvokeRequired)
                Invoke(() => _view.CompleteStep(step, success, result));
            else
                _view.CompleteStep(step, success, result);
        };

        engine.OnTaskCompleted += (success, message) =>
        {
            if (InvokeRequired)
                Invoke(() =>
                {
                    _view.SetTaskCompleted(success, message);
                    _view.AddHistoryItem(_view.GetCurrentSettings().Instruction, success,
                        _view.StepCount, DateTime.Now);
                });
            else
            {
                _view.SetTaskCompleted(success, message);
                _view.AddHistoryItem(_view.GetCurrentSettings().Instruction, success,
                    _view.StepCount, DateTime.Now);
            }
        };

        engine.OnStatusChanged += (status) =>
        {
            if (InvokeRequired)
                Invoke(() => _view.SetStatus(status));
            else
                _view.SetStatus(status);
        };

        engine.OnRunningChanged += (running) =>
        {
            if (InvokeRequired)
                Invoke(() => _view.SetRunning(running));
            else
                _view.SetRunning(running);
        };
    }
}
