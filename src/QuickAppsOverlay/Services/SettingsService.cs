using System.IO;
using System.Text.Json;

namespace QuickAppsOverlay.Services;

public class LauncherItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public class OverlaySettings
{
    public double Opacity { get; set; } = 0.85;
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 10;
    public bool IsCollapsed { get; set; } = false;
    public bool IsPositionLocked { get; set; } = false;
    public string DockEdge { get; set; } = "Bottom";
    public string LayoutOrientation { get; set; } = "Auto";
    public string DockScreen { get; set; } = string.Empty;
    public double? DockOffset { get; set; }
    public List<string> SelectedWindowKeys { get; set; } = new();
    public List<LauncherItem> LauncherItems { get; set; } = new();
}

public class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickAppsOverlay");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public OverlaySettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Settings = JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions) ?? new();
            }
        }
        catch
        {
            Settings = new OverlaySettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
