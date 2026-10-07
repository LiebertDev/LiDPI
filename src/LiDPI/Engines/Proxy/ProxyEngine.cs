using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiDPI.Core;
using LiDPI.Dns;

namespace LiDPI.Engines.Proxy;

/// <summary>
/// User-space engine: a local HTTP/HTTPS proxy on 127.0.0.1 registered as the system proxy.
/// TLS ClientHellos are fragmented (TLS record + TCP segment split inside the SNI) before reaching the
/// server; hostnames are resolved over DoH. Packet-level tricks (disorder, fake, QUIC) are not possible here.
/// </summary>
public sealed class ProxyEngine : IBypassEngine
{
    public const int DefaultPort = 8881;
    internal const int PortSearchRange = 20;
    private const int HeadBufferSize = 64 * 1024;
    private const int PipeBufferSize = 32 * 1024;

    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    /// <summary>No idle limit once a tunnel is established (WebSockets / long-poll stay open); only the header phase is timed.</summary>
    private static readonly TimeSpan TunnelIdleTimeout = Timeout.InfiniteTimeSpan;
    private static readonly TimeSpan HalfCloseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HappyEyeballsDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SegmentDelay = TimeSpan.FromMilliseconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private BypassProfile _profile = BuiltInProfiles.Standard;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private DohResolver? _resolver;
    private int _activeConnections;
    private bool _proxySet;
    private EventHandler? _onProcessExit;
    private UnhandledExceptionEventHandler? _onUnhandled;
    private long _warnWindow, _debugWindow;
    private int _warnCount, _debugCount;

    public EngineMode Mode => EngineMode.Proxy;
    public string DisplayName => "Sadece Tarayıcı (Proxy)";
    public bool RequiresAdmin => false;
    public EngineState State { get; private set; } = EngineState.Stopped;
    public EngineStats Stats { get; } = new();

    /// <summary>Port actually bound (0 while stopped).</summary>
    public int Port { get; private set; }

    /// <summary>Register the proxy as the per-user system proxy while running (default true).</summary>
    public bool ConfigureSystemProxy { get; init; } = true;

    /// <summary>Also split the ClientHello into two TLS records (in addition to two TCP segments). Default true.</summary>
    public bool TlsRecordSplit { get; init; } = true;

    public event EventHandler<EngineState>? StateChanged;
    public event EventHandler<LogEntry>? Log;

