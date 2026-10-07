namespace LiDPI.Core;

/// <summary>
/// Predefined bypass profiles. Tuned with <see cref="ProfileProbe"/> on Türk Telekom (2026-10): the DPI box sits
/// ~2 hops away, reassembles split/disordered segments and injects RSTs, so splitting alone is unreliable;
/// a decoy ClientHello that reaches the DPI (TTL >= 3) but not the server is what makes it work. Decoys with a
/// wrong checksum or wrong sequence number are ignored by this DPI.
/// </summary>
public static class BuiltInProfiles
{
    /// <summary>Fake (auto TTL, x2) + split at byte 2 + disorder + DoH + block QUIC.</summary>
    public static BypassProfile Standard { get; } = new()
    {
        Id = "standard",
        Name = "Türkiye Standart",
        Description = "Sahte paket (otomatik TTL, x2) + 2. baytta bölme + ters sıra + DoH + QUIC engelleme",
        SplitTlsHello = true,
        SplitAtSni = false,
        SplitPosition = 2,
        DisorderSegments = true,
        FakePacket = true,
        FakeTtl = 0,
        FakeRepeats = 2,
        Dns = DnsMode.DoH,
        BlockQuic = true,
    };

    /// <summary>Split only + DoH (no decoy; weak against reassembling DPI).</summary>
    public static BypassProfile Light { get; } = new()
    {
        Id = "light",
        Name = "Hafif",
        Description = "Yalnızca SNI'de bölme + DoH (sahte paket yok; birçok hatta yetersiz)",
        SplitTlsHello = true,
        Dns = DnsMode.DoH,
    };

    /// <summary>Fake (auto TTL, x3) + split at byte 1 + disorder + DoH + block QUIC.</summary>
    public static BypassProfile Aggressive { get; } = new()
    {
        Id = "aggressive",
        Name = "Agresif",
        Description = "Sahte paket (otomatik TTL, x3) + 1. baytta bölme + ters sıra + DoH + QUIC engelleme",
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
        Name = "Superonline",
        Description = "Sahte paket (TTL 3) + bölme + DoH",
        SplitTlsHello = true,
        FakePacket = true,
        FakeTtl = 3,
        Dns = DnsMode.DoH,
    };

    /// <summary>Fake TTL 5 + split at byte 2 + disorder + DoH + block QUIC (best probe result on TT).</summary>
    public static BypassProfile Ttnet { get; } = new()
    {
        Id = "ttnet",
        Name = "Türk Telekom",
        Description = "Sahte paket (TTL 5) + 2. baytta bölme + ters sıra + DoH + QUIC engelleme",
        SplitTlsHello = true,
        SplitAtSni = false,
        SplitPosition = 2,
        DisorderSegments = true,
        FakePacket = true,
        FakeTtl = 5,
        Dns = DnsMode.DoH,
        BlockQuic = true,
    };

    /// <summary>All built-in profiles.</summary>
    public static IReadOnlyList<BypassProfile> All { get; } = [Standard, Light, Aggressive, Superonline, Ttnet];
}
