using System.Net;
using System.Net.Sockets;

namespace LiDPI.Core;

/// <summary>A DNS-over-HTTPS resolver endpoint.</summary>
/// <param name="Name">Display / lookup name.</param>
/// <param name="Url">DoH query URL.</param>
/// <param name="BootstrapIp">IP used to reach the resolver without system DNS (empty for a custom provider whose host is resolved at engine start).</param>
public sealed record DohProvider(string Name, string Url, string BootstrapIp);

/// <summary>Built-in DoH providers plus the user's optional custom one.</summary>
public static class DohProviders
{
    /// <summary>Persisted <see cref="AppSettings.DohProviderName"/> / <see cref="BypassProfile.DohProviderName"/> of the custom provider.</summary>
    public const string CustomKey = "Custom";

    public static DohProvider Cloudflare { get; } = new("Cloudflare", "https://cloudflare-dns.com/dns-query", "1.1.1.1");
    public static DohProvider Google { get; } = new("Google", "https://dns.google/dns-query", "8.8.8.8");
    public static DohProvider Quad9 { get; } = new("Quad9", "https://dns.quad9.net/dns-query", "9.9.9.9");
    public static DohProvider AdGuard { get; } = new("AdGuard", "https://dns.adguard-dns.com/dns-query", "94.140.14.14");

    /// <summary>All built-in providers.</summary>
    public static IReadOnlyList<DohProvider> All { get; } = [Cloudflare, Google, Quad9, AdGuard];

    private static volatile DohProvider? _custom;

    /// <summary>The user's custom provider (null when not configured). Set from the UI; read by the engines.</summary>
    public static DohProvider? Custom
    {
        get => _custom;
        set => _custom = value;
    }

    /// <summary>Builds the custom provider from stored settings; null if the URL is missing or invalid.</summary>
    public static DohProvider? CreateCustom(string? url, string? bootstrap)
    {
        if (!TryNormalizeUrl(url, out var u, out var host)) return null;
        string ip = "";
        if (!string.IsNullOrWhiteSpace(bootstrap))
        {
            if (!TryParseBootstrap(bootstrap, out var addr)) return null;
            ip = addr.ToString();
        }
        return new DohProvider(LiDPI.UI.Strings.DohCustomName(host), u, ip);
    }

    /// <summary>https only, a real host, a non-root path ("/dns-query"), no userinfo / fragment.</summary>
    public static bool TryNormalizeUrl(string? input, out string url, out string host)
    {
        url = host = "";
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 500) return false;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment)) return false;
        if (u.AbsolutePath.Length <= 1) return false;
        var kind = Uri.CheckHostName(u.IdnHost);
        if (kind is UriHostNameType.Unknown or UriHostNameType.Basic) return false;
        if (kind == UriHostNameType.Dns && !u.IdnHost.Contains('.')) return false;
        url = u.AbsoluteUri;
        host = u.Host;
        return true;
    }

    /// <summary>Literal IPv4 (dotted quad) or IPv6 address; not unspecified / broadcast.</summary>
    public static bool TryParseBootstrap(string? input, out IPAddress address)
    {
        address = IPAddress.None;
        var s = input?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(s) || !IPAddress.TryParse(s, out var a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') != 3) return false;   // "1" / "1.2" shorthand
        if (a.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.Broadcast)) return false;
        address = a;
        return true;
    }
}
