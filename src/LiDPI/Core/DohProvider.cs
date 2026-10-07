namespace LiDPI.Core;

/// <summary>A DNS-over-HTTPS resolver endpoint.</summary>
/// <param name="Name">Display / lookup name.</param>
/// <param name="Url">DoH query URL.</param>
/// <param name="BootstrapIp">IP used to reach the resolver without system DNS.</param>
public sealed record DohProvider(string Name, string Url, string BootstrapIp);

/// <summary>Built-in DoH providers.</summary>
public static class DohProviders
{
    public static DohProvider Cloudflare { get; } = new("Cloudflare", "https://cloudflare-dns.com/dns-query", "1.1.1.1");
    public static DohProvider Google { get; } = new("Google", "https://dns.google/dns-query", "8.8.8.8");
    public static DohProvider Quad9 { get; } = new("Quad9", "https://dns.quad9.net/dns-query", "9.9.9.9");
    public static DohProvider AdGuard { get; } = new("AdGuard", "https://dns.adguard-dns.com/dns-query", "94.140.14.14");

    /// <summary>All built-in providers.</summary>
    public static IReadOnlyList<DohProvider> All { get; } = [Cloudflare, Google, Quad9, AdGuard];
}
