using System.Collections.ObjectModel;
using System.Text.Json;

namespace NetPrio.Models;

public enum PriorityTier
{
    Highest,
    Medium,
    Low
}

public class PriorityConfig
{
    public ObservableCollection<string> HighestApps { get; set; } = new();
    public ObservableCollection<string> MediumApps { get; set; } = new();
    public ObservableCollection<string> LowApps { get; set; } = new();

    public string Language { get; set; } = "auto"; // "auto", "ru", "en"
    public bool AutoMeasureSpeed { get; set; } = true;
    public double ChannelDownloadMbps { get; set; } = 100;
    public double ChannelUploadMbps { get; set; } = 100;
    public int MediumCapPercent { get; set; } = 50;
    public int LowCapPercent { get; set; } = 10;
    public double MinimumFloorMbps { get; set; } = 2.0;
    public bool ExcludeVpnTunnelProcess { get; set; } = true;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetPrio", "config.json");

    public static PriorityConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new PriorityConfig();
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<PriorityConfig>(json) ?? new PriorityConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
