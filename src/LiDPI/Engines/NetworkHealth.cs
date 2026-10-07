using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LiDPI.Engines;

/// <summary>Why a health target failed.</summary>
public enum HealthErrorKind
{
    /// <summary>Reachable (any HTTP status).</summary>
    None,
    /// <summary>Connection reset / closed during connect or TLS handshake (typical DPI block).</summary>
    Reset,
    /// <summary>TLS failure other than a reset (certificate, protocol, fake certificate...).</summary>
    TlsError,
    /// <summary>No response within the timeout.</summary>
    Timeout,
    /// <summary>Host name could not be resolved.</summary>
    DnsError,
    /// <summary>Anything else (invalid URL, refused, unreachable network...).</summary>
    Other,
}

/// <summary>Result of one URL.</summary>
/// <param name="Url">The URL as given.</param>
/// <param name="Ok">True if any HTTP response (any status code) arrived.</param>
/// <param name="Latency">Time to response headers (or to failure).</param>
/// <param name="StatusCode">HTTP status code when <paramref name="Ok"/>.</param>
/// <param name="Error">Failure category; <see cref="HealthErrorKind.None"/> when OK.</param>
/// <param name="Message">Short technical description of the failure, or null.</param>
public sealed record HealthTargetResult(string Url, bool Ok, TimeSpan Latency, int? StatusCode, HealthErrorKind Error, string? Message);

/// <summary>Results of a <see cref="NetworkHealth.CheckAsync"/> run, in input order.</summary>
public sealed record HealthResult(IReadOnlyList<HealthTargetResult> Targets)
{
    public int OkCount => Targets.Count(t => t.Ok);
    public bool AllOk => Targets.Count > 0 && Targets.All(t => t.Ok);
    public bool AnyOk => Targets.Any(t => t.Ok);
}

/// <summary>
/// HTTPS reachability check. Each URL gets a fresh <see cref="SocketsHttpHandler"/> (no proxy, no pooling,
/// no redirects) and a GET of the URL; any HTTP status counts as reachable. Uses the system resolver, so
/// with the packet engine running the lookups go through its DoH redirection. Thread-safe, no state.
/// </summary>
public static class NetworkHealth
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36";

    /// <summary>Checks all URLs in parallel. Throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> is cancelled.</summary>
    public static async Task<HealthResult> CheckAsync(IEnumerable<string> urls, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(8);
        var tasks = urls.Select(u => CheckOneAsync(u, timeout, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new HealthResult(results);
    }

    private static async Task<HealthTargetResult> CheckOneAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Yield();
        var sw = Stopwatch.StartNew();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return new HealthTargetResult(url, false, TimeSpan.Zero, null, HealthErrorKind.Other, LiDPI.UI.Strings.ErrInvalidUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.Zero,
                PooledConnectionIdleTimeout = TimeSpan.Zero,
                MaxConnectionsPerServer = 1,
                ConnectTimeout = timeout,
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var req = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,*/*");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return new HealthTargetResult(url, true, sw.Elapsed, (int)resp.StatusCode, HealthErrorKind.None, null);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            var elapsed = sw.Elapsed;
            if (cts.IsCancellationRequested)
                return new HealthTargetResult(url, false, elapsed, null, HealthErrorKind.Timeout, LiDPI.UI.Strings.ErrTimeoutSeconds(timeout.TotalSeconds));
            var (kind, msg) = Classify(ex);
            return new HealthTargetResult(url, false, elapsed, null, kind, msg);
        }
    }

    /// <summary>Maps an exception chain to a failure category (reset wins over TLS: DPI resets mid-handshake).</summary>
    internal static (HealthErrorKind Kind, string Message) Classify(Exception ex)
    {
        bool tls = false, io = false, dns = false, timeout = false;
        string msg = ex.Message;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException se:
                    msg = $"Socket {se.SocketErrorCode}";
                    switch (se.SocketErrorCode)
                    {
                        case SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Disconnecting or SocketError.Shutdown:
                            return (HealthErrorKind.Reset, msg);
                        case SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery:
                            dns = true; break;
                        case SocketError.TimedOut:
                            timeout = true; break;
                    }
                    break;
                case AuthenticationException ae:
                    tls = true;
                    msg = $"TLS: {ae.Message}";
                    break;
                case HttpRequestException hre:
                    if (hre.HttpRequestError == HttpRequestError.NameResolutionError) dns = true;
                    else if (hre.HttpRequestError == HttpRequestError.SecureConnectionError) tls = true;
                    break;
                case IOException:
                    io = true;   // e.g. "unexpected EOF or 0 bytes from the transport stream"
                    break;
                case TimeoutException:
                    timeout = true;
                    break;
            }
        }
        if (dns) return (HealthErrorKind.DnsError, msg);
        if (timeout) return (HealthErrorKind.Timeout, msg);
        // TLS handshake aborted by the transport (no AuthenticationException certificate verdict) => reset/drop
        if (io && !HasAuthVerdict(ex)) return (HealthErrorKind.Reset, msg);
        if (tls) return (HealthErrorKind.TlsError, msg);
        return (HealthErrorKind.Other, msg);
    }

    private static bool HasAuthVerdict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is AuthenticationException && e.InnerException is not IOException) return true;
        return false;
    }
}
