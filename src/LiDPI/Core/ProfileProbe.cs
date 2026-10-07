using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using LiDPI.Engines;
using LiDPI.Engines.Divert;

namespace LiDPI.Core;

/// <summary>One candidate to test. <see cref="Profile"/> null = baseline (no engine running).</summary>
public sealed record ProbeVariant(string Name, BypassProfile? Profile);

/// <summary>Outcome of one HTTPS request.</summary>
public sealed record ProbeTargetResult(
    string Url, bool Ok, int? StatusCode, string? HttpVersion, double LatencyMs, double DnsMs,
    string? Addresses, string? ConnectedTo, string? Error, IReadOnlyList<string> Trace);

/// <summary>Outcome of one variant.</summary>
public sealed record ProbeVariantResult(
    ProbeVariant Variant, IReadOnlyList<ProbeTargetResult> Targets, string? EngineError,
    long PacketsProcessed, long ClientHellosSeen, long ClientHellosModified, long FakePacketsSent,
    long DnsQueriesResolved, IReadOnlyList<string> EngineLog)
{
    public int OkCount => Targets.Count(t => t.Ok);
    public double AvgLatencyMs
    {
        get
        {
            var ok = Targets.Where(t => t.Ok).ToList();
            return ok.Count == 0 ? double.PositiveInfinity : ok.Average(t => t.LatencyMs);
        }
    }
}

/// <summary>Progress notification: <see cref="Completed"/> is null when a variant starts.</summary>
public sealed record ProbeProgress(int Index, int Total, string VariantName, ProbeVariantResult? Completed);

/// <summary>
/// Tries a list of bypass profiles one after another with the packet engine and measures which of them
/// let blocked HTTPS sites complete a TLS handshake. Requires administrator rights.
/// </summary>
public static class ProfileProbe
{
    /// <summary>Configured in one place: <see cref="ProbeTargets"/>.</summary>
    public static IReadOnlyList<string> DefaultTargets => ProbeTargets.Urls;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Default candidate matrix (~25 variants incl. baseline and all built-in profiles).</summary>
    public static IReadOnlyList<ProbeVariant> DefaultCandidates()
    {
        var b = new BypassProfile { Id = "probe", SplitTlsHello = true, SplitAtSni = false, DisorderSegments = false,
                                    FakePacket = false, Dns = DnsMode.DoH, BlockQuic = true, HttpHostTricks = true };
        var s2d = b with { SplitPosition = 2, DisorderSegments = true };
        var list = new List<ProbeVariant>
        {
            new("baseline (engine off)", null),
            new("split 1", b with { SplitPosition = 1 }),
            new("split 1 + disorder", b with { SplitPosition = 1, DisorderSegments = true }),
            new("split 2", b with { SplitPosition = 2 }),
            new("split 2 + disorder", s2d),
            new("split SNI", b with { SplitAtSni = true }),
            new("split SNI + disorder", b with { SplitAtSni = true, DisorderSegments = true }),
        };
        foreach (int ttl in new[] { 1, 2, 3, 4, 5, 6, 8 })
            list.Add(new($"fake TTL {ttl} + split 2 + disorder", s2d with { FakePacket = true, FakeTtl = ttl }));
        list.Add(new("fake auto TTL + split 2 + disorder", s2d with { FakePacket = true, FakeTtl = 0 }));
        list.Add(new("fake auto TTL + split 1 + disorder", s2d with { SplitPosition = 1, FakePacket = true, FakeTtl = 0 }));
        list.Add(new("fake auto TTL x3 + split 1 + disorder", s2d with { SplitPosition = 1, FakePacket = true, FakeTtl = 0, FakeRepeats = 3 }));
        list.Add(new("fake TTL 3 + split SNI + disorder", s2d with { SplitAtSni = true, FakePacket = true, FakeTtl = 3 }));
        list.Add(new("fake bad checksum (TTL 64) + split 2 + disorder", s2d with { FakePacket = true, FakeTtl = 64, WrongChecksumFake = true }));
        list.Add(new("fake wrong seq (TTL 64) + split 2 + disorder", s2d with { FakePacket = true, FakeTtl = 64, FakeWrongSeq = true }));
        list.Add(new("fake wrong seq + bad checksum x2 + split 2 + disorder", s2d with { FakePacket = true, FakeTtl = 64, FakeWrongSeq = true, WrongChecksumFake = true, FakeRepeats = 2 }));
        foreach (var p in BuiltInProfiles.All)
            list.Add(new($"built-in: {p.Id}", p));
        return list;
    }

