using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiDPI.Core;

/// <summary>User settings persisted as JSON in %AppData%\LiDPI\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Full path of the settings file.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiDPI", "settings.json");

    public EngineMode Mode { get; set; } = EngineMode.Divert;
    public string ProfileId { get; set; } = "standard";
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool AutoConnectOnLaunch { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Name of the selected <see cref="DohProviders"/> entry (applied to the profile on start).</summary>
    public string DohProviderName { get; set; } = "Cloudflare";

    /// <summary>Load settings; returns defaults if the file is missing or corrupt.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // corrupt / unreadable file: fall back to defaults
        }
        return new AppSettings();
    }

    /// <summary>Save settings atomically. Never throws.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }
}
