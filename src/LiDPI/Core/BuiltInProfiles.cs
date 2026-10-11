namespace LiDPI.Core;

/// <summary>
/// Predefined bypass profiles. Tuned with <see cref="ProfileProbe"/> on Türk Telekom (2026-10): the DPI box sits
/// ~2 hops away, reassembles split/disordered segments and injects RSTs, so splitting alone is unreliable;
/// a decoy ClientHello that reaches the DPI (TTL >= 3) but not the server is what makes it work. Decoys with a
/// wrong checksum or wrong sequence number are ignored by this DPI.
/// </summary>
public static class BuiltInProfiles
{
    /// <summary>Fake (auto TTL, x2) + split at byte 2 + disorder + DoH. QUIC stays allowed (faster; browsers use it when it works).</summary>
    public static BypassProfile Standard { get; } = new()
    {
        Id = "standard",
        SplitTlsHello = true,
        SplitAtSni = false,
        SplitPosition = 2,
        DisorderSegments = true,
        FakePacket = true,
        FakeTtl = 0,
        FakeRepeats = 2,
        Dns = DnsMode.DoH,
        BlockQuic = false,
    };

    /// <summary>Split only + DoH (no decoy; weak against reassembling DPI).</summary>
    public static BypassProfile Light { get; } = new()
    {
        Id = "light",
        SplitTlsHello = true,
        Dns = DnsMode.DoH,
        BlockQuic = false,
    };

    /// <summary>Fake (auto TTL, x3) + split at byte 1 + disorder + DoH + block QUIC.</summary>
    public static BypassProfile Aggressive { get; } = new()
    {
        Id = "aggressive",
        SplitTlsHello = true,
        SplitAtSni = false,
        SplitPosition = 1,
        DisorderSegments = true,
        FakePacket = true,
        FakeTtl = 0,
        FakeRepeats = 3,
        Dns = DnsMode.DoH,
        BlockQuic = true,
    };

    /// <summary>Fake TTL 3 + split + DoH.</summary>
    public static BypassProfile Superonline { get; } = new()
    {
        Id = "superonline",
        SplitTlsHello = true,
        FakePacket = true,
        FakeTtl = 3,
        Dns = DnsMode.DoH,
        BlockQuic = false,
    };

    /// <summary>Fake TTL 5 + split at byte 2 + disorder + DoH (best probe result on TT).</summary>
    public static BypassProfile Ttnet { get; } = new()
    {
        Id = "ttnet",
        SplitTlsHello = true,
        SplitAtSni = false,
        SplitPosition = 2,
        DisorderSegments = true,
        FakePacket = true,
        FakeTtl = 5,
        Dns = DnsMode.DoH,
        BlockQuic = false,
    };

    /// <summary>All built-in profiles. Names and descriptions are in the language files ("Profile.{Id}.Name" / ".Desc").</summary>
    public static IReadOnlyList<BypassProfile> All { get; } = [Standard, Light, Aggressive, Superonline, Ttnet];
}
