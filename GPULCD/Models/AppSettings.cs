using System.IO;
using System.Text.Json;

namespace GPULCD.Models;

public class AppSettings
{
    public string? ComPort { get; set; } // null = auto-detect
    public string DisplayMode { get; set; } = "turing"; // "turing" or "gscoler"
    public int Brightness { get; set; } = 75;
    public DisplayOrientation Orientation { get; set; } = DisplayOrientation.Landscape;
    public bool MirrorMode { get; set; } = false;
    public bool AutoStart { get; set; } = false;
    public int UpdateIntervalMs { get; set; } = 500;
    public string ThemeName { get; set; } = "default";
    public string SensorProvider { get; set; } = "lhm"; // "lhm" or "hwinfo"

    // Sensor slot mappings — maps display slot to sensor name
    public Dictionary<string, string> SensorMappings { get; set; } = new()
    {
        ["CpuTemp"] = "CPU Package (Temperature)",
        ["CpuUsage"] = "CPU Total",
        ["GpuTemp"] = "GPU Core (Temperature)",
        ["GpuUsage"] = "D3D 3D",
        ["RamUsage"] = "Memory (Load)",
        ["CpuFan"] = "Nuvoton NCT6687D: CPU Fan",
        ["GpuFan"] = "GPU Fan (Fan)",
        ["CaseFan"] = "Nuvoton NCT6687D: System Fan #3"
    };

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GPULCD");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(ConfigPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
