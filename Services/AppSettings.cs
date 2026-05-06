using System;
using System.IO;
using System.Text.Json;

namespace Twenti.Services;

public enum MonitorPreference
{
    FollowCursor = 0,
    MainMonitor = 1,
}

public sealed class AppSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public bool Muted { get; set; } = false;
    public MonitorPreference Monitor { get; set; } = MonitorPreference.FollowCursor;
    public bool HasShownTrayHint { get; set; } = false;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Twenti", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // ignore — fall through to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // ignore — non-critical
        }
    }
}
