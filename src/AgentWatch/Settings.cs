using System.IO;
using System.Text.Json;
using AgentWatch.Services;

namespace AgentWatch;

public sealed class Settings
{
    public bool AlwaysOnTop { get; set; }
    public bool ShowShortCommands { get; set; }
    public bool Notify { get; set; } = true;
    public double NotifyMinSeconds { get; set; } = 15;
    public double RecentHours { get; set; } = 6;
    public bool RecentExpanded { get; set; }
    public bool RecentShowAll { get; set; }
    public int RecentShortCount { get; set; } = 8;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 820;

    private static string FilePath => Path.Combine(MonitorService.DataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(MonitorService.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
