using LiDPI.Engines;

namespace LiDPI.UI.Services;

/// <summary>One URL's reachability.</summary>
public sealed record HealthSample(string Url, bool Ok, double LatencyMs);

/// <summary>Reachability check used by the periodic health monitor (thin adapter over <see cref="NetworkHealth"/>).</summary>
public static class HealthCheck
{
    public static async Task<IReadOnlyList<HealthSample>> RunAsync(IReadOnlyList<string> urls, TimeSpan timeout, CancellationToken ct)
    {
        var result = await NetworkHealth.CheckAsync(urls, timeout, ct).ConfigureAwait(false);
        return result.Targets
            .Select(t => new HealthSample(t.Url, t.Ok, t.Ok ? t.Latency.TotalMilliseconds : double.PositiveInfinity))
            .ToList();
    }
}
