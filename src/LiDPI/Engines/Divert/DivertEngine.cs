using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LiDPI.Core;
using LiDPI.Dns;
using LiDPI.UI;

namespace LiDPI.Engines.Divert;

/// <summary>
/// System-wide packet engine built on WinDivert 2.2: TLS ClientHello splitting / disorder / fake
/// injection, HTTP Host obfuscation, QUIC blocking, transparent DNS-over-HTTPS and the "Hızlı DNS" UDP redirect.
/// </summary>
public sealed class DivertEngine : IBypassEngine
{
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoData = 232;
    private const int ErrorOperationAborted = 995;

    private const int MaxDnsInFlight = 256;
    private const int MaxDnsUdpResponse = 4096;
    private const byte FallbackFakeTtl = 4;
    private const int MinAutoFakeTtl = 3;
    /// <summary>
    /// Upper bound of the auto TTL. The hop count read from a SYN-ACK is only an estimate: CDN edges (Akamai,
    /// Cloudflare, Fastly, Meta measured on Türk Telekom 2026-10) answer with non-standard initial TTLs or over
    /// longer return paths, so "hops - 2" often let the decoy reach the server, which then aborted the handshake
    /// with a TLS alert (57 failed connections in one browsing session; Instagram never loaded). The DPI box is
    /// 2 hops away and no server was seen closer than 5 hops, so 5 (a common Türkiye setting) is safe.
    /// </summary>
    private const int MaxAutoFakeTtl = 5;
    /// <summary>"Hızlı DNS": how long an IPv6 query forwarded over IPv4 waits for the resolver.</summary>
    private const int FastDnsForwardTimeoutMs = 3000;
    private const int MaxHopCacheEntries = 16384;
    /// <summary>Learned hop counts go stale (route / network changes): the whole cache is dropped this often.</summary>
    private const long HopCacheLifetimeMs = 30 * 60 * 1000;
    private const string DriverServiceName = "WinDivert";
    /// <summary>Blocked QUIC: ICMP "port unreachable" answers per second at most (the rest is only dropped).</summary>
    private const int MaxQuicRejectsPerSecond = 50;
    /// <summary>A QUIC flow (destination + client port) gets one ICMP answer per this period; its other packets are dropped.</summary>
    private const long QuicFlowMemoryMs = 10_000;
    private const int MaxQuicFlows = 4096;
    /// <summary>"Hızlı DNS": how long a redirected query's original destination is remembered for its replies.</summary>
    private const long FastDnsFlowMs = 10_000;
    private const int MaxFastDnsFlows = 8192;

    /// <summary>Process names (lowercase, no .exe) of other DPI tools that conflict with this engine.</summary>
    internal static readonly string[] ConflictingTools = ["goodbyedpi", "winws", "zapret", "splitwire"];

    private const string SniffFilter =
        "inbound and !loopback and !impostor and tcp and tcp.Syn and tcp.Ack and tcp.SrcPort == 443";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<UInt128, byte> _hopTtl = new();
    private long _hopCacheSince = Environment.TickCount64;   // sniff thread only

    private BypassProfile _profile = BuiltInProfiles.Standard;
    private IntPtr _main = WinDivertNative.InvalidHandle;
    private IntPtr _sniff = WinDivertNative.InvalidHandle;
    private Thread? _mainThread;
    private Thread? _sniffThread;
    private DohResolver? _resolver;
    private TcpDnsResponder? _tcpDns;
    private CancellationTokenSource? _cts;
    private byte[] _segBuf = [];
    private byte[] _fakeBuf = [];
    private volatile bool _stopping;
    private volatile bool _closing;
    private volatile bool _loopFailed;
    private bool _ownsDriver;   // WinDivert service was not running before our first open
    private bool _handled;      // main thread only: something was sent for the current packet
    private int _activeSenders;
    private int _dnsInFlight;
    private EventHandler? _onProcessExit;
    private UnhandledExceptionEventHandler? _onUnhandled;

    private long _warnWindow, _debugWindow;
    private int _warnCount, _debugCount;

    // blocked QUIC (main thread only)
    private readonly Dictionary<(UInt128 Dst, ushort SrcPort), long> _quicFlows = new();
    private readonly byte[] _icmpBuf = new byte[1280];
    private long _quicWindow;
    private int _quicCount;

    // "Hızlı DNS" UDP redirect (set in StartCore; the flow table is used by the main thread only)
    private byte[]? _fastAddr4, _fastAddr6;
    private ushort _fastPort4, _fastPort6;
    /// <summary>IPv4 resolver; IPv6-transport queries are forwarded to it when there is no global IPv6.</summary>
    private System.Net.IPEndPoint? _fastEp4;
    private readonly Dictionary<(bool V6, ushort ClientPort, ushort Id), FastDnsFlow> _fastFlows = new();
    private long _fastPruneAt;

    /// <summary>Where a redirected query was originally sent (restored as the source of its replies).</summary>
    private readonly record struct FastDnsFlow(byte[] Server, ushort ServerPort, long ExpiresAt);

    public EngineMode Mode => EngineMode.Divert;
    public string DisplayName => Strings.ModeAll;
    public bool RequiresAdmin => true;
    public EngineState State { get; private set; } = EngineState.Stopped;
    public EngineStats Stats { get; } = new();

