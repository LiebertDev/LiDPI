using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LiDPI.Core;
using LiDPI.Engines;

namespace LiDPI.Dns;

/// <summary>
/// DNS-over-HTTPS (RFC 8484) client. Connects to each provider's bootstrap IP directly, so it never
/// depends on (possibly poisoned) system DNS. Falls back through <see cref="DohProviders.All"/>, hedging a
/// slow provider with the next one. Raw wire answers are cached (TTL-respecting, serve-stale) and identical
/// in-flight queries share one upstream request. Thread-safe.
/// </summary>
public sealed class DohResolver : IDisposable
{
    private static readonly MediaTypeHeaderValue DnsMessage = new("application/dns-message");
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);
    private const int MaxCacheEntries = 4096;

    // ---- wire-level answer cache (QueryRawAsync)
    private const int MaxWireEntries = 8192;
    private const uint WireMinTtl = 30, WireMaxTtl = 3600;          // positive answers
    private const uint WireNegMinTtl = 30, WireNegMaxTtl = 300;     // NXDOMAIN / NODATA
    private const uint StaleAnswerTtl = 30;
    private static readonly long StaleWindowMs = (long)TimeSpan.FromDays(1).TotalMilliseconds;
    /// <summary>Start the next provider in parallel when the current one has not answered after this long.</summary>
    private static readonly TimeSpan HedgeDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>Budget for one upstream resolution across all providers.</summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(3);

    private readonly DohProvider[] _providers;
    private readonly HttpClient?[] _clients;
    private readonly object _clientsLock = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<WireKey, WireEntry> _wire = new();
    private readonly ConcurrentDictionary<WireKey, Lazy<Task<byte[]>>> _inflight = new();
    private readonly CancellationTokenSource _life = new();
    private int _pruning;
    /// <summary>Provider asked first: the primary (0), or a fallback while the primary is backed off.</summary>
    private volatile int _preferred;
    /// <summary>Until this tick count the primary is skipped (it failed, or kept losing hedge races).</summary>
    private long _primaryRetryAt;
    /// <summary>Consecutive queries in which the primary was too slow and a fallback answered first.</summary>
    private int _primaryStrikes;
    private const int PrimaryMaxStrikes = 3;
    private static readonly long PrimaryBackoffMs = 30_000;
    private volatile bool _disposed;

    private sealed record CacheEntry(IPAddress[] Addresses, long ExpiresAtTicks);

    /// <summary>Cache key: question + the query bits that change the answer's shape (EDNS present, DO, CD).</summary>
    private readonly record struct WireKey(string Name, ushort Type, ushort Class, byte Flags);

    private sealed class WireEntry(byte[] data, long storedAt, long expiresAt)
    {
        public readonly byte[] Data = data;
        public readonly long StoredAt = storedAt;
        public readonly long ExpiresAt = expiresAt;
        public long LastUsed = storedAt;
    }

    /// <param name="provider">Primary provider; the remaining built-in providers are used as fallbacks.</param>
    public DohResolver(DohProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var list = new List<DohProvider> { provider };
        list.AddRange(DohProviders.All.Where(p => p.Name != provider.Name));
        _providers = [.. list];
        _clients = new HttpClient?[_providers.Length];
    }

    /// <summary>Timeout of one attempt against a single provider (the whole resolution is capped at 3 s).</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// False when this machine has no global IPv6: <see cref="ResolveAsync"/> then returns as soon as the A answer
    /// is in (AAAA is only asked when there is no A record).
    /// </summary>
    public bool Ipv6Available { get; set; } = true;

    /// <summary>Provider asked first right now: the primary one, or a fallback while the primary is backed off.</summary>
    public DohProvider CurrentProvider => _providers[StartIndex(Environment.TickCount64)];

    private int StartIndex(long now) =>
        _preferred != 0 && now < Interlocked.Read(ref _primaryRetryAt) ? _preferred : 0;

    /// <summary>
    /// Records which provider answered. The user's provider stays first unless it failed outright or lost
    /// <see cref="PrimaryMaxStrikes"/> hedge races in a row; then a fallback leads for 30 s and the primary is retried.
    /// </summary>
    private void NoteWinner(int idx, bool primaryFailed)
    {
        long now = Environment.TickCount64;
        if (idx == 0)
        {
            Volatile.Write(ref _primaryStrikes, 0);
            _preferred = 0;
            return;
        }
        if (primaryFailed || Interlocked.Increment(ref _primaryStrikes) >= PrimaryMaxStrikes)
        {
            Volatile.Write(ref _primaryStrikes, 0);
            _preferred = idx;
            Interlocked.Exchange(ref _primaryRetryAt, now + PrimaryBackoffMs);
        }
    }

    /// <summary>Raised when a provider fails and the next one is tried (message is Turkish).</summary>
    public event Action<string>? Warning;

    /// <summary>
    /// Finds a provider by name: a built-in one, or the configured custom provider for
    /// <see cref="DohProviders.CustomKey"/>; defaults to Cloudflare.
    /// </summary>
    public static DohProvider FindProvider(string? name)
    {
        if (string.Equals(name, DohProviders.CustomKey, StringComparison.OrdinalIgnoreCase))
            return DohProviders.Custom ?? DohProviders.Cloudflare;
        return DohProviders.All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? DohProviders.Cloudflare;
    }

    /// <summary>
    /// Engine start: <see cref="FindProvider"/> plus, for a custom provider without a bootstrap IP, one lookup of its
    /// host through the system resolver (before any packet interception is active). Falls back to Cloudflare if that
    /// fails. Blocking; call off the UI thread.
    /// </summary>
    public static DohProvider PrepareProvider(string? name, Action<string>? log)
    {
        var p = FindProvider(name);
        if (!string.IsNullOrEmpty(p.BootstrapIp)) return p;
        try
        {
            var ip = ResolveBootstrap(p);
            log?.Invoke(LiDPI.UI.Strings.LogDohBootstrapResolved(new Uri(p.Url).Host, ip.ToString()));
            return p with { BootstrapIp = ip.ToString() };
        }
        catch (Exception ex)
        {
            log?.Invoke(LiDPI.UI.Strings.LogDohBootstrapFailed(new Uri(p.Url).Host, ex.Message));
            return DohProviders.Cloudflare;
        }
    }

    /// <summary>
    /// One DoH query for example.com against <paramref name="provider"/> only (no fallback, no cache).
    /// Returns the round-trip time; throws on any failure. Resolves an empty bootstrap IP via system DNS first.
    /// </summary>
    public static async Task<TimeSpan> TestAsync(DohProvider provider, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // one budget for the whole test, including the system-DNS bootstrap lookup
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        if (string.IsNullOrEmpty(provider.BootstrapIp))
        {
            var ip = await ResolveBootstrapAsync(provider, cts.Token).ConfigureAwait(false);
            provider = provider with { BootstrapIp = ip.ToString() };
        }
        using var client = CreateClient(provider);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var content = new ByteArrayContent(DnsWire.BuildQuery("example.com", DnsWire.TypeA));
        content.Headers.ContentType = DnsMessage;
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.Url)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        sw.Stop();
        if (body.Length < 12 || (body[2] & 0x80) == 0) throw new InvalidDataException(LiDPI.UI.Strings.ErrDohInvalidResponse);
        var list = new List<IPAddress>(2);
        uint ttl = uint.MaxValue;
        DnsWire.TryParseAddresses(body, list, ref ttl);
        if (list.Count == 0) throw new InvalidDataException(LiDPI.UI.Strings.ErrDohNoAddress);
        return sw.Elapsed;
    }

    /// <summary>System-resolver lookup of a provider's host (IPv4 preferred).</summary>
    private static IPAddress ResolveBootstrap(DohProvider p)
    {
        var host = new Uri(p.Url).IdnHost;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return literal;
        return PickBootstrap(System.Net.Dns.GetHostAddresses(host));
    }

    /// <summary>Cancellable variant of <see cref="ResolveBootstrap"/> (the lookup itself honours <paramref name="ct"/>).</summary>
    private static async Task<IPAddress> ResolveBootstrapAsync(DohProvider p, CancellationToken ct)
    {
        var host = new Uri(p.Url).IdnHost;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return literal;
        return PickBootstrap(await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false));
    }

    private static IPAddress PickBootstrap(IPAddress[] addrs) =>
        addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
        ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6)
        ?? throw new SocketException((int)SocketError.HostNotFound);

    /// <summary>
    /// Sends a raw DNS wire query and returns the raw wire response (a fresh array the caller may modify; its
    /// transaction ID is the query's). Answers come from the cache when possible: fresh ones with their remaining
    /// TTL, expired ones (up to a day) at once with TTL 30 s while a background refresh runs. Identical queries in
    /// flight share one upstream request.
    /// </summary>
    public async Task<byte[]> QueryRawAsync(byte[] dnsWireQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dnsWireQuery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryGetWireKey(dnsWireQuery, out var key))
            return await QueryUpstreamAsync(dnsWireQuery, ct).ConfigureAwait(false);   // not cacheable

        long now = Environment.TickCount64;
        if (_wire.TryGetValue(key, out var hit))
        {
            if (hit.ExpiresAt > now)
            {
                Volatile.Write(ref hit.LastUsed, now);
                return Materialize(hit, dnsWireQuery, now, stale: false);
            }
            if (now - hit.ExpiresAt < StaleWindowMs)
            {
                Volatile.Write(ref hit.LastUsed, now);
                // off this thread: starting the request (client creation, connect) must not delay the stale answer
                var refresh = Task.Run(() => Fetch(key, dnsWireQuery));
                _ = refresh.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return Materialize(hit, dnsWireQuery, now, stale: true);
            }
            _wire.TryRemove(new KeyValuePair<WireKey, WireEntry>(key, hit));
        }

        var shared = await Fetch(key, dnsWireQuery).WaitAsync(ct).ConfigureAwait(false);
        var copy = (byte[])shared.Clone();
        AdoptQuery(copy, dnsWireQuery);
        return copy;
    }

    /// <summary>
    /// Gives a shared answer the query's transaction ID and the exact spelling of its question name (resolvers
    /// using DNS 0x20 case randomisation compare it), when both questions have the same wire length.
    /// </summary>
    private static void AdoptQuery(byte[] resp, byte[] query)
    {
        resp[0] = query[0];
        resp[1] = query[1];
        if (DnsWire.TryReadQuestion(query, out _, out _, out _, out int qEnd) &&
            DnsWire.TryReadQuestion(resp, out _, out _, out _, out int rEnd) && qEnd == rEnd)
            query.AsSpan(12, qEnd - 16).CopyTo(resp.AsSpan(12));
    }

    /// <summary>
    /// Fire-and-forget query (cloudflare.com A) that opens the HTTPS connection to the primary provider, so the
    /// first real lookup does not pay for the TCP + TLS handshake. Never throws.
    /// </summary>
    public void Warmup()
    {
        if (_disposed) return;
        try
        {
            _ = QueryRawAsync(DnsWire.BuildQuery("cloudflare.com", DnsWire.TypeA), _life.Token)
                .ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch
        {
            // disposed in between
        }
    }

    /// <summary>One upstream request per key at a time; the shared response is cached when it completes.</summary>
    private Task<byte[]> Fetch(WireKey key, byte[] query)
    {
        var mine = new Lazy<Task<byte[]>>(() => FetchCoreAsync(key, query), LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = _inflight.GetOrAdd(key, mine);
        var task = actual.Value;
        if (ReferenceEquals(actual, mine))
        {
            _ = task.ContinueWith(_ => _inflight.TryRemove(new KeyValuePair<WireKey, Lazy<Task<byte[]>>>(key, mine)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return task;
    }

    private async Task<byte[]> FetchCoreAsync(WireKey key, byte[] query)
    {
        // not tied to any single caller: others may be waiting on the same request
        var resp = await QueryUpstreamAsync(query, _life.Token).ConfigureAwait(false);
        Store(key, resp);
        return resp;
    }

    private void Store(WireKey key, byte[] resp)
    {
        if (_disposed) return;
        uint? ttl = DnsWire.GetCacheTtl(resp, out bool negative);
        if (ttl is not { } t) return;
        // the answer must be for the question asked
        if (!DnsWire.TryReadQuestion(resp, out var name, out var type, out var cls, out _) ||
            name != key.Name || type != key.Type || cls != key.Class) return;
        t = negative ? Math.Clamp(t, WireNegMinTtl, WireNegMaxTtl) : Math.Clamp(t, WireMinTtl, WireMaxTtl);
        long now = Environment.TickCount64;
        if (_wire.Count >= MaxWireEntries) PruneWire(now);
        _wire[key] = new WireEntry(resp, now, now + t * 1000L);
    }

    /// <summary>A copy of a cached answer with the query's ID and TTLs reduced by its age (or 30 s when stale).</summary>
    private static byte[] Materialize(WireEntry e, byte[] query, long now, bool stale)
    {
        var copy = (byte[])e.Data.Clone();
        AdoptQuery(copy, query);
        uint elapsed = (uint)Math.Clamp((now - e.StoredAt) / 1000, 0, uint.MaxValue);
        DnsWire.RewriteTtls(copy, elapsed, stale ? StaleAnswerTtl : null);
        return copy;
    }

    /// <summary>Standard query (opcode 0) with one question: key = name, type, class, EDNS present / DO / CD.</summary>
    private static bool TryGetWireKey(byte[] query, out WireKey key)
    {
        key = default;
        if (query.Length < 12 || (query[2] & 0x80) != 0 || (query[2] & 0x78) != 0) return false;   // response / opcode != 0
        if (!DnsWire.TryReadQuestion(query, out var name, out var type, out var cls, out _)) return false;
        if (type is 251 or 252 or 255) return false;   // IXFR / AXFR / ANY
        if (!DnsWire.TryGetEdns(query, out bool hasOpt, out bool doBit)) return false;
        byte flags = (byte)((hasOpt ? 1 : 0) | (doBit ? 2 : 0) | ((query[3] & 0x10) != 0 ? 4 : 0));
        key = new WireKey(name, type, cls, flags);
        return true;
    }

    /// <summary>Drops entries past the stale window; if still full, the least recently used eighth (one thread at a time).</summary>
    private void PruneWire(long now)
    {
        if (Interlocked.Exchange(ref _pruning, 1) != 0) return;
        try
        {
            foreach (var kv in _wire)
                if (now - kv.Value.ExpiresAt >= StaleWindowMs) _wire.TryRemove(kv);
            int excess = _wire.Count - MaxWireEntries * 7 / 8;
            if (excess <= 0) return;
            foreach (var kv in _wire.ToArray().OrderBy(kv => Volatile.Read(ref kv.Value.LastUsed)).Take(excess))
                _wire.TryRemove(kv);
        }
        finally
        {
            Volatile.Write(ref _pruning, 0);
        }
    }

    /// <summary>
    /// Asks the preferred provider; if it has not answered after <see cref="HedgeDelay"/> (or failed), the next one
    /// is started in parallel. The first valid answer wins; the rest are cancelled. Capped at <see cref="TotalTimeout"/>.
    /// </summary>
    private async Task<byte[]> QueryUpstreamAsync(byte[] query, CancellationToken ct)
    {
        int n = _providers.Length;
        int start = StartIndex(Environment.TickCount64);
        int launched = 0;
        bool primaryFailed = false;
        Exception? last = null;
        var running = new List<Task<byte[]>>(n);
        var indexOf = new Dictionary<Task<byte[]>, int>(n);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TotalTimeout);

        void Launch()
        {
            int idx = (start + launched++) % n;
            var t = QueryOneAsync(idx, query, total.Token);
            running.Add(t);
            indexOf[t] = idx;
        }

        try
        {
            Launch();
            while (running.Count > 0)
            {
                var waitSet = new List<Task>(running.Count + 1);
                waitSet.AddRange(running);
                Task? hedge = launched < n ? Task.Delay(HedgeDelay, total.Token) : null;
                if (hedge is not null) waitSet.Add(hedge);
                var done = await Task.WhenAny(waitSet).ConfigureAwait(false);

                if (ReferenceEquals(done, hedge))
                {
                    if (total.IsCancellationRequested) break;
                    Launch();   // slow provider: race the next one
                    continue;
                }

                var attempt = (Task<byte[]>)done;
                running.Remove(attempt);
                int idx = indexOf[attempt];
                if (attempt.IsCompletedSuccessfully)
                {
                    // a fallback that won while it led (primary backed off) keeps leading; otherwise judge the primary
                    if (start == 0 || idx == 0) NoteWinner(idx, primaryFailed);
                    return attempt.Result;
                }
                if (idx == 0 && !total.IsCancellationRequested) primaryFailed = true;
                last = attempt.Exception?.GetBaseException() ?? last;
                if (ct.IsCancellationRequested || _disposed) break;
                if (total.IsCancellationRequested) continue;   // budget spent: let the others finish cancelling
                if (launched < n)
                {
                    Warning?.Invoke(LiDPI.UI.Strings.LogDohSwitch(_providers[idx].Name, _providers[(start + launched) % n].Name));
                    Launch();   // failed outright: next provider at once
                }
            }
        }
        finally
        {
            try { total.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var t in running)
                _ = t.ContinueWith(static r => _ = r.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new HttpRequestException(LiDPI.UI.Strings.ErrDohAllFailed, last ?? new TimeoutException());
    }

    /// <summary>Resolves A and AAAA records (cached, TTL-respecting). Returns an empty array for NXDOMAIN.</summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];

        long now = Environment.TickCount64;
        if (_cache.TryGetValue(host, out var hit) && hit.ExpiresAtTicks > now) return hit.Addresses;

        var list = new List<IPAddress>(4);
        uint minTtl = uint.MaxValue;
        Exception? error = null;

        async Task CollectAsync(Task<byte[]> t)
        {
            try
            {
                var resp = await t.ConfigureAwait(false);
                DnsWire.TryParseAddresses(resp, list, ref minTtl);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error ??= ex;
            }
        }

        if (Ipv6Available)
        {
            var qa = QueryRawAsync(DnsWire.BuildQuery(host, DnsWire.TypeA), ct);
            var q6 = QueryRawAsync(DnsWire.BuildQuery(host, DnsWire.TypeAaaa), ct);
            await CollectAsync(qa).ConfigureAwait(false);
            await CollectAsync(q6).ConfigureAwait(false);
        }
        else
        {
            // no global IPv6 here: AAAA records could not be used anyway, so do not wait for them
            await CollectAsync(QueryRawAsync(DnsWire.BuildQuery(host, DnsWire.TypeA), ct)).ConfigureAwait(false);
            if (list.Count == 0 && !ct.IsCancellationRequested)
                await CollectAsync(QueryRawAsync(DnsWire.BuildQuery(host, DnsWire.TypeAaaa), ct)).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (list.Count == 0 && error is not null) throw error;

        var ttl = minTtl == uint.MaxValue ? MinTtl : TimeSpan.FromSeconds(minTtl);
        if (ttl < MinTtl) ttl = MinTtl;
        if (ttl > MaxTtl) ttl = MaxTtl;

        var arr = list.Distinct().ToArray();
        if (_cache.Count >= MaxCacheEntries) PruneCache(now);
        _cache[host] = new CacheEntry(arr, now + (long)ttl.TotalMilliseconds);
        return arr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        lock (_clientsLock)
        {
            for (int i = 0; i < _clients.Length; i++)
            {
                _clients[i]?.Dispose();
                _clients[i] = null;
            }
        }
        _cache.Clear();
        _wire.Clear();
        _inflight.Clear();
    }

    private async Task<byte[]> QueryOneAsync(int idx, byte[] query, CancellationToken ct)
    {
        var client = GetClient(idx);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AttemptTimeout);

        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = DnsMessage;
        using var req = new HttpRequestMessage(HttpMethod.Post, _providers[idx].Url)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        if (body.Length < 12 || (body[2] & 0x80) == 0)
            throw new InvalidDataException(LiDPI.UI.Strings.ErrDohInvalidResponse);
        return body;
    }

    private HttpClient GetClient(int idx)
    {
        var c = Volatile.Read(ref _clients[idx]);
        if (c is not null) return c;
        lock (_clientsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _clients[idx] ??= CreateClient(_providers[idx]);
        }
    }

    private static HttpClient CreateClient(DohProvider p)
    {
        // a custom provider normally arrives here already prepared; resolve as a last resort
        var bootstrap = string.IsNullOrEmpty(p.BootstrapIp) ? ResolveBootstrap(p) : IPAddress.Parse(p.BootstrapIp);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,              // never loop through our own (system) proxy
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            EnableMultipleHttp2Connections = true,
            // keep the (expensive) HTTPS connection open across quiet periods; pings detect a dead one early
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            ConnectCallback = async (ctx, token) =>
            {
                var s = new Socket(bootstrap.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(new IPEndPoint(bootstrap, ctx.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new HelloSplitStream(new NetworkStream(s, ownsSocket: true));
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,   // per-attempt timeout via CancellationToken
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        return client;
    }

    private void PruneCache(long now)
    {
        foreach (var kv in _cache)
            if (kv.Value.ExpiresAtTicks <= now) _cache.TryRemove(kv.Key, out _);
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
    }

    /// <summary>
    /// Stream wrapper that writes the first TLS ClientHello in two TCP segments split inside the SNI,
    /// so the DoH handshake itself is not trivially SNI-filtered.
    /// </summary>
    private sealed class HelloSplitStream(NetworkStream inner) : Stream
    {
        private bool _first = true;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int split = TakeSplit(buffer);
            if (split > 0)
            {
                inner.Write(buffer[..split]);
                inner.Write(buffer[split..]);
            }
            else inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            int split = TakeSplit(buffer.Span);
            if (split > 0)
            {
                await inner.WriteAsync(buffer[..split], ct).ConfigureAwait(false);
                await inner.WriteAsync(buffer[split..], ct).ConfigureAwait(false);
            }
            else await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        private int TakeSplit(ReadOnlySpan<byte> data)
        {
            if (!_first) return -1;
            _first = false;
            if (!TlsClientHello.TryFindSni(data, out int o, out int l)) return -1;
            int split = o + l / 2;
            return split > 0 && split < data.Length ? split : -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
