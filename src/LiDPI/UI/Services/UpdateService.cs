using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LiDPI.UI.Services;

/// <summary>A newer GitHub release: its version and the release page to open in the browser.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string HtmlUrl)
{
    public string DisplayVersion => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";
}

/// <summary>Outcome of a manual update check.</summary>
public enum UpdateCheckStatus { UpToDate, Available, Failed }

/// <summary>Result of <see cref="UpdateService.CheckDetailedAsync"/>; <see cref="Info"/> is set only when <see cref="Status"/> is Available.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info);

/// <summary>
/// Manual, check-only GitHub Releases lookup, run only when the user presses "Check for updates" (there is no
/// automatic or scheduled check). A newer release's page is opened in the default browser; nothing is downloaded or
/// installed by the app.
/// </summary>
public static class UpdateService
{
    public const string LatestReleaseApi = "https://api.github.com/repos/LiebertDev/LiDPI/releases/latest";
    public const string ReleasePagePrefix = "https://github.com/LiebertDev/LiDPI/releases/";

    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxRedirects = 3;

    /// <summary>Version of this build (LiDPI assembly, not the entry assembly).</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static string UserAgent => $"LiDPI/{CurrentVersion.ToString(3)} (+https://github.com/LiebertDev/LiDPI)";

    /// <summary>Asks GitHub for the latest release; tells "newer", "up to date" and "could not check" apart.</summary>
    public static async Task<UpdateCheckResult> CheckDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            var url = new Uri(LatestReleaseApi);
            for (var hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    // e.g. a renamed repository: follow only within api.github.com over https
                    if (resp.Headers.Location is not { } loc || hop >= MaxRedirects) return Failed;
                    var next = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                    if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
                        return Failed;
                    url = next;
                    continue;
                }
                // 404 = no published (non-draft, non-prerelease) release yet: nothing newer
                if (resp.StatusCode == HttpStatusCode.NotFound) return new(UpdateCheckStatus.UpToDate, null);
                if (!resp.IsSuccessStatusCode) return Failed;
                var json = await ReadLimitedAsync(resp, ct).ConfigureAwait(false);
                if (json is null) return Failed;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (Parse(root) is { } info) return new(UpdateCheckStatus.Available, info);
                // newer tag but an untrusted page URL: report a failure rather than "up to date"
                var tag = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
                if (!TryParseTag(tag, out var v)) return Failed;
                return v > CurrentVersion && !IsDraftOrPrerelease(root) ? Failed : new(UpdateCheckStatus.UpToDate, null);
            }
        }
        catch
        {
            return Failed;   // offline, rate-limited, blocked, malformed
        }
    }

    private static UpdateCheckResult Failed { get; } = new(UpdateCheckStatus.Failed, null);

    private static bool IsDraftOrPrerelease(JsonElement r) =>
        (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
        || (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True);

    /// <summary>Parses a GitHub "release" object; null if not newer, a draft/prerelease, or the page URL is untrusted.</summary>
    public static UpdateInfo? Parse(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (release.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = release.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (!TryParseTag(tag, out var version) || version <= CurrentVersion) return null;
        var html = release.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        return IsTrustedReleasePage(html) ? new UpdateInfo(version, tag!, html!) : null;
    }

    /// <summary>Only https pages of this repository's releases (no other host, port, userinfo or path escape).</summary>
    public static bool IsTrustedReleasePage(string? url)
    {
        if (url is null || !url.StartsWith(ReleasePagePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
               && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
               && u.AbsolutePath.StartsWith("/LiebertDev/LiDPI/releases/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens the release page in the default browser (re-validated). Returns false if refused or failed.</summary>
    public static bool OpenReleasePage(UpdateInfo info)
    {
        if (!IsTrustedReleasePage(info.HtmlUrl)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(new Uri(info.HtmlUrl).AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"v1.2.3" / "1.2.3" → 1.2.3 (missing parts are 0).</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return false;
        version = Normalize(v);
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > MaxResponseBytes) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxResponseBytes) return null;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
