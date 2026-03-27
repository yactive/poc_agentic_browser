using System.IO;

namespace AgenticBrowser;

public class EngineSettings
{
    public string ClaudeApiKey { get; set; } = "";
    public string GeminiApiKey { get; set; } = "";
    public string SelectedModel { get; set; } = "claude-sonnet-4-6";
    public int ModeIndex { get; set; }  // 0=Direct, 1=Hybrid
    public int Port { get; set; } = 9222;
    public int MaxSteps { get; set; } = 25;
    public string TargetUrl { get; set; } = "";
    public bool Headless { get; set; } = true;
    public string Instruction { get; set; } = "";

    private const string SettingsFile = "agentic_browser_settings.json";

    public static EngineSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SettingsFile);
        if (!File.Exists(path)) return new();
        try
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            return new EngineSettings
            {
                ClaudeApiKey = json["apiKey"]?.ToString() ?? "",
                GeminiApiKey = json["geminiKey"]?.ToString() ?? "",
                SelectedModel = json["model"]?.ToString() ?? "claude-sonnet-4-6",
                ModeIndex = json["modeIndex"]?.ToObject<int>() ?? 0,
                Port = json["port"]?.ToObject<int>() ?? 9222,
                MaxSteps = json["maxSteps"]?.ToObject<int>() ?? 25,
                TargetUrl = json["targetUrl"]?.ToString() ?? "",
                Headless = json["headless"]?.ToObject<bool>() ?? true,
            };
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            var json = new Newtonsoft.Json.Linq.JObject
            {
                ["apiKey"] = ClaudeApiKey,
                ["geminiKey"] = GeminiApiKey,
                ["model"] = SelectedModel,
                ["modeIndex"] = ModeIndex,
                ["port"] = Port,
                ["maxSteps"] = MaxSteps,
                ["targetUrl"] = TargetUrl,
                ["headless"] = Headless,
            };
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, SettingsFile),
                json.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch { }
    }
}
