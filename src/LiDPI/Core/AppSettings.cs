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
    /// <summary>
    /// Profile found by "En iyi ayarı otomatik bul" when no built-in profile won (Id = <see cref="AutoFind.CustomProfileId"/>).
    /// Null until a probe produced one.
    /// </summary>
    public BypassProfile? CustomProfile { get; set; }

    /// <summary>
    /// False only on a brand-new install (no settings file yet): the main window then offers the auto-find once.
    /// A settings file written by an older version (no such key) counts as completed.
    /// </summary>
    public bool FirstRunCompleted { get; set; } = false;

    /// <summary>Restart the engine after network changes / resume from sleep (only if it was meant to run).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Reachability check interval while connected, in minutes: 0 (off), 5 or 15.</summary>
    public int HealthCheckMinutes { get; set; } = 5;

    /// <summary>UI language code ("tr", "en", or an external language file's name). Null = follow the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Sites tested by auto-find and the health check (https URLs). Null = <see cref="ProbeTargets.DefaultBlockedUrls"/>.
    /// The control site is internal and never stored here.
    /// </summary>
    public List<string>? TestSites { get; set; }

    /// <summary>DoH URL of the "Özel…" provider (https, with a path). Used when <see cref="DohProviderName"/> is <see cref="DohProviders.CustomKey"/>.</summary>
    public string? CustomDohUrl { get; set; }

    /// <summary>Optional bootstrap IP of the custom DoH provider; empty = resolve the host once via system DNS at engine start.</summary>
    public string? CustomDohBootstrap { get; set; }

    /// <summary>Load settings; returns defaults if the file is missing or corrupt.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                // existing user upgrading from a version without the flag: not a first run
                if (!json.Contains($"\"{nameof(FirstRunCompleted)}\"", StringComparison.Ordinal))
                    s.FirstRunCompleted = true;
                if (s.HealthCheckMinutes is not (0 or 5 or 15)) s.HealthCheckMinutes = 5;
                if (s.TestSites is { } sites)
                {
                    var clean = ProbeTargets.Sanitize(sites);
                    s.TestSites = clean.Count == 0 ? null : clean;
                }
                if (s.CustomDohUrl is { } du && !DohProviders.TryNormalizeUrl(du, out _, out _)) s.CustomDohUrl = null;
                if (!string.IsNullOrWhiteSpace(s.CustomDohBootstrap) && !DohProviders.TryParseBootstrap(s.CustomDohBootstrap, out _))
                    s.CustomDohBootstrap = null;
                return s;
            }
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
