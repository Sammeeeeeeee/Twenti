using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// WaveOut device product name selected for sound output. Null/empty
    /// means "use system default". Stored by name (not index) because
    /// device indices shuffle when devices are added/removed.
    /// </summary>
    public string? OutputDeviceName { get; set; }

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
                return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"AppSettings.Load failed, falling back to defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    /// <summary>
    /// Atomic write: serialise to a sibling .tmp file and rename over the
    /// real path. A crash mid-write leaves the previous good file intact
    /// instead of producing a truncated JSON that Load() would silently
    /// discard.
    /// </summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"AppSettings.Save failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext { }
