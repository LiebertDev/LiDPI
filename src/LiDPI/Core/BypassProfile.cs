using System.Text.Json.Serialization;
using LiDPI.Localization;

namespace LiDPI.Core;

/// <summary>Set of DPI bypass techniques applied by an engine.</summary>
public sealed record BypassProfile
{
    public string Id { get; init; } = "";
    /// <summary>Fallback name; shown texts come from <see cref="DisplayName"/> (language file key "Profile.{Id}.Name").</summary>
    public string Name { get; init; } = "";
    /// <summary>Fallback description; see <see cref="DisplayDescription"/>.</summary>
    public string Description { get; init; } = "";

    /// <summary>Name in the current UI language.</summary>
    [JsonIgnore]
    public string DisplayName => Loc.Instance.TryGet($"Profile.{Id}.Name", out var s) ? s : Name;

    /// <summary>
    /// Description in the current UI language. The auto-found profile describes its own techniques, built-ins use
    /// "Profile.{Id}.Desc".
    /// </summary>
    [JsonIgnore]
    public string DisplayDescription =>
        Id == AutoFind.CustomProfileId ? AutoFind.Describe(this)
        : Loc.Instance.TryGet($"Profile.{Id}.Desc", out var s) ? s : Description;

    /// <summary>Split the TLS ClientHello into segments.</summary>
    public bool SplitTlsHello { get; init; } = true;
    /// <summary>Split offset in bytes (used when not splitting at SNI).</summary>
    public int SplitPosition { get; init; } = 2;
    /// <summary>Split inside the SNI hostname.</summary>
    public bool SplitAtSni { get; init; } = true;

    /// <summary>Send the 2nd segment first.</summary>
    public bool DisorderSegments { get; init; } = false;

    /// <summary>Inject a fake packet before the real one.</summary>
    public bool FakePacket { get; init; } = false;
    /// <summary>TTL of the fake packet; 0 = auto TTL.</summary>
    public int FakeTtl { get; init; } = 0;

    /// <summary>Give the fake packet a wrong TCP checksum.</summary>
    public bool WrongChecksumFake { get; init; } = false;

    /// <summary>
    /// Give the fake packet a sequence number far behind the real one: a DPI box that does not track
    /// sequence numbers still parses it, while the server discards it as stale data.
    /// </summary>
    public bool FakeWrongSeq { get; init; } = false;

    /// <summary>How many times the fake packet is sent (1-5).</summary>
    public int FakeRepeats { get; init; } = 1;

    /// <summary>Host header case mix / space removal for plain HTTP.</summary>
    public bool HttpHostTricks { get; init; } = true;

    /// <summary>Block UDP/443 (QUIC) so apps fall back to TCP, where the ClientHello tricks apply.</summary>
    public bool BlockQuic { get; init; } = true;

    /// <summary>DNS resolution mode.</summary>
    public DnsMode Dns { get; init; } = DnsMode.DoH;
    /// <summary>Name of a <see cref="DohProviders"/> entry.</summary>
    public string DohProviderName { get; init; } = "Cloudflare";

    /// <summary>IPv4 resolver ("ip:port") for <see cref="DnsMode.UdpRedirect"/>; null = <see cref="FastDns.DefaultV4"/>. Set per start from the settings.</summary>
    [JsonIgnore]
    public string? FastDnsServer { get; init; }

    /// <summary>IPv6 resolver ("[ip]:port") for <see cref="DnsMode.UdpRedirect"/>; null = <see cref="FastDns.DefaultV6"/>.</summary>
    [JsonIgnore]
    public string? FastDnsServerV6 { get; init; }
}
