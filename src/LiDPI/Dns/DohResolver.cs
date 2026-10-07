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
/// depends on (possibly poisoned) system DNS. Falls back through <see cref="DohProviders.All"/>.
/// Thread-safe.
/// </summary>
public sealed class DohResolver : IDisposable
{
    private static readonly MediaTypeHeaderValue DnsMessage = new("application/dns-message");
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);
    private const int MaxCacheEntries = 4096;

    private readonly DohProvider[] _providers;
    private readonly HttpClient?[] _clients;
    private readonly object _clientsLock = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile int _preferred;
    private volatile bool _disposed;

    private sealed record CacheEntry(IPAddress[] Addresses, long ExpiresAtTicks);

    /// <param name="provider">Primary provider; the remaining built-in providers are used as fallbacks.</param>
    public DohResolver(DohProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var list = new List<DohProvider> { provider };
        list.AddRange(DohProviders.All.Where(p => p.Name != provider.Name));
        _providers = [.. list];
        _clients = new HttpClient?[_providers.Length];
    }

    /// <summary>Timeout of one attempt against a single provider.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Provider that answered last (or the primary one).</summary>
    public DohProvider CurrentProvider => _providers[_preferred];

    /// <summary>Raised when a provider fails and the next one is tried (message is Turkish).</summary>
    public event Action<string>? Warning;

    /// <summary>Finds a built-in provider by name; defaults to Cloudflare.</summary>
    public static DohProvider FindProvider(string? name) =>
        DohProviders.All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? DohProviders.Cloudflare;

    /// <summary>Sends a raw DNS wire query and returns the raw wire response.</summary>
    public async Task<byte[]> QueryRawAsync(byte[] dnsWireQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dnsWireQuery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Exception? last = null;
        int start = _preferred;
        for (int i = 0; i < _providers.Length; i++)
        {
            int idx = (start + i) % _providers.Length;
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await QueryOneAsync(idx, dnsWireQuery, ct).ConfigureAwait(false);
                if (_preferred != idx) _preferred = idx;
                return resp;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !_disposed)
            {
                last = ex;
                if (i + 1 < _providers.Length)
                    Warning?.Invoke($"DoH ({_providers[idx].Name}) yanıt vermedi, {_providers[(idx + 1) % _providers.Length].Name} deneniyor.");
            }
        }
        throw new HttpRequestException("Hiçbir DoH sağlayıcısına ulaşılamadı.", last);
    }

    /// <summary>Resolves A and AAAA records (cached, TTL-respecting). Returns an empty array for NXDOMAIN.</summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];

        long now = Environment.TickCount64;
        if (_cache.TryGetValue(host, out var hit) && hit.ExpiresAtTicks > now) return hit.Addresses;

        var qa = QueryRawAsync(DnsWire.BuildQuery(host, DnsWire.TypeA), ct);
        var q6 = QueryRawAsync(DnsWire.BuildQuery(host, DnsWire.TypeAaaa), ct);

        var list = new List<IPAddress>(4);
        uint minTtl = uint.MaxValue;
        Exception? error = null;
        foreach (var t in new[] { qa, q6 })
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
        lock (_clientsLock)
        {
            for (int i = 0; i < _clients.Length; i++)
            {
                _clients[i]?.Dispose();
                _clients[i] = null;
            }
        }
        _cache.Clear();
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
            throw new InvalidDataException("Geçersiz DoH yanıtı.");
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
        var bootstrap = IPAddress.Parse(p.BootstrapIp);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,              // never loop through our own (system) proxy
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
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