    /// <summary>
    /// Crash recovery: restores the user's previous proxy settings if they still point to a LiDPI
    /// proxy left behind by an earlier run. Call once at application start-up. Never throws.
    /// </summary>
    public static void RestoreIfDirty() => SystemProxy.RestoreIfDirty();

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
                StartCore();
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                await TeardownAsync().ConfigureAwait(false);
                SetState(EngineState.Stopped);
                throw;
            }
            catch (Exception ex)
            {
                Write("Error", $"Proxy başlatılamadı: {ex.Message}");
                await TeardownAsync().ConfigureAwait(false);
                SetState(EngineState.Error);
                return;
            }
            Stats.StartedAt = DateTime.UtcNow;
            SetState(EngineState.Running);
            if (DnsCache.Flush()) Write("Info", "DNS önbelleği temizlendi.");
            Write("Info", "İpucu: Discord gibi açık uygulamaları bir kez yeniden başlatın.");
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
            await TeardownAsync().ConfigureAwait(false);
            SetState(EngineState.Stopped);
            if (wasRunning)
                Write("Info", ConfigureSystemProxy ? "Proxy durduruldu, sistem proxy ayarları geri yüklendi." : "Proxy durduruldu.");
            if (DnsCache.Flush()) Write("Info", "DNS önbelleği temizlendi.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------ lifecycle

    private void StartCore()
    {
        var p = _profile;
        Stats.Reset();
        _cts = new CancellationTokenSource();
        if (p.Dns == DnsMode.DoH)
        {
            _resolver = new DohResolver(DohResolver.FindProvider(p.DohProviderName));
            _resolver.Warning += m => LimitedWrite("Warn", m);
        }

        _listener = Bind(out int port);
        Port = port;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));

        if (ConfigureSystemProxy)
        {
            _onProcessExit = (_, _) => SystemProxy.RestoreIfDirty();
            _onUnhandled = (_, _) => SystemProxy.RestoreIfDirty();
            AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
            AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
            SystemProxy.Enable($"127.0.0.1:{port}");
            _proxySet = true;
        }

        Write("Info", $"Proxy 127.0.0.1:{port} adresinde çalışıyor ({Describe(p)}).");
        if (p.DisorderSegments || p.FakePacket || p.WrongChecksumFake)
            Write("Info", "Ters sıra / sahte paket teknikleri yalnızca 'Tüm Uygulamalar' modunda uygulanabilir; proxy modunda yok sayıldı.");
    }

    private static Socket Bind(out int port)
    {
        for (int candidate = DefaultPort; candidate < DefaultPort + PortSearchRange; candidate++)
        {
            var s = NewListener();
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Loopback, candidate));
                s.Listen(1024);
                port = candidate;
                return s;
            }
            catch (SocketException)
            {
                s.Dispose();
            }
        }
        var any = NewListener();
        try
        {
            any.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            any.Listen(1024);
            port = ((IPEndPoint)any.LocalEndPoint!).Port;
            return any;
        }
        catch
        {
            any.Dispose();
            throw;
        }
    }

    private static Socket NewListener() =>
        new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };

    private async Task TeardownAsync()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Dispose(); } catch { /* ignore */ }
        _listener = null;

        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* ignore */ }
            _acceptTask = null;
        }

        for (int i = 0; i < 100 && Volatile.Read(ref _activeConnections) > 0; i++)
            await Task.Delay(20).ConfigureAwait(false);

        if (_proxySet)
        {
            SystemProxy.RestoreIfDirty();
            _proxySet = false;
        }
        if (_onProcessExit is not null) AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        if (_onUnhandled is not null) AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
        _onProcessExit = null;
        _onUnhandled = null;

        _resolver?.Dispose();
        _resolver = null;
        _cts?.Dispose();
        _cts = null;
        Port = 0;
    }

    // ------------------------------------------------------------------ connections

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                LimitedWrite("Warn", $"Bağlantı kabul edilemedi: {ex.Message}");
                continue;
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    private sealed class Conn(CancellationTokenSource cts)
    {
        public readonly CancellationTokenSource Cts = cts;
        public volatile bool HalfClosed;

        public void Touch()
        {
            try { Cts.CancelAfter(HalfClosed ? HalfCloseTimeout : TunnelIdleTimeout); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken engineCt)
    {
        Interlocked.Increment(ref _activeConnections);
        Stats.IncrementConnections();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(engineCt);
        var conn = new Conn(cts);
        Socket? server = null;
        byte[] buf = ArrayPool<byte>.Shared.Rent(HeadBufferSize);
        try
        {
            client.NoDelay = true;
            cts.CancelAfter(HeaderTimeout);

            int filled = 0, headLen = -1;
            while (headLen < 0)
            {
                if (filled == buf.Length)
                {
                    await SendTextAsync(client, "HTTP/1.1 431 Request Header Fields Too Large\r\nConnection: close\r\n\r\n", cts.Token).ConfigureAwait(false);
                    return;
                }
                int n = await client.ReceiveAsync(buf.AsMemory(filled), SocketFlags.None, cts.Token).ConfigureAwait(false);
                if (n == 0) return;
                int searchFrom = Math.Max(0, filled - 3);
                filled += n;
                int idx = buf.AsSpan(searchFrom, filled - searchFrom).IndexOf("\r\n\r\n"u8);
                if (idx >= 0) headLen = searchFrom + idx + 4;
            }

            var head = buf.AsSpan(0, headLen);
            int lineEnd = head.IndexOf("\r\n"u8);
            var parts = Encoding.Latin1.GetString(head[..lineEnd]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                await SendTextAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", cts.Token).ConfigureAwait(false);
                return;
            }
            string method = parts[0], target = parts[1], version = parts[2];

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySplitHostPort(target, 443, out string host, out int port))
                {
                    await SendTextAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", cts.Token).ConfigureAwait(false);
                    return;
                }
                server = await ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                if (server is null)
                {
                    await SendTextAsync(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", cts.Token).ConfigureAwait(false);
                    return;
                }
                await SendTextAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n", cts.Token).ConfigureAwait(false);
                conn.Touch();

                var down = PumpAsync(server, client, conn);
                int firstLen = filled - headLen;
                if (firstLen > 0) Buffer.BlockCopy(buf, headLen, buf, 0, firstLen);
                else firstLen = await client.ReceiveAsync(buf.AsMemory(), SocketFlags.None, cts.Token).ConfigureAwait(false);

                Task up;
                if (firstLen > 0)
                {
                    conn.Touch();
                    await SendFirstAsync(server, buf.AsMemory(0, firstLen), host, cts.Token).ConfigureAwait(false);
                    up = PumpAsync(client, server, conn);
                }
                else
                {
                    conn.HalfClosed = true;
                    ShutdownSend(server);
                    up = Task.CompletedTask;
                }
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            else
            {
                if (!TryParseAbsoluteHttp(target, out string host, out int port, out string pathAndQuery))
                {
                    await SendTextAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", cts.Token).ConfigureAwait(false);
                    return;
                }
                byte[] newHead = RewriteHead(head, method, pathAndQuery, version, host, port);
                server = await ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                if (server is null)
                {
                    await SendTextAsync(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", cts.Token).ConfigureAwait(false);
                    return;
                }
                conn.Touch();

                int split = -1;
                if (_profile.HttpHostTricks) HttpHostTrick.TryApply(newHead, out split);
                if (split > 0 && split < newHead.Length)
                {
                    await SendAllAsync(server, newHead.AsMemory(0, split), cts.Token).ConfigureAwait(false);
                    await Task.Delay(SegmentDelay, cts.Token).ConfigureAwait(false);
                    await SendAllAsync(server, newHead.AsMemory(split), cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await SendAllAsync(server, newHead, cts.Token).ConfigureAwait(false);
                }
                if (filled > headLen)
                    await SendAllAsync(server, buf.AsMemory(headLen, filled - headLen), cts.Token).ConfigureAwait(false);

                await Task.WhenAll(PumpAsync(client, server, conn), PumpAsync(server, client, conn)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* timeout or stop */ }
        catch (SocketException) { /* peer reset */ }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LimitedWrite("Warn", $"Bağlantı hatası: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            server?.Dispose();
            client.Dispose();
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    /// <summary>Copies src → dst until EOF, then half-closes dst. Any error tears down the connection.</summary>
    private static async Task PumpAsync(Socket src, Socket dst, Conn conn)
    {
        var ct = conn.Cts.Token;
        byte[] buf = ArrayPool<byte>.Shared.Rent(PipeBufferSize);
        try
        {
            while (true)
            {
                int n = await src.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    ShutdownSend(dst);
                    conn.HalfClosed = true;
                    conn.Touch();
                    return;
                }
                await SendAllAsync(dst, buf.AsMemory(0, n), ct).ConfigureAwait(false);
                conn.Touch();
            }
        }
        catch
        {
            try { conn.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Sends the first client chunk, fragmenting a TLS ClientHello inside the SNI.</summary>
    private async Task SendFirstAsync(Socket server, ReadOnlyMemory<byte> data, string host, CancellationToken ct)
    {
        var p = _profile;
        if (p.SplitTlsHello && TlsClientHello.IsClientHello(data.Span))
        {
            Stats.IncrementPackets();
            // TLS record fragmentation needs a split inside the record body: tiny fixed offsets (packet-engine
            // profiles) fall back to the SNI position here.
            int split = TlsClientHello.ChooseSplit(data.Span,
                TlsRecordSplit && !p.SplitAtSni && p.SplitPosition <= 5 ? p with { SplitAtSni = true } : p);
            if (DebugAllowed()) Write("Debug", $"TLS: {host}");

            if (TlsRecordSplit && split > 5)
            {
                byte[] tmp = ArrayPool<byte>.Shared.Rent(data.Length + 5);
                try
                {
                    int w = TlsClientHello.FragmentRecord(data.Span, split, tmp);
                    if (w > 0)
                    {
                        // first TLS record ends exactly at 'split'
                        await SendAllAsync(server, tmp.AsMemory(0, split), ct).ConfigureAwait(false);
                        await Task.Delay(SegmentDelay, ct).ConfigureAwait(false);
                        await SendAllAsync(server, tmp.AsMemory(split, w - split), ct).ConfigureAwait(false);
                        return;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tmp);
                }
            }
            if (split > 0)
            {
                await SendAllAsync(server, data[..split], ct).ConfigureAwait(false);
                await Task.Delay(SegmentDelay, ct).ConfigureAwait(false);
                await SendAllAsync(server, data[split..], ct).ConfigureAwait(false);
                return;
            }
        }
        await SendAllAsync(server, data, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ outbound connect

    private async Task<Socket?> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addrs;
        if (IPAddress.TryParse(host, out var literal))
        {
            addrs = [literal];
        }
        else
        {
            var resolver = _resolver;
            try
            {
                if (resolver is not null)
                {
                    try
                    {
                        addrs = await resolver.ResolveAsync(host, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ObjectDisposedException)
                    {
                        LimitedWrite("Warn", $"DoH çözümlemesi başarısız ({host}), sistem DNS'i kullanılıyor.");
                        addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                }
            }
            catch (SocketException)
            {
                LimitedWrite("Warn", $"Alan adı çözülemedi: {host}");
                return null;
            }
            if (resolver is not null) Stats.IncrementDnsQueries();
        }

        var ordered = OrderAddresses(addrs, port);
        if (ordered.Count == 0)
        {
            LimitedWrite("Warn", $"Alan adı için adres bulunamadı: {host}");
            return null;
        }
        var s = await ConnectAnyAsync(ordered, port, ct).ConfigureAwait(false);
        if (s is null) LimitedWrite("Warn", $"Sunucuya bağlanılamadı: {host}:{port}");
        return s;
    }

    /// <summary>IPv4 first, then interleaved with IPv6; drops loops back into this proxy.</summary>
    private List<IPAddress> OrderAddresses(IPAddress[] addrs, int port)
    {
        var v4 = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        foreach (var a in addrs)
        {
            var ip = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
            if (IPAddress.IsLoopback(ip) && port == Port) continue;
            if (ip.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip);
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6) v6.Add(ip);
        }
        var result = new List<IPAddress>(v4.Count + v6.Count);
        for (int i = 0; i < Math.Max(v4.Count, v6.Count); i++)
        {
            if (i < v4.Count) result.Add(v4[i]);
            if (i < v6.Count) result.Add(v6[i]);
        }
        if (result.Count > 8) result.RemoveRange(8, result.Count - 8);
        return result;
    }

    /// <summary>Staggered parallel connect: a new attempt starts every 250 ms until one succeeds.</summary>
    private static async Task<Socket?> ConnectAnyAsync(List<IPAddress> addrs, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        var pending = new List<Task<Socket?>>();
        int next = 0;
        Socket? winner = null;
        try
        {
            while (winner is null && !cts.IsCancellationRequested && (next < addrs.Count || pending.Count > 0))
            {
                if (next < addrs.Count) pending.Add(TryConnectAsync(addrs[next++], port, cts.Token));
                var waitSet = new List<Task>(pending);
                if (next < addrs.Count) waitSet.Add(Task.Delay(HappyEyeballsDelay, cts.Token));
                var done = await Task.WhenAny(waitSet).ConfigureAwait(false);
                if (done is Task<Socket?> attempt)
                {
                    pending.Remove(attempt);
                    winner = await attempt.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            cts.Cancel();
            foreach (var t in pending)
                _ = t.ContinueWith(static r => { if (r.IsCompletedSuccessfully) r.Result?.Dispose(); }, TaskScheduler.Default);
        }
        return winner;
    }

    private static async Task<Socket?> TryConnectAsync(IPAddress addr, int port, CancellationToken ct)
    {
        var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await s.ConnectAsync(new IPEndPoint(addr, port), ct).ConfigureAwait(false);
            return s;
        }
        catch
        {
            s.Dispose();
            return null;
        }
    }

    // ------------------------------------------------------------------ HTTP helpers

    private static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (string.IsNullOrEmpty(target)) return false;
        if (target[0] == '[')
        {
            int close = target.IndexOf(']');
            if (close < 0) return false;
            host = target[1..close];
            string rest = target[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != ':' || !int.TryParse(rest.AsSpan(1), out port))) return false;
        }
        else
        {
            int colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon)
            {
                host = target[..colon];
                if (!int.TryParse(target.AsSpan(colon + 1), out port)) return false;
            }
            else host = target;
        }
        return host.Length > 0 && port is > 0 and <= 65535;
    }

    private static bool TryParseAbsoluteHttp(string target, out string host, out int port, out string pathAndQuery)
    {
        host = "";
        port = 80;
        pathAndQuery = "/";
        const string scheme = "http://";
        if (!target.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = target[scheme.Length..];
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? rest : rest[..end];
        if (end >= 0)
        {
            pathAndQuery = rest[end..];
            int hash = pathAndQuery.IndexOf('#');
            if (hash >= 0) pathAndQuery = pathAndQuery[..hash];
            if (pathAndQuery.StartsWith('?')) pathAndQuery = "/" + pathAndQuery;
        }
        int at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        return TrySplitHostPort(authority, 80, out host, out port);
    }

    /// <summary>Converts a proxy request head to origin-form and forces Connection: close.</summary>
    private static byte[] RewriteHead(ReadOnlySpan<byte> head, string method, string pathAndQuery, string version, string host, int port)
    {
        string text = Encoding.Latin1.GetString(head);
        var lines = text.Split("\r\n");
        var sb = new StringBuilder(text.Length + 32);
        sb.Append(method).Append(' ').Append(pathAndQuery).Append(' ').Append(version).Append("\r\n");
        bool hasHost = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            string name = colon > 0 ? line[..colon].Trim() : "";
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            sb.Append(line).Append("\r\n");
        }
        if (!hasHost)
        {
            string h = host.Contains(':') ? $"[{host}]" : host;
            sb.Append("Host: ").Append(h);
            if (port != 80) sb.Append(':').Append(port);
            sb.Append("\r\n");
        }
        sb.Append("Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static Task SendTextAsync(Socket s, string text, CancellationToken ct) =>
        SendAllAsync(s, Encoding.ASCII.GetBytes(text), ct);

    private static async Task SendAllAsync(Socket s, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            int n = await s.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            data = data[n..];
        }
    }

    private static void ShutdownSend(Socket s)
    {
        try { s.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
    }

    // ------------------------------------------------------------------ misc

    private static string Describe(BypassProfile p)
    {
        var t = new List<string>();
        if (p.SplitTlsHello) t.Add(p.SplitAtSni ? "SNI'de bölme" : $"{p.SplitPosition}. baytta bölme");
        if (p.HttpHostTricks) t.Add("HTTP Host hilesi");
        t.Add(p.Dns == DnsMode.DoH ? $"DoH: {DohResolver.FindProvider(p.DohProviderName).Name}" : "sistem DNS");
        return string.Join(", ", t);
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
}