    public event EventHandler<EngineState>? StateChanged;
    public event EventHandler<LogEntry>? Log;

    public async Task StartAsync(BypassProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is EngineState.Running or EngineState.Starting) return;
            _profile = profile;
            SetState(EngineState.Starting);
            try
            {
                await Task.Run(() => StartCore(ct), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Teardown(fromExit: false);
                SetState(EngineState.Stopped);
                throw;
            }
            catch (Exception ex)
            {
                Write("Error", ex is DivertStartException ? ex.Message : Strings.LogDivertStartFailed(ex.Message));
                Teardown(fromExit: false);
                SetState(EngineState.Error);
                return;
            }
            Stats.StartedAt = DateTime.UtcNow;
            SetState(EngineState.Running);
            Interlocked.MemoryBarrier();
            if (_loopFailed) SetState(EngineState.Error);   // receive loop died during start-up
            else FlushDnsAfterStart();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == EngineState.Stopped) return;
            bool wasRunning = State == EngineState.Running;
            SetState(EngineState.Stopping);
            await Task.Run(() => Teardown(fromExit: false)).ConfigureAwait(false);
            SetState(EngineState.Stopped);
            if (wasRunning) Write("Info", Strings.LogDivertStopped);
            if (DnsCache.Flush()) Write("Info", Strings.LogDnsFlushed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops poisoned answers the ISP resolver may have cached while LiDPI was off, so apps re-resolve via DoH.
    /// </summary>
    private void FlushDnsAfterStart()
    {
        if (DnsCache.Flush()) Write("Info", Strings.LogDnsFlushed);
        Write("Info", Strings.LogRestartAppsHint);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------ lifecycle

    private void StartCore(CancellationToken ct)
    {
        if (!IsAdministrator())
            throw new DivertStartException(WinDivertNative.DescribeOpenError(5));

        WarnAboutConflicts();

        try
        {
            WinDivertLoader.EnsureLoaded();
        }
        catch (Exception ex)
        {
            throw new DivertStartException(Strings.ErrDivertPrepare(ex.Message));
        }
        ct.ThrowIfCancellationRequested();

        var p = _profile;
        bool doh = p.Dns == DnsMode.DoH;
        bool fast = p.Dns == DnsMode.UdpRedirect;
        _fastAddr4 = _fastAddr6 = null;
        _fastPort4 = _fastPort6 = 0;
        _fastEp4 = null;
        string? inbound = null;
        if (fast)
        {
            var ep4 = FastDns.Resolve(p.FastDnsServer, System.Net.Sockets.AddressFamily.InterNetwork);
            var ep6 = FastDns.Resolve(p.FastDnsServerV6, System.Net.Sockets.AddressFamily.InterNetworkV6);
            _fastEp4 = ep4;
            _fastAddr4 = ep4.Address.GetAddressBytes();
            _fastPort4 = (ushort)ep4.Port;
            var from = new List<string>(2) { $"(ip.SrcAddr == {ep4.Address} and udp.SrcPort == {ep4.Port})" };
            // without global IPv6 the IPv6 resolver is unreachable: IPv6-transport queries (e.g. to the router's
            // fe80:: address, which answers from the ISP's poisoned resolver) are then forwarded over IPv4
            if (FastDns.HasGlobalIpv6())
            {
                _fastAddr6 = ep6.Address.GetAddressBytes();
                _fastPort6 = (ushort)ep6.Port;
                from.Add($"(ipv6.SrcAddr == {ep6.Address} and udp.SrcPort == {ep6.Port})");
            }
            inbound = "inbound and !loopback and !impostor and udp and (" + string.Join(" or ", from) + ")";
        }
        string? filter = BuildMainFilter(p, doh || fast, inbound);

        _stopping = false;
        _closing = false;
        _loopFailed = false;
        _hopTtl.Clear();
        _quicFlows.Clear();
        _fastFlows.Clear();
        _quicWindow = 0;
        _quicCount = 0;
        _fastPruneAt = 0;
        _cts = new CancellationTokenSource();
        _segBuf = GC.AllocateArray<byte>(WinDivertNative.MtuMax, pinned: true);
        _fakeBuf = GC.AllocateArray<byte>(WinDivertNative.MtuMax, pinned: true);

        if (doh)
        {
            _resolver = new DohResolver(DohResolver.PrepareProvider(p.DohProviderName, m => Write("Info", m)));
            _resolver.Warning += m => LimitedWrite("Warn", m);
            var resolver = _resolver;
            _tcpDns = new TcpDnsResponder(resolver.QueryRawAsync, SendInbound, _cts.Token,
                warn: m => { if (!_stopping) LimitedWrite("Warn", m); },
                answered: () => Stats.IncrementDnsQueries());
        }

        if (filter is null)
        {
            Write("Warn", Strings.LogNoTechniques);
        }
        else
        {
            if (WinDivertNative.CheckFilter(filter) is { } err)
                throw new DivertStartException(Strings.ErrDivertFilter(err));
            if (WinDivertLoader.SysPath is { } sysPath)
                DriverServices.DeleteIfStale(DriverServiceName, sysPath, m => Write("Info", m));
            bool driverWasRunning = DriverServices.IsRunning(DriverServiceName);
            _main = Open(filter, 0, 0);
            _ownsDriver = !driverWasRunning;

            if (p.FakePacket && p.FakeTtl <= 0)
            {
                try
                {
                    _sniff = Open(SniffFilter, 0, WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
                }
                catch (DivertStartException ex)
                {
                    Write("Warn", Strings.LogAutoTtlOff(FallbackFakeTtl, ex.Message));
                }
            }
        }

        Stats.Reset();
        _onProcessExit = (_, _) => SafeTeardownOnExit();
        _onUnhandled = (_, _) => SafeTeardownOnExit();
        AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
        AppDomain.CurrentDomain.UnhandledException += _onUnhandled;

        if (_main != WinDivertNative.InvalidHandle)
        {
            _mainThread = new Thread(MainLoop) { IsBackground = true, Name = "LiDPI divert", Priority = ThreadPriority.AboveNormal };
            _mainThread.Start();
        }
        if (_sniff != WinDivertNative.InvalidHandle)
        {
            _sniffThread = new Thread(SniffLoop) { IsBackground = true, Name = "LiDPI divert sniff" };
            _sniffThread.Start();
        }

        Write("Info", Strings.LogDivertRunning(Describe(p)));
        // open the DoH connection now, before the DNS cache flush makes every app resolve again
        _resolver?.Warmup();
    }

    private static IntPtr Open(string filter, short priority, ulong flags)
    {
        IntPtr h = WinDivertNative.WinDivertOpen(filter, WinDivertNative.LayerNetwork, priority, flags);
        if (h == WinDivertNative.InvalidHandle || h == IntPtr.Zero)
            throw new DivertStartException(WinDivertNative.DescribeOpenError(Marshal.GetLastPInvokeError()));
        return h;
    }

    private void Teardown(bool fromExit)
    {
        _stopping = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }

        IntPtr main = _main, sniff = _sniff;
        if (main != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertShutdown(main, WinDivertNative.ShutdownRecv);
        if (sniff != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertShutdown(sniff, WinDivertNative.ShutdownRecv);

        if (!fromExit)
        {
            JoinQuietly(_mainThread);
            JoinQuietly(_sniffThread);
        }

        // reset the emulated TCP/53 connections while the handle can still send (clients retry at once
        // instead of waiting for a dead connection)
        try { _tcpDns?.AbortAll(); } catch { /* best effort */ }

        _closing = true;
        Interlocked.MemoryBarrier();
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _activeSenders) != 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(1);

        main = Interlocked.Exchange(ref _main, WinDivertNative.InvalidHandle);
        sniff = Interlocked.Exchange(ref _sniff, WinDivertNative.InvalidHandle);
        if (main != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(main);
        if (sniff != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(sniff);

        if (fromExit) return;

        _mainThread = null;
        _sniffThread = null;
        if (_onProcessExit is not null) AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        if (_onUnhandled is not null) AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
        _onProcessExit = null;
        _onUnhandled = null;

        _tcpDns?.Dispose();
        _tcpDns = null;
        sw.Restart();
        while (Volatile.Read(ref _dnsInFlight) != 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(5);
        _resolver?.Dispose();
        _resolver = null;
        _cts?.Dispose();
        _cts = null;
        _hopTtl.Clear();
        _quicFlows.Clear();
        _fastFlows.Clear();

        // Only stop the driver if we started it (other tools may share it); our handles are closed by now.
        if (_ownsDriver)
        {
            _ownsDriver = false;
            DriverServices.Stop(DriverServiceName, m => Write("Info", m));
        }
    }

    private void SafeTeardownOnExit()
    {
        try { Teardown(fromExit: true); } catch { /* process is going away */ }
    }

    private static void JoinQuietly(Thread? t)
    {
        try { t?.Join(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ receive loops

    private unsafe void MainLoop()
    {
        string? failure = null;
        try
        {
            IntPtr h = _main;
            var buf = GC.AllocateArray<byte>(WinDivertNative.MtuMax, pinned: true);
            int consecutiveErrors = 0;
            fixed (byte* p = buf)
            {
                WinDivertAddress addr;
                uint len;
                while (true)
                {
                    if (!WinDivertNative.WinDivertRecv(h, p, (uint)buf.Length, &len, &addr))
                    {
                        int err = Marshal.GetLastPInvokeError();
                        if (err is ErrorNoData or ErrorInvalidHandle or ErrorOperationAborted || _stopping)
                        {
                            if (!_stopping) failure = Strings.FailWinDivertError(err);
                            break;
                        }
                        if (err == ErrorInsufficientBuffer) continue;
                        LimitedWrite("Warn", Strings.LogRecvFailed(err));
                        if (++consecutiveErrors > 100) Thread.Sleep(10);
                        continue;
                    }
                    consecutiveErrors = 0;
                    Stats.IncrementPackets();
                    var origAddr = addr;
                    _handled = false;
                    try
                    {
                        HandleOutbound(buf.AsSpan(0, (int)len), ref addr);
                    }
                    catch (Exception ex)
                    {
                        LimitedWrite("Warn", Strings.LogPacketFailed(ex.Message));
                        // nothing went out for this packet yet: pass the original through so it isn't lost
                        if (!_handled)
                        {
                            try { WinDivertNative.SendRaw(h, buf.AsSpan(0, (int)len), ref origAddr); }
                            catch { /* ignore */ }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        if (!_stopping) FailClosed(failure ?? Strings.FailUnknown);
    }

    /// <summary>
    /// The receive loop ended while the engine should be running: close the WinDivert handles at once
    /// so diverted traffic is not black-holed, and report the error.
    /// </summary>
    private void FailClosed(string reason)
    {
        _loopFailed = true;
        Interlocked.MemoryBarrier();
        try
        {
            IntPtr sniff = _sniff, main = _main;
            if (sniff != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertShutdown(sniff, WinDivertNative.ShutdownRecv);
            if (main != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertShutdown(main, WinDivertNative.ShutdownBoth);
            JoinQuietly(_sniffThread);

            _closing = true;
            Interlocked.MemoryBarrier();
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref _activeSenders) != 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(1);

            main = Interlocked.Exchange(ref _main, WinDivertNative.InvalidHandle);
            sniff = Interlocked.Exchange(ref _sniff, WinDivertNative.InvalidHandle);
            if (main != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(main);
            if (sniff != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(sniff);
        }
        catch { /* best effort */ }

        Write("Error", Strings.LogLoopDied(reason));
        if (State == EngineState.Running) SetState(EngineState.Error);
    }

    private unsafe void SniffLoop()
    {
        try
        {
            IntPtr h = _sniff;
            var buf = GC.AllocateArray<byte>(2048, pinned: true);
            fixed (byte* p = buf)
            {
                WinDivertAddress addr;
                uint len;
                while (true)
                {
                    if (!WinDivertNative.WinDivertRecv(h, p, (uint)buf.Length, &len, &addr))
                    {
                        int err = Marshal.GetLastPInvokeError();
                        if (err is ErrorNoData or ErrorInvalidHandle or ErrorOperationAborted || _stopping) break;
                        if (err != ErrorInsufficientBuffer) Thread.Sleep(1);
                        continue;
                    }
                    var pkt = new ReadOnlySpan<byte>(p, (int)len);
                    if (pkt.Length < 20) continue;
                    LearnHops(PacketInfo.SrcKey(pkt), PacketInfo.GetTtl(pkt));
                }
            }
        }
        catch (Exception ex)
        {
            Write("Warn", Strings.LogTtlListenerStopped(ex.Message));
        }
    }

    private void LearnHops(UInt128 key, byte ttl)
    {
        int initial = ttl <= 64 ? 64 : ttl <= 128 ? 128 : 255;
        int hops = initial - ttl;
        // The decoy must pass the DPI box (measured on Türk Telekom: 2 hops away, so TTL >= 3) but die
        // before the server: hops-2 within [MinAutoFakeTtl, MaxAutoFakeTtl], never more than hops-1.
        int fakeTtl = hops - 1 >= MinAutoFakeTtl ? Math.Clamp(hops - 2, MinAutoFakeTtl, MaxAutoFakeTtl) : hops - 1;
        byte fake = (byte)Math.Clamp(fakeTtl, 1, 255);
        long now = Environment.TickCount64;
        if (_hopTtl.Count >= MaxHopCacheEntries || now - _hopCacheSince > HopCacheLifetimeMs)
        {
            // bounded + expiring; every flow re-learns from its own SYN-ACK before its ClientHello anyway
            _hopTtl.Clear();
            _hopCacheSince = now;
        }
        _hopTtl[key] = fake;
    }

    // ------------------------------------------------------------------ packet handling

    private void HandleOutbound(Span<byte> pkt, ref WinDivertAddress addr)
    {
        if (!PacketInfo.TryParse(pkt, out var pi))
        {
            Reinject(pkt, ref addr);
            return;
        }
        pkt = pkt[..pi.TotalLength];
        var payload = pkt.Slice(pi.PayloadOffset, pi.PayloadLength);

        if (!addr.Outbound)
        {
            // only replies of the "Hızlı DNS" resolver are captured inbound
            if (pi.Protocol == PacketInfo.ProtoUdp && TryRestoreFastDnsReply(pkt, pi, ref addr)) return;
            Reinject(pkt, ref addr);
            return;
        }

        if (pi.Protocol == PacketInfo.ProtoUdp)
        {
            if (pi.DstPort == 443 && _profile.BlockQuic)
            {
                // QUIC is dropped; the first packet of a flow gets a "port unreachable" so browsers switch to TCP
                // at once instead of waiting for the QUIC handshake to time out
                _handled = true;
                try { RejectQuic(pkt, pi, addr); }
                catch { /* the drop is what matters */ }
                return;
            }
            if (pi.DstPort == 53)
            {
                if (_resolver is not null && TryQueueDns(pkt, pi, addr)) return;
                if (_fastAddr4 is not null && TryRedirectFastDns(pkt, pi, ref addr)) return;
                if (pi.IsV6 && _fastAddr6 is null && _fastEp4 is not null && TryForwardFastDns(pkt, pi, addr)) return;
            }
        }
        else if (pi.Protocol == PacketInfo.ProtoTcp && pi.DstPort == 53)
        {
            // DoH mode: every TCP/53 segment is consumed (answered, or reset when unknown) — never reinjected
            // to the possibly poisoned network resolver
            if (_tcpDns is { } tcpDns && tcpDns.Handle(pkt, pi, addr))
            {
                _handled = true;
                return;
            }
        }
        else if (pi.Protocol == PacketInfo.ProtoTcp && pi.PayloadLength > 0)
        {
            if (pi.DstPort == 443 && TlsClientHello.IsClientHello(payload) && HandleClientHello(pkt, pi, ref addr))
                return;
            if (pi.DstPort == 80 && _profile.HttpHostTricks && HttpHostTrick.LooksLikeRequest(payload) && HandleHttp(pkt, pi, ref addr))
                return;
        }
        Reinject(pkt, ref addr);
    }

    private void Reinject(Span<byte> pkt, ref WinDivertAddress addr)
    {
        WinDivertNative.SendRaw(_main, pkt, ref addr);
        _handled = true;
    }

    private bool HandleClientHello(Span<byte> pkt, in PacketInfo pi, ref WinDivertAddress addr)
    {
        var p = _profile;
        var payload = pkt.Slice(pi.PayloadOffset, pi.PayloadLength);
        Stats.IncrementClientHellosSeen();
        int split = p.SplitTlsHello ? TlsClientHello.ChooseSplit(payload, p) : -1;
        if (split < 0 && !p.FakePacket) return false;

        Stats.IncrementConnections();
        Stats.IncrementClientHellosModified();
        if (DebugAllowed()) Write("Debug", $"TLS: {TlsClientHello.GetSni(payload) ?? "(no SNI)"} ({pi.PayloadLength} B, split {split})");

        if (p.FakePacket)
        {
            int repeats = Math.Clamp(p.FakeRepeats, 1, 5);
            for (int i = 0; i < repeats; i++) SendFake(pkt, pi, addr);
        }

        if (split < 0)
        {
            Reinject(pkt, ref addr);
        }
        else if (p.DisorderSegments)
        {
            SendSegment(pkt, pi, split, pi.PayloadLength - split, 1, addr);
            SendSegment(pkt, pi, 0, split, 0, addr);
        }
        else
        {
            SendSegment(pkt, pi, 0, split, 0, addr);
            SendSegment(pkt, pi, split, pi.PayloadLength - split, 1, addr);
        }
        return true;
    }

    private bool HandleHttp(Span<byte> pkt, in PacketInfo pi, ref WinDivertAddress addr)
    {
        var payload = pkt.Slice(pi.PayloadOffset, pi.PayloadLength);
        if (!HttpHostTrick.TryApply(payload, out int split)) return false;   // untouched

        Stats.IncrementConnections();
        if (split <= 0 || split >= pi.PayloadLength)
        {
            WinDivertNative.Send(_main, pkt, ref addr, recalc: true);
            _handled = true;
            return true;
        }
        SendSegment(pkt, pi, 0, split, 0, addr);
        SendSegment(pkt, pi, split, pi.PayloadLength - split, 1, addr);
        return true;
    }

    /// <summary>Sends payload[start..start+len] with the original headers, fixed seq/lengths/checksums.</summary>
    private void SendSegment(ReadOnlySpan<byte> pkt, in PacketInfo pi, int start, int len, int ipIdDelta, WinDivertAddress addr)
    {
        int n = PacketBuilder.BuildSegment(pkt, pi, start, len, ipIdDelta, _segBuf);
        WinDivertNative.Send(_main, _segBuf.AsSpan(0, n), ref addr, recalc: true);
        _handled = true;
    }

    /// <summary>Sends a decoy ClientHello with the real sequence number but a TTL that dies before the server.</summary>
    private void SendFake(ReadOnlySpan<byte> pkt, in PacketInfo pi, WinDivertAddress addr)
    {
        int n = PacketBuilder.BuildFake(pkt, pi, FakeTtlFor(pkt), _profile.FakeWrongSeq, _fakeBuf);
        if (n <= 0) return;
        var fake = _fakeBuf.AsSpan(0, n);
        WinDivertNative.CalcChecksums(fake, ref addr);
        if (_profile.WrongChecksumFake) PacketBuilder.CorruptTcpChecksum(fake, pi);
        if (WinDivertNative.SendRaw(_main, fake, ref addr)) Stats.IncrementFakePackets();
    }

    private byte FakeTtlFor(ReadOnlySpan<byte> pkt)
    {
        int fixedTtl = _profile.FakeTtl;
        if (fixedTtl > 0) return (byte)Math.Min(fixedTtl, 255);
        return _hopTtl.TryGetValue(PacketInfo.DstKey(pkt), out byte t) ? t : FallbackFakeTtl;
    }

    // ------------------------------------------------------------------ blocked QUIC

    private void RejectQuic(ReadOnlySpan<byte> pkt, in PacketInfo pi, in WinDivertAddress addr)
    {
        long now = Environment.TickCount64;
        var key = (PacketInfo.DstKey(pkt), pi.SrcPort);
        if (_quicFlows.TryGetValue(key, out long first) && now - first < QuicFlowMemoryMs) return;
        if (_quicFlows.Count >= MaxQuicFlows) _quicFlows.Clear();
        _quicFlows[key] = now;

        if (now - _quicWindow >= 1000)
        {
            _quicWindow = now;
            _quicCount = 0;
        }
        if (++_quicCount > MaxQuicRejectsPerSecond) return;

        int n = PacketBuilder.BuildPortUnreachable(pkt, pi, _icmpBuf);
        if (n > 0) SendInbound(_icmpBuf.AsSpan(0, n), addr);
    }

    // ------------------------------------------------------------------ "Hızlı DNS" (UDP redirect)

    /// <summary>
    /// NATs an outbound DNS query to the configured resolver (destination address and port rewritten, checksums
    /// recomputed) and remembers the original destination for the replies. False = not handled (passed on as is).
    /// </summary>
    private bool TryRedirectFastDns(Span<byte> pkt, in PacketInfo pi, ref WinDivertAddress addr)
    {
        if (pi.PayloadLength < 12 || (pkt[pi.PayloadOffset + 2] & 0x80) != 0) return false;   // not a query
        byte[]? server = pi.IsV6 ? _fastAddr6 : _fastAddr4;
        if (server is null) return false;
        ushort serverPort = pi.IsV6 ? _fastPort6 : _fastPort4;

        long now = Environment.TickCount64;
        PruneFastDnsFlows(now);
        var dst = pi.IsV6 ? pkt.Slice(24, 16) : pkt.Slice(16, 4);
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(pkt[pi.PayloadOffset..]);
        _fastFlows[(pi.IsV6, pi.SrcPort, id)] = new FastDnsFlow(dst.ToArray(), pi.DstPort, now + FastDnsFlowMs);

        server.AsSpan().CopyTo(dst);
        BinaryPrimitives.WriteUInt16BigEndian(pkt[(pi.L4Offset + 2)..], serverPort);
        WinDivertNative.Send(_main, pkt, ref addr, recalc: true);
        _handled = true;
        return true;
    }

    /// <summary>Gives a reply of the resolver the source the client asked (original address and port 53).</summary>
    private bool TryRestoreFastDnsReply(Span<byte> pkt, in PacketInfo pi, ref WinDivertAddress addr)
    {
        byte[]? server = pi.IsV6 ? _fastAddr6 : _fastAddr4;
        if (server is null || pi.PayloadLength < 12 || pi.SrcPort != (pi.IsV6 ? _fastPort6 : _fastPort4)) return false;
        var src = pi.IsV6 ? pkt.Slice(8, 16) : pkt.Slice(12, 4);
        if (!src.SequenceEqual(new ReadOnlySpan<byte>(server))) return false;
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(pkt[pi.PayloadOffset..]);
        if (!_fastFlows.TryGetValue((pi.IsV6, pi.DstPort, id), out var flow) || flow.ExpiresAt < Environment.TickCount64)
            return false;

        flow.Server.AsSpan().CopyTo(src);
        BinaryPrimitives.WriteUInt16BigEndian(pkt[pi.L4Offset..], flow.ServerPort);
        WinDivertNative.Send(_main, pkt, ref addr, recalc: true);
        _handled = true;
        Stats.IncrementDnsQueries();
        return true;
    }

    private void PruneFastDnsFlows(long now)
    {
        if (_fastFlows.Count < MaxFastDnsFlows && now < _fastPruneAt) return;
        _fastPruneAt = now + 5000;
        List<(bool V6, ushort ClientPort, ushort Id)>? expired = null;
        foreach (var kv in _fastFlows)
            if (kv.Value.ExpiresAt < now) (expired ??= new List<(bool V6, ushort ClientPort, ushort Id)>()).Add(kv.Key);
        if (expired is not null)
            foreach (var k in expired) _fastFlows.Remove(k);
        if (_fastFlows.Count >= MaxFastDnsFlows) _fastFlows.Clear();
    }

    private readonly record struct ForwardJob(byte[] Packet, int Length, PacketInfo Info, WinDivertAddress Addr, System.Net.IPEndPoint Server, CancellationToken Token);

    /// <summary>
    /// "Hızlı DNS" without global IPv6: an IPv6-transport query cannot be NAT-ed to the IPv4 resolver, so it is
    /// consumed here, asked over an IPv4 socket and answered with an injected IPv6 reply from the address the client
    /// asked. Left alone it would reach the router's resolver and get the ISP's poisoned answer first.
    /// </summary>
    private bool TryForwardFastDns(ReadOnlySpan<byte> pkt, in PacketInfo pi, in WinDivertAddress addr)
    {
        var server = _fastEp4;
        var cts = _cts;
        if (server is null || cts is null || pi.PayloadLength < 12) return false;
        if ((pkt[pi.PayloadOffset + 2] & 0x80) != 0) return false;   // not a query
        if (Interlocked.Increment(ref _dnsInFlight) > MaxDnsInFlight)
        {
            Interlocked.Decrement(ref _dnsInFlight);
            return false;
        }
        var copy = ArrayPool<byte>.Shared.Rent(pkt.Length);
        pkt.CopyTo(copy);
        var job = new ForwardJob(copy, pkt.Length, pi, addr, server, cts.Token);
        _ = Task.Run(() => ForwardFastDnsAsync(job));
        _handled = true;
        return true;
    }

    private async Task ForwardFastDnsAsync(ForwardJob job)
    {
        byte[]? outBuf = null;
        try
        {
            var pi = job.Info;
            var query = job.Packet.AsMemory(pi.PayloadOffset, pi.PayloadLength);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
            timeout.CancelAfter(FastDnsForwardTimeoutMs);
            using var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            udp.Connect(job.Server);
            await udp.SendAsync(query, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                byte[] resp = r.Buffer;
                if (resp.Length < 12 || resp[0] != query.Span[0] || resp[1] != query.Span[1] || (resp[2] & 0x80) == 0) continue;
                outBuf = ArrayPool<byte>.Shared.Rent(40 + 8 + resp.Length);
                int total = PacketBuilder.BuildUdpReply(job.Packet.AsSpan(0, job.Length), pi, resp, outBuf);
                if (total > 0 && SendInbound(outBuf.AsSpan(0, total), job.Addr)) Stats.IncrementDnsQueries();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // stopping, or no answer in time: the client retries / asks its other resolver
        }
        catch (Exception ex)
        {
            if (!_stopping) LimitedWrite("Warn", Strings.LogDnsInjectFailed(ex.Message));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(job.Packet);
            if (outBuf is not null) ArrayPool<byte>.Shared.Return(outBuf);
            Interlocked.Decrement(ref _dnsInFlight);
        }
    }

    // ------------------------------------------------------------------ DNS over HTTPS

    private readonly record struct DnsJob(byte[] Packet, int Length, PacketInfo Info, WinDivertAddress Addr, DohResolver Resolver, CancellationToken Token);

    private bool TryQueueDns(ReadOnlySpan<byte> pkt, in PacketInfo pi, in WinDivertAddress addr)
    {
        var resolver = _resolver;
        var cts = _cts;
        if (resolver is null || cts is null || pi.PayloadLength < 12) return false;
        if ((pkt[pi.PayloadOffset + 2] & 0x80) != 0) return false;   // already a response
        if (Interlocked.Increment(ref _dnsInFlight) > MaxDnsInFlight)
        {
            Interlocked.Decrement(ref _dnsInFlight);
            return false;
        }
        var copy = ArrayPool<byte>.Shared.Rent(pkt.Length);
        pkt.CopyTo(copy);
        var job = new DnsJob(copy, pkt.Length, pi, addr, resolver, cts.Token);
        _ = Task.Run(() => ProcessDnsAsync(job));
        _handled = true;
        return true;
    }

    private async Task ProcessDnsAsync(DnsJob job)
    {
        byte[]? outBuf = null;
        try
        {
            var pi = job.Info;
            byte[] query = job.Packet.AsSpan(pi.PayloadOffset, pi.PayloadLength).ToArray();
            byte[] resp;
            try
            {
                resp = await job.Resolver.QueryRawAsync(query, job.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_stopping) return;
                LimitedWrite("Warn", Strings.LogDohPassthrough(ex.Message));
                var orig = job.Addr;
                GuardedSend(job.Packet.AsSpan(0, job.Length), ref orig, recalc: false);
                return;
            }

            resp[0] = query[0];   // keep the client's transaction id
            resp[1] = query[1];
            // Honour the client's UDP size (512 or its EDNS size): strip authority/additional first; only if the
            // answers alone do not fit is TC set — the TCP retry is then answered by TcpDnsResponder via DoH.
            resp = DnsWire.FitToUdp(resp, DnsWire.GetClientUdpLimit(query, MaxDnsUdpResponse))
                   ?? DnsWire.BuildTruncatedReply(query) ?? resp;

            outBuf = ArrayPool<byte>.Shared.Rent(40 + 8 + resp.Length);
            int total = PacketBuilder.BuildUdpReply(job.Packet.AsSpan(0, job.Length), pi, resp, outBuf);
            if (total <= 0) return;
            var o = outBuf.AsSpan(0, total);

            var addr = job.Addr;
            if (SendInbound(o, addr)) Stats.IncrementDnsQueries();
        }
        catch (Exception ex)
        {
            if (!_stopping) LimitedWrite("Warn", Strings.LogDnsInjectFailed(ex.Message));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(job.Packet);
            if (outBuf is not null) ArrayPool<byte>.Shared.Return(outBuf);
            Interlocked.Decrement(ref _dnsInFlight);
        }
    }

    /// <summary>Injects a locally built reply as an inbound packet on the interface of <paramref name="template"/>; checksums are computed here.</summary>
    private bool SendInbound(Span<byte> pkt, in WinDivertAddress template)
    {
        var addr = template;
        addr.Outbound = false;
        addr.Sniffed = false;
        addr.Impostor = false;
        addr.Loopback = false;
        addr.IPv6 = (pkt[0] >> 4) == 6;
        return GuardedSend(pkt, ref addr, recalc: true);
    }

    /// <summary>Send from a non-recv thread; refuses once the handle is being closed.</summary>
    private bool GuardedSend(Span<byte> pkt, ref WinDivertAddress addr, bool recalc)
    {
        Interlocked.Increment(ref _activeSenders);
        try
        {
            if (_closing) return false;
            IntPtr h = _main;
            if (h == WinDivertNative.InvalidHandle) return false;
            return recalc ? WinDivertNative.Send(h, pkt, ref addr, true) : WinDivertNative.SendRaw(h, pkt, ref addr);
        }
        finally
        {
            Interlocked.Decrement(ref _activeSenders);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <param name="dns">Capture outbound DNS: DoH (UDP and TCP/53) or "Hızlı DNS" (UDP/53).</param>
    /// <param name="inbound">Optional inbound clause (complete filter expression) OR-ed to the outbound one.</param>
    internal static string? BuildMainFilter(BypassProfile p, bool dns, string? inbound = null)
    {
        var parts = new List<string>(5);
        if (p.SplitTlsHello || p.FakePacket)
            parts.Add("(tcp and tcp.DstPort == 443 and tcp.PayloadLength > 5 and tcp.Payload[0] == 22 and tcp.Payload[1] == 3 and tcp.Payload[5] == 1)");
        if (p.HttpHostTricks)
            parts.Add("(tcp and tcp.DstPort == 80 and tcp.PayloadLength > 16)");
        if (p.BlockQuic)
            parts.Add("(udp and udp.DstPort == 443)");
        if (dns)
        {
            parts.Add("(udp and udp.DstPort == 53)");
            // TCP/53 is answered via DoH; "Hızlı DNS" leaves it to the system resolver (truncated answers only)
            if (p.Dns == DnsMode.DoH) parts.Add("(tcp and tcp.DstPort == 53)");
        }
        if (parts.Count == 0) return null;
        string outbound = "outbound and !loopback and !impostor and (" + string.Join(" or ", parts) + ")";
        return inbound is null ? outbound : "(" + outbound + ") or (" + inbound + ")";
    }

    internal static string SniffFilterText => SniffFilter;

    private static string Describe(BypassProfile p)
    {
        var t = new List<string>();
        if (p.SplitTlsHello) t.Add(p.SplitAtSni ? Strings.DescSplitSni : Strings.DescSplitAt(p.SplitPosition));
        if (p.DisorderSegments) t.Add(Strings.DescDisorder);
        if (p.FakePacket) t.Add(Strings.DescFakePacket(p.FakeTtl > 0 ? $"TTL {p.FakeTtl}" : Strings.DescAutoTtl));
        if (p.WrongChecksumFake) t.Add(Strings.DescBadChecksum);
        if (p.FakeWrongSeq) t.Add(Strings.DescBadSeq);
        if (p.FakePacket && p.FakeRepeats > 1) t.Add(Strings.DescFakeRepeat(Math.Clamp(p.FakeRepeats, 1, 5)));
        if (p.HttpHostTricks) t.Add(Strings.DescHostTrick);
        if (p.BlockQuic) t.Add(Strings.DescBlockQuic);
        if (p.Dns == DnsMode.DoH) t.Add($"DoH: {DohResolver.FindProvider(p.DohProviderName).Name}");
        else if (p.Dns == DnsMode.UdpRedirect)
            t.Add($"{Strings.DescFastDns}: {FastDns.Resolve(p.FastDnsServer, System.Net.Sockets.AddressFamily.InterNetwork)}");
        return t.Count == 0 ? Strings.DescNoTechnique : string.Join(", ", t);
    }

    internal static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void WarnAboutConflicts()
    {
        foreach (var name in ConflictingTools)
        {
            Process[] procs = [];
            try { procs = Process.GetProcessesByName(name); }
            catch { /* ignore */ }
            if (procs.Length > 0)
                Write("Warn", Strings.LogConflictTool(name));
            foreach (var pr in procs) pr.Dispose();
        }
        foreach (var svc in DriverServices.Running())
            if (!svc.Equals("WinDivert", StringComparison.OrdinalIgnoreCase))
                Write("Warn", Strings.LogForeignDriver(svc));
    }

    private bool DebugAllowed() => Allow(ref _debugWindow, ref _debugCount, 20);

    private void LimitedWrite(string level, string message)
    {
        if (Allow(ref _warnWindow, ref _warnCount, 5)) Write(level, message);
    }

    private static bool Allow(ref long window, ref int count, int max)
    {
        long now = Environment.TickCount64;
        long w = Volatile.Read(ref window);
        if (now - w > 5000 && Interlocked.CompareExchange(ref window, now, w) == w)
            Volatile.Write(ref count, 0);
        return Interlocked.Increment(ref count) <= max;
    }

    private void SetState(EngineState s)
    {
        State = s;
        try { StateChanged?.Invoke(this, s); } catch { /* subscriber errors must not break the engine */ }
    }

    private void Write(string level, string message)
    {
        try { Log?.Invoke(this, new LogEntry(DateTime.Now, level, message)); } catch { /* ignore */ }
    }

    private sealed class DivertStartException(string message) : Exception(message);
}
