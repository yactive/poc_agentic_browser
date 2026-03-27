namespace AgenticBrowser;

static class Program
{
    [STAThread]
    static void Main()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticBrowser");
        System.IO.Directory.CreateDirectory(logDir);
        var logPath = System.IO.Path.Combine(logDir, "crash.log");

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.IO.File.WriteAllText(logPath, $"{DateTime.Now}\nUnhandled:\n{e.ExceptionObject}\n");
        };

        Application.ThreadException += (s, e) =>
        {
            System.IO.File.WriteAllText(logPath, $"{DateTime.Now}\nThreadException:\n{e.Exception}\n");
        };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(logPath, $"{DateTime.Now}\nMain catch:\n{ex}\n");
            MessageBox.Show($"Fatal error:\n{ex.Message}\n\nLog: {logPath}",
                "Active Worker - Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
