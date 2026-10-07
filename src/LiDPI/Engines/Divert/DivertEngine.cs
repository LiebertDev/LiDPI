using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LiDPI.Core;
using LiDPI.Dns;

namespace LiDPI.Engines.Divert;

/// <summary>
/// System-wide packet engine built on WinDivert 2.2: TLS ClientHello splitting / disorder / fake
/// injection, HTTP Host obfuscation, QUIC blocking and transparent DNS-over-HTTPS.
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
    private const int MaxHopCacheEntries = 16384;
    private const string DriverServiceName = "WinDivert";

    /// <summary>Process names (lowercase, no .exe) of other DPI tools that conflict with this engine.</summary>
    private static readonly string[] ConflictingTools = ["goodbyedpi", "winws", "zapret", "splitwire"];

    private const string SniffFilter =
        "inbound and !loopback and !impostor and tcp and tcp.Syn and tcp.Ack and tcp.SrcPort == 443";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<UInt128, byte> _hopTtl = new();

    private BypassProfile _profile = BuiltInProfiles.Standard;
    private IntPtr _main = WinDivertNative.InvalidHandle;
    private IntPtr _sniff = WinDivertNative.InvalidHandle;
    private Thread? _mainThread;
    private Thread? _sniffThread;
    private DohResolver? _resolver;
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

    public EngineMode Mode => EngineMode.Divert;
    public string DisplayName => "Tüm Uygulamalar";
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
                Write("Error", ex is DivertStartException ? ex.Message : $"Paket motoru başlatılamadı: {ex.Message}");
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
            if (wasRunning) Write("Info", "Paket motoru durduruldu.");
            if (DnsCache.Flush()) Write("Info", "DNS önbelleği temizlendi.");
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
        if (DnsCache.Flush()) Write("Info", "DNS önbelleği temizlendi.");
        Write("Info", "İpucu: Discord gibi açık uygulamaları bir kez yeniden başlatın.");
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
            throw new DivertStartException($"WinDivert bileşenleri hazırlanamadı: {ex.Message}");
        }
        ct.ThrowIfCancellationRequested();

        var p = _profile;
        bool doh = p.Dns == DnsMode.DoH;
        string? filter = BuildMainFilter(p, doh);

        _stopping = false;
        _closing = false;
        _loopFailed = false;
        _hopTtl.Clear();
        _cts = new CancellationTokenSource();
        _segBuf = GC.AllocateArray<byte>(WinDivertNative.MtuMax, pinned: true);
        _fakeBuf = GC.AllocateArray<byte>(WinDivertNative.MtuMax, pinned: true);

        if (doh)
        {
            _resolver = new DohResolver(DohResolver.FindProvider(p.DohProviderName));
            _resolver.Warning += m => LimitedWrite("Warn", m);
        }

        if (filter is null)
        {
            Write("Warn", "Profil hiçbir paket tekniği içermiyor; trafik değiştirilmeyecek.");
        }
        else
        {
            if (WinDivertNative.CheckFilter(filter) is { } err)
                throw new DivertStartException($"WinDivert filtresi derlenemedi: {err}");
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
                    Write("Warn", $"Otomatik TTL kapatıldı (sabit TTL {FallbackFakeTtl} kullanılacak): {ex.Message}");
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

        Write("Info", $"Paket motoru çalışıyor ({Describe(p)}).");
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

        sw.Restart();
        while (Volatile.Read(ref _dnsInFlight) != 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(5);
        _resolver?.Dispose();
        _resolver = null;
        _cts?.Dispose();
        _cts = null;
        _hopTtl.Clear();

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
                            if (!_stopping) failure = $"WinDivert hata {err}";
                            break;
                        }
                        if (err == ErrorInsufficientBuffer) continue;
                        LimitedWrite("Warn", $"Paket alınamadı (hata {err}).");
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
                        LimitedWrite("Warn", $"Paket işlenemedi: {ex.Message}");
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
        if (!_stopping) FailClosed(failure ?? "bilinmeyen neden");
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

        Write("Error", $"Paket döngüsü beklenmedik şekilde sonlandı ({reason}). Trafiğin kesilmemesi için paket motoru kapatıldı; lütfen yeniden bağlanın.");
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
            Write("Warn", $"TTL dinleyicisi durdu: {ex.Message}");
        }
    }

    private void LearnHops(UInt128 key, byte ttl)
    {
        int initial = ttl <= 64 ? 64 : ttl <= 128 ? 128 : 255;
        int hops = initial - ttl;
        // The decoy must pass the DPI box (measured on Türk Telekom: 2 hops away, so TTL >= 3) but die
        // before the server: hops-2, at least MinAutoFakeTtl, never more than hops-1.
        int fakeTtl = hops - 1 >= MinAutoFakeTtl ? Math.Max(hops - 2, MinAutoFakeTtl) : hops - 1;
        byte fake = (byte)Math.Clamp(fakeTtl, 1, 255);
        if (_hopTtl.Count >= MaxHopCacheEntries) _hopTtl.Clear();
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

        if (pi.Protocol == PacketInfo.ProtoUdp)
        {
            if (pi.DstPort == 443 && _profile.BlockQuic) return;   // drop QUIC -> browsers fall back to TCP
            if (pi.DstPort == 53 && _resolver is not null && TryQueueDns(pkt, pi, addr)) return;
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
        if (DebugAllowed()) Write("Debug", $"TLS: {TlsClientHello.GetSni(payload) ?? "(SNI yok)"} ({pi.PayloadLength} B, bölme {split})");

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
        int hdr = pi.PayloadOffset;
        var seg = _segBuf.AsSpan(0, hdr + len);
        pkt[..hdr].CopyTo(seg);
        pkt.Slice(hdr + start, len).CopyTo(seg[hdr..]);
        pi.SetTotalLength(seg, seg.Length);
        pi.SetSeq(seg, pi.GetSeq(pkt) + (uint)start);
        pi.BumpIpId(seg, ipIdDelta);
        WinDivertNative.Send(_main, seg, ref addr, recalc: true);
        _handled = true;
    }

    /// <summary>Sends a decoy ClientHello with the real sequence number but a TTL that dies before the server.</summary>
    private void SendFake(ReadOnlySpan<byte> pkt, in PacketInfo pi, WinDivertAddress addr)
    {
        int hdr = pi.PayloadOffset;
        var buf = _fakeBuf.AsSpan();
        pkt[..hdr].CopyTo(buf);
        int fakeLen = TlsClientHello.BuildFake(buf[hdr..], pi.PayloadLength);
        if (fakeLen <= 0) return;

        var fake = buf[..(hdr + fakeLen)];
        pi.SetTotalLength(fake, fake.Length);
        pi.SetTtl(fake, FakeTtlFor(pkt));
        if (_profile.FakeWrongSeq) pi.SetSeq(fake, pi.GetSeq(pkt) - 10000u);
        WinDivertNative.CalcChecksums(fake, ref addr);
        if (_profile.WrongChecksumFake)
        {
            var cs = fake.Slice(pi.L4Offset + 16, 2);
            BinaryPrimitives.WriteUInt16BigEndian(cs, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(cs) ^ 0xA5A5));
        }
        if (WinDivertNative.SendRaw(_main, fake, ref addr)) Stats.IncrementFakePackets();
    }

    private byte FakeTtlFor(ReadOnlySpan<byte> pkt)
    {
        int fixedTtl = _profile.FakeTtl;
        if (fixedTtl > 0) return (byte)Math.Min(fixedTtl, 255);
        return _hopTtl.TryGetValue(PacketInfo.DstKey(pkt), out byte t) ? t : FallbackFakeTtl;
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
                LimitedWrite("Warn", $"DoH başarısız, DNS sorgusu olduğu gibi gönderildi: {ex.Message}");
                var orig = job.Addr;
                GuardedSend(job.Packet.AsSpan(0, job.Length), ref orig, recalc: false);
                return;
            }

            resp[0] = query[0];   // keep the client's transaction id
            resp[1] = query[1];
            if (resp.Length > MaxDnsUdpResponse) resp = DnsWire.BuildTruncatedReply(query) ?? resp;

            int ipLen = pi.IsV6 ? 40 : 20;
            int total = ipLen + 8 + resp.Length;
            if (total > 0xFFFF) return;

            outBuf = ArrayPool<byte>.Shared.Rent(total);
            var o = outBuf.AsSpan(0, total);
            var src = job.Packet.AsSpan(0, job.Length);
            o[..ipLen].Clear();
            if (pi.IsV6)
            {
                o[0] = 0x60;
                BinaryPrimitives.WriteUInt16BigEndian(o[4..], (ushort)(8 + resp.Length));
                o[6] = PacketInfo.ProtoUdp;
                o[7] = 64;
                src.Slice(24, 16).CopyTo(o[8..]);    // src = original destination
                src.Slice(8, 16).CopyTo(o[24..]);    // dst = original source
            }
            else
            {
                o[0] = 0x45;
                BinaryPrimitives.WriteUInt16BigEndian(o[2..], (ushort)total);
                BinaryPrimitives.WriteUInt16BigEndian(o[4..], (ushort)Random.Shared.Next(0x10000));
                o[8] = 64;
                o[9] = PacketInfo.ProtoUdp;
                src.Slice(16, 4).CopyTo(o[12..]);
                src.Slice(12, 4).CopyTo(o[16..]);
            }
            var udp = o[ipLen..];
            BinaryPrimitives.WriteUInt16BigEndian(udp, pi.DstPort);
            BinaryPrimitives.WriteUInt16BigEndian(udp[2..], pi.SrcPort);
            BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + resp.Length));
            udp[6] = udp[7] = 0;
            resp.CopyTo(udp[8..]);

            var addr = job.Addr;
            addr.Outbound = false;
            addr.Sniffed = false;
            addr.Impostor = false;
            addr.Loopback = false;
            addr.IPv6 = pi.IsV6;
            if (GuardedSend(o, ref addr, recalc: true)) Stats.IncrementDnsQueries();
        }
        catch (Exception ex)
        {
            if (!_stopping) LimitedWrite("Warn", $"DNS yanıtı enjekte edilemedi: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(job.Packet);
            if (outBuf is not null) ArrayPool<byte>.Shared.Return(outBuf);
            Interlocked.Decrement(ref _dnsInFlight);
        }
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

    internal static string? BuildMainFilter(BypassProfile p, bool doh)
    {
        var parts = new List<string>(4);
        if (p.SplitTlsHello || p.FakePacket)
            parts.Add("(tcp and tcp.DstPort == 443 and tcp.PayloadLength > 5 and tcp.Payload[0] == 22 and tcp.Payload[1] == 3 and tcp.Payload[5] == 1)");
        if (p.HttpHostTricks)
            parts.Add("(tcp and tcp.DstPort == 80 and tcp.PayloadLength > 16)");
        if (p.BlockQuic)
            parts.Add("(udp and udp.DstPort == 443)");
        if (doh)
            parts.Add("(udp and udp.DstPort == 53)");
        if (parts.Count == 0) return null;
        return "outbound and !loopback and !impostor and (" + string.Join(" or ", parts) + ")";
    }

    internal static string SniffFilterText => SniffFilter;

    private static string Describe(BypassProfile p)
    {
        var t = new List<string>();
        if (p.SplitTlsHello) t.Add(p.SplitAtSni ? "SNI'de bölme" : $"{p.SplitPosition}. baytta bölme");
        if (p.DisorderSegments) t.Add("ters sıra");
        if (p.FakePacket) t.Add(p.FakeTtl > 0 ? $"sahte paket (TTL {p.FakeTtl})" : "sahte paket (otomatik TTL)");
        if (p.WrongChecksumFake) t.Add("hatalı checksum");
        if (p.FakeWrongSeq) t.Add("hatalı seq");
        if (p.FakePacket && p.FakeRepeats > 1) t.Add($"sahte x{Math.Clamp(p.FakeRepeats, 1, 5)}");
        if (p.HttpHostTricks) t.Add("HTTP Host hilesi");
        if (p.BlockQuic) t.Add("QUIC engelleme");
        if (p.Dns == DnsMode.DoH) t.Add($"DoH: {DohResolver.FindProvider(p.DohProviderName).Name}");
        return t.Count == 0 ? "teknik yok" : string.Join(", ", t);
    }

    private static bool IsAdministrator()
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
                Write("Warn", $"Çakışan bir DPI aracı çalışıyor ({name}.exe). Lütfen önce onu kapatın.");
            foreach (var pr in procs) pr.Dispose();
        }
        foreach (var svc in DriverServices.Running())
            if (!svc.Equals("WinDivert", StringComparison.OrdinalIgnoreCase))
                Write("Warn", $"Başka bir programın yüklediği bir WinDivert sürücüsü çalışıyor ({svc}). Sorun yaşarsanız diğer DPI araçlarını kapatıp tekrar deneyin.");
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