    /// <summary>
    /// Runs every variant (engine started, targets tested in parallel, engine stopped) and returns the
    /// results ranked by number of reachable targets, then by average latency.
    /// </summary>
    public static async Task<IReadOnlyList<ProbeVariantResult>> RunAsync(
        IReadOnlyList<ProbeVariant> variants, IReadOnlyList<string>? targets = null,
        IProgress<ProbeProgress>? progress = null, Action<string>? diagnostics = null, int rounds = 1, CancellationToken ct = default,
        bool packetTrace = true)
    {
        targets ??= DefaultTargets;
        var results = new List<ProbeVariantResult>(variants.Count);
        using var trace = packetTrace ? PacketTrace.TryOpen(diagnostics) : null;

        for (int i = 0; i < variants.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var v = variants[i];
            progress?.Report(new ProbeProgress(i, variants.Count, v.Name, null));
            var r = await RunVariantAsync(v, targets, trace, Math.Clamp(rounds, 1, 5), ct).ConfigureAwait(false);
            results.Add(r);
            progress?.Report(new ProbeProgress(i, variants.Count, v.Name, r));
        }

        return results
            .OrderByDescending(r => r.OkCount)
            .ThenBy(r => r.AvgLatencyMs)
            .ToList();
    }

    private static async Task<ProbeVariantResult> RunVariantAsync(
        ProbeVariant v, IReadOnlyList<string> targets, PacketTrace? trace, int rounds, CancellationToken ct)
    {
        var log = new ConcurrentQueue<string>();
        DivertEngine? engine = null;
        string? engineError = null;
        FlushDnsCache();

        try
        {
            if (v.Profile is not null)
            {
                engine = new DivertEngine();
                engine.Log += (_, e) => log.Enqueue($"{e.Time:HH:mm:ss.fff} [{e.Level}] {e.Message}");
                await engine.StartAsync(v.Profile with { Id = "probe" }, ct).ConfigureAwait(false);
                if (engine.State != EngineState.Running)
                    engineError = $"motor durumu: {engine.State}";
                else
                    await Task.Delay(400, ct).ConfigureAwait(false);
            }

            trace?.Reset();
            var outcomes = new List<TargetOutcome>();
            for (int round = 0; round < rounds; round++)
            {
                if (round > 0) FlushDnsCache();
                var tasks = targets.Select(t => TestTargetAsync(t, ct)).ToArray();
                outcomes.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            }
            await Task.Delay(300, ct).ConfigureAwait(false);   // let trailing RSTs arrive

            var final = outcomes
                .Select(o => o.Result with { Trace = trace?.Get(o.LocalPort) ?? (IReadOnlyList<string>)[] })
                .ToList();

            var st = engine?.Stats;
            return new ProbeVariantResult(v, final, engineError,
                st?.PacketsProcessed ?? 0, st?.ClientHellosSeen ?? 0, st?.ClientHellosModified ?? 0,
                st?.FakePacketsSent ?? 0, st?.DnsQueriesResolved ?? 0, log.ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeVariantResult(v, [], $"{engineError} {ex.GetType().Name}: {ex.Message}".Trim(),
                0, 0, 0, 0, 0, log.ToList());
        }
        finally
        {
            if (engine is not null)
            {
                try { await engine.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private readonly record struct TargetOutcome(ProbeTargetResult Result, int LocalPort);

    private static async Task<TargetOutcome> TestTargetAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        int localPort = -1;
        string? connectedTo = null;
        string? addresses = null;
        double dnsMs = 0;
        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            var dnsSw = Stopwatch.StartNew();
            IPAddress[] ips = await System.Net.Dns.GetHostAddressesAsync(uri.Host, cts.Token).ConfigureAwait(false);
            dnsMs = dnsSw.Elapsed.TotalMilliseconds;
            addresses = string.Join(",", ips.Take(4).Select(a => a.ToString()));
            if (ips.Length == 0) throw new IOException("DNS returned no address");
            // prefer IPv4 (deterministic, comparable across variants)
            var ordered = ips.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();

            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                PooledConnectionIdleTimeout = TimeSpan.Zero,
                MaxConnectionsPerServer = 1,
                ConnectTimeout = RequestTimeout,
                UseCookies = false,
                ConnectCallback = async (ctx, token) =>
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await s.ConnectAsync(ordered, ctx.DnsEndPoint.Port, token).ConfigureAwait(false);
                        if (s.LocalEndPoint is IPEndPoint le) localPort = le.Port;
                        if (s.RemoteEndPoint is IPEndPoint re)
                            connectedTo = (re.Address.IsIPv4MappedToIPv6 ? re.Address.MapToIPv4() : re.Address).ToString();
                        return new NetworkStream(s, ownsSocket: true);
                    }
                    catch
                    {
                        s.Dispose();
                        throw;
                    }
                },
            };
            using var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            using var req = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,*/*");
            sw.Restart();
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            double ms = sw.Elapsed.TotalMilliseconds;
            return new(new ProbeTargetResult(url, true, (int)resp.StatusCode, resp.Version.ToString(), ms, dnsMs,
                addresses, connectedTo, null, []), localPort);
        }
        catch (Exception ex)
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            string err = cts.IsCancellationRequested && !ct.IsCancellationRequested
                ? $"timeout ({RequestTimeout.TotalSeconds:0} s)"
                : Describe(ex);
            if (ct.IsCancellationRequested) throw;
            return new(new ProbeTargetResult(url, false, null, null, ms, dnsMs, addresses, connectedTo, err, []), localPort);
        }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            string s = e switch
            {
                SocketException se => $"Socket {se.SocketErrorCode}",
                AuthenticationException ae => $"TLS: {ae.Message}",
                _ => $"{e.GetType().Name}: {e.Message}",
            };
            if (!parts.Contains(s)) parts.Add(s);
        }
        string all = string.Join(" <- ", parts);
        return all.Length > 300 ? all[..300] : all;
    }

    private static void FlushDnsCache() => LiDPI.Dns.DnsCache.Flush();

    // ------------------------------------------------------------------ report

    /// <summary>Plain-text report: ranking table followed by per-variant details.</summary>
    public static string FormatReport(IReadOnlyList<ProbeVariantResult> ranked, IEnumerable<string>? header = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LiDPI profile probe");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  OS: {Environment.OSVersion}");
        if (header is not null) foreach (var h in header) sb.AppendLine(h);
        sb.AppendLine();
        sb.AppendLine("RANKING (OK = got an HTTP status code, i.e. TLS worked)");
        sb.AppendLine(new string('-', 120));
        int rank = 0;
        foreach (var r in ranked)
        {
            rank++;
            string avg = double.IsInfinity(r.AvgLatencyMs) ? "-" : $"{r.AvgLatencyMs:0}ms";
            var cells = r.Targets.Select(t => $"{Short(t.Url)}={(t.Ok ? $"{t.StatusCode}/{t.LatencyMs:0}ms" : ShortErr(t.Error))}");
            sb.AppendLine($"{rank,2}. [{r.OkCount}/{r.Targets.Count}] avg {avg,-7} {r.Variant.Name}");
            sb.AppendLine($"      {string.Join("  ", cells)}");
            sb.AppendLine($"      pkts={r.PacketsProcessed} CH seen={r.ClientHellosSeen} mod={r.ClientHellosModified} fakes={r.FakePacketsSent} dns={r.DnsQueriesResolved}" +
                          (r.EngineError is null ? "" : $"  ENGINE ERROR: {r.EngineError}"));
        }

        sb.AppendLine();
        sb.AppendLine("DETAILS");
        foreach (var r in ranked)
        {
            sb.AppendLine(new string('=', 120));
            sb.AppendLine($"{r.Variant.Name}  [{r.OkCount}/{r.Targets.Count}]");
            if (r.Variant.Profile is { } p)
                sb.AppendLine($"  profile: splitHello={p.SplitTlsHello} atSni={p.SplitAtSni} pos={p.SplitPosition} disorder={p.DisorderSegments} " +
                              $"fake={p.FakePacket} ttl={p.FakeTtl} badSum={p.WrongChecksumFake} wrongSeq={p.FakeWrongSeq} rep={p.FakeRepeats} " +
                              $"quic={p.BlockQuic} dns={p.Dns}");
            foreach (var t in r.Targets)
            {
                sb.AppendLine($"  {t.Url}: {(t.Ok ? $"OK {t.StatusCode} HTTP/{t.HttpVersion}" : "FAIL " + t.Error)}  t={t.LatencyMs:0}ms dns={t.DnsMs:0}ms ip={t.ConnectedTo ?? "-"} dnsIPs={t.Addresses ?? "-"}");
                foreach (var line in t.Trace) sb.AppendLine($"      {line}");
            }
            foreach (var l in r.EngineLog) sb.AppendLine($"  log {l}");
        }
        return sb.ToString();
    }

    private static string Short(string url)
    {
        string h = new Uri(url).Host;
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    private static string ShortErr(string? e)
    {
        if (e is null) return "FAIL";
        if (e.StartsWith("zaman", StringComparison.Ordinal)) return "TIMEOUT";
        if (e.Contains("ConnectionReset", StringComparison.Ordinal)) return "RESET";
        if (e.Contains("TimedOut", StringComparison.Ordinal)) return "TCP-TIMEOUT";
        if (e.Contains("TLS", StringComparison.Ordinal)) return "TLS-ERR";
        if (e.Contains("DNS", StringComparison.Ordinal)) return "DNS-ERR";
        return "FAIL";
    }

    // ------------------------------------------------------------------ packet trace

    /// <summary>
    /// Passive WinDivert sniffer below the engine's priority: shows the TCP/443 packets that actually leave
    /// the machine (after the engine's rewriting) and what comes back, keyed by local port.
    /// </summary>
    private sealed class PacketTrace : IDisposable
    {
        private const string Filter = "!loopback and tcp and (tcp.DstPort == 443 or tcp.SrcPort == 443)";
        private const short Priority = -1000;   // lower than the engine (0): sees the re-injected packets
        private const int MaxEvents = 24;

        private sealed class Conn
        {
            public uint ClientIsn;
            public bool HaveClientIsn;
            public uint ServerIsn;
            public bool HaveServerIsn;
            public int InboundData;
            public readonly List<string> Events = [];
        }

        private readonly ConcurrentDictionary<int, Conn> _conns = new();
        private readonly IntPtr _h;
        private readonly bool _ownsDriver;
        private readonly Thread _thread;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private volatile bool _stop;

        private PacketTrace(IntPtr h, bool ownsDriver)
        {
            _h = h;
            _ownsDriver = ownsDriver;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LiDPI probe trace" };
            _thread.Start();
        }

        public static PacketTrace? TryOpen(Action<string>? diag)
        {
            try
            {
                WinDivertLoader.EnsureLoaded();
                if (WinDivertLoader.SysPath is { } sys) DriverServices.DeleteIfStale("WinDivert", sys, m => diag?.Invoke(m));
                bool wasRunning = DriverServices.IsRunning("WinDivert");
                IntPtr h = WinDivertNative.WinDivertOpen(Filter, WinDivertNative.LayerNetwork, Priority,
                    WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
                if (h == WinDivertNative.InvalidHandle || h == IntPtr.Zero)
                {
                    diag?.Invoke($"Packet trace failed to open: {WinDivertNative.DescribeOpenError(Marshal.GetLastPInvokeError())}");
                    return null;
                }
                diag?.Invoke("Packet trace open (priority -1000, sniff).");
                return new PacketTrace(h, !wasRunning);
            }
            catch (Exception ex)
            {
                diag?.Invoke($"Packet trace failed to open: {ex.Message}");
                return null;
            }
        }

        public void Reset() => _conns.Clear();

        public IReadOnlyList<string> Get(int localPort)
        {
            if (localPort <= 0 || !_conns.TryGetValue(localPort, out var c)) return ["(no trace)"];
            lock (c) return c.Events.ToList();
        }

        private unsafe void Loop()
        {
            var buf = GC.AllocateArray<byte>(WinDivertNative.MtuMax, pinned: true);
            fixed (byte* p = buf)
            {
                WinDivertAddress addr;
                uint len;
                while (!_stop)
                {
                    if (!WinDivertNative.WinDivertRecv(_h, p, (uint)buf.Length, &len, &addr))
                    {
                        int err = Marshal.GetLastPInvokeError();
                        if (_stop || err is 6 or 232 or 995) break;
                        continue;
                    }
                    try { Record(buf.AsSpan(0, (int)len), addr.Outbound); }
                    catch { /* never let a malformed packet stop the trace */ }
                }
            }
        }

        private void Record(ReadOnlySpan<byte> pkt, bool outbound)
        {
            if (!PacketInfo.TryParse(pkt, out var pi) || pi.Protocol != PacketInfo.ProtoTcp) return;
            int localPort = outbound ? pi.SrcPort : pi.DstPort;
            if (_conns.Count > 8192) _conns.Clear();
            var c = _conns.GetOrAdd(localPort, _ => new Conn());

            byte flags = pkt[pi.L4Offset + 13];
            bool syn = (flags & 0x02) != 0, ack = (flags & 0x10) != 0, rst = (flags & 0x04) != 0, fin = (flags & 0x01) != 0;
            uint seq = pi.GetSeq(pkt);
            byte ttl = PacketInfo.GetTtl(pkt);
            var payload = pkt.Slice(pi.PayloadOffset, pi.PayloadLength);
            string t = $"{_clock.Elapsed.TotalMilliseconds,8:0.0}ms";

            lock (c)
            {
                if (c.Events.Count >= MaxEvents) return;
                if (outbound)
                {
                    if (syn)
                    {
                        c.Events.Clear();
                        c.ClientIsn = seq; c.HaveClientIsn = true; c.HaveServerIsn = false; c.InboundData = 0;
                        c.Events.Add($"{t} -> SYN ttl={ttl}");
                        return;
                    }
                    long rel = c.HaveClientIsn ? (int)(seq - c.ClientIsn - 1) : seq;
                    if (pi.PayloadLength > 0)
                    {
                        string extra = "";
                        if (TlsClientHello.IsClientHello(payload))
                            extra = $" ClientHello sni={TlsClientHello.GetSni(payload) ?? "?"}";
                        c.Events.Add($"{t} -> DATA seq+{rel} len={pi.PayloadLength} ttl={ttl}{extra}");
                    }
                    else if (rst) c.Events.Add($"{t} -> RST seq+{rel}");
                    else if (fin) c.Events.Add($"{t} -> FIN");
                }
                else
                {
                    if (syn && ack)
                    {
                        c.ServerIsn = seq; c.HaveServerIsn = true;
                        c.Events.Add($"{t} <- SYN-ACK ttl={ttl}");
                        return;
                    }
                    long rel = c.HaveServerIsn ? (int)(seq - c.ServerIsn - 1) : seq;
                    if (rst) c.Events.Add($"{t} <- RST{(ack ? "-ACK" : "")} ttl={ttl} seq+{rel} len={pi.PayloadLength}");
                    else if (pi.PayloadLength > 0)
                    {
                        if (++c.InboundData > 3) return;
                        string kind = payload.Length > 5 && payload[0] == 0x16 && payload[5] == 0x02 ? " ServerHello"
                                    : payload.Length > 0 && payload[0] == 0x15 ? " TLS-ALERT"
                                    : payload.Length > 4 && payload[0] == (byte)'H' ? " HTTP" : "";
                        c.Events.Add($"{t} <- DATA seq+{rel} len={pi.PayloadLength} ttl={ttl}{kind}");
                    }
                    else if (fin) c.Events.Add($"{t} <- FIN ttl={ttl}");
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            WinDivertNative.WinDivertShutdown(_h, WinDivertNative.ShutdownRecv);
            try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            WinDivertNative.WinDivertClose(_h);
            if (_ownsDriver) DriverServices.Stop("WinDivert", null);
        }
    }
}
