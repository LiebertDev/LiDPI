namespace LiDPI.Core;

/// <summary>Live counters of an engine. Thread-safe.</summary>
public sealed class EngineStats
{
    private long _packetsProcessed;
    private long _connectionsHandled;
    private long _dnsQueriesResolved;
    private long _clientHellosSeen;
    private long _clientHellosModified;
    private long _fakePacketsSent;
    private long _startedAtTicks; // 0 = null

    public long PacketsProcessed => Interlocked.Read(ref _packetsProcessed);
    public long ConnectionsHandled => Interlocked.Read(ref _connectionsHandled);
    public long DnsQueriesResolved => Interlocked.Read(ref _dnsQueriesResolved);
    /// <summary>TLS ClientHello packets that reached the packet handler.</summary>
    public long ClientHellosSeen => Interlocked.Read(ref _clientHellosSeen);
    /// <summary>ClientHello packets that were split and/or preceded by a fake.</summary>
    public long ClientHellosModified => Interlocked.Read(ref _clientHellosModified);
    /// <summary>Decoy packets injected.</summary>
    public long FakePacketsSent => Interlocked.Read(ref _fakePacketsSent);

    /// <summary>UTC time the engine reached Running, or null.</summary>
    public DateTime? StartedAt
    {
        get { var t = Interlocked.Read(ref _startedAtTicks); return t == 0 ? null : new DateTime(t, DateTimeKind.Utc); }
        set => Interlocked.Exchange(ref _startedAtTicks, value?.ToUniversalTime().Ticks ?? 0);
    }

    public long IncrementPackets(long by = 1) => Interlocked.Add(ref _packetsProcessed, by);
    public long IncrementConnections(long by = 1) => Interlocked.Add(ref _connectionsHandled, by);
    public long IncrementDnsQueries(long by = 1) => Interlocked.Add(ref _dnsQueriesResolved, by);
    public long IncrementClientHellosSeen(long by = 1) => Interlocked.Add(ref _clientHellosSeen, by);
    public long IncrementClientHellosModified(long by = 1) => Interlocked.Add(ref _clientHellosModified, by);
    public long IncrementFakePackets(long by = 1) => Interlocked.Add(ref _fakePacketsSent, by);

    /// <summary>Zero all counters and clear <see cref="StartedAt"/>.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _packetsProcessed, 0);
        Interlocked.Exchange(ref _connectionsHandled, 0);
        Interlocked.Exchange(ref _dnsQueriesResolved, 0);
        Interlocked.Exchange(ref _clientHellosSeen, 0);
        Interlocked.Exchange(ref _clientHellosModified, 0);
        Interlocked.Exchange(ref _fakePacketsSent, 0);
        Interlocked.Exchange(ref _startedAtTicks, 0);
    }
}
