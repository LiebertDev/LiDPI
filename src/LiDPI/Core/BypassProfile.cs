namespace LiDPI.Core;

/// <summary>Set of DPI bypass techniques applied by an engine.</summary>
public sealed record BypassProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

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

    /// <summary>Drop UDP/443 so apps fall back to TCP.</summary>
    public bool BlockQuic { get; init; } = true;

    /// <summary>DNS resolution mode.</summary>
    public DnsMode Dns { get; init; } = DnsMode.DoH;
    /// <summary>Name of a <see cref="DohProviders"/> entry.</summary>
    public string DohProviderName { get; init; } = "Cloudflare";
}
