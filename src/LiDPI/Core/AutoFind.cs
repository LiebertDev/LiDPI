using LiDPI.UI;

namespace LiDPI.Core;

/// <summary>
/// The sites every probe (CLI <c>--probe</c>, the in-app "find best settings" and the health check) tests against.
/// The blocked-site list is user-editable (Settings → "Test siteleri", stored in <see cref="AppSettings.TestSites"/>);
/// the control site stays internal. Thread-safe: the lists are swapped atomically.
/// </summary>
public static class ProbeTargets
{
    /// <summary>Most user-defined test sites.</summary>
    public const int MaxSites = 10;

    /// <summary>Hosts that are typically DPI-blocked in Türkiye (the defaults).</summary>
    public static IReadOnlyList<string> DefaultBlockedHosts { get; } = ["discord.com", "www.roblox.com", "www.wattpad.com"];

    /// <summary>Default test sites as HTTPS URLs.</summary>
    public static IReadOnlyList<string> DefaultBlockedUrls { get; } = [.. DefaultBlockedHosts.Select(ToUrl)];

    /// <summary>A normally reachable host: tells "the line is down" apart from "the bypass does not work".</summary>
    public static string ControlHost { get; } = "www.google.com";

    private static volatile string[] _blockedUrls = [.. DefaultBlockedUrls];
    private static volatile string[] _urls = Build(_blockedUrls);

    /// <summary>Current (user-configured or default) test sites as HTTPS URLs, without the control site.</summary>
    public static IReadOnlyList<string> BlockedUrls => _blockedUrls;

    /// <summary>All targets as HTTPS URLs (test sites first, control last).</summary>
    public static IReadOnlyList<string> Urls => _urls;

    /// <summary>Applies the user's list (null or empty = defaults). Invalid entries are dropped.</summary>
    public static void Configure(IEnumerable<string>? urls)
    {
        var clean = urls is null ? [] : Sanitize(urls);
        var blocked = clean.Count == 0 ? [.. DefaultBlockedUrls] : clean.ToArray();
        _blockedUrls = blocked;
        _urls = Build(blocked);
    }

    public static bool IsControl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && string.Equals(u.Host, ControlHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Host part of a target URL ("discord.com"), for display.</summary>
    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>
    /// "https://host/path" or a bare hostname ("discord.com" → "https://discord.com/"). Only https, no userinfo,
    /// a real host name or IP; never the internal control host.
    /// </summary>
    public static bool TryNormalize(string? input, out string url)
    {
        url = "";
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 300 || s.Any(char.IsWhiteSpace)) return false;
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            if (s.Contains('/') || s.Contains('@')) s = "https://" + s;   // "host/path"
            else s = "https://" + s + "/";
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment)) return false;
        var kind = Uri.CheckHostName(u.IdnHost);
        if (kind == UriHostNameType.Unknown || kind == UriHostNameType.Basic) return false;
        if (kind == UriHostNameType.Dns && !u.IdnHost.Contains('.')) return false;
        if (string.Equals(u.Host, ControlHost, StringComparison.OrdinalIgnoreCase)) return false;
        url = u.AbsoluteUri;
        return true;
    }

    /// <summary>Normalises, de-duplicates (by URL) and caps a stored list.</summary>
    public static List<string> Sanitize(IEnumerable<string> urls)
    {
        var list = new List<string>();
        foreach (var raw in urls)
        {
            if (!TryNormalize(raw, out var u)) continue;
            if (list.Contains(u, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(u);
            if (list.Count >= MaxSites) break;
        }
        return list;
    }

    /// <summary>True if <paramref name="urls"/> equals the default list.</summary>
    public static bool IsDefault(IReadOnlyList<string> urls) =>
        urls.Count == DefaultBlockedUrls.Count && urls.Zip(DefaultBlockedUrls).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    private static string[] Build(string[] blocked) => [.. blocked, ToUrl(ControlHost)];

    private static string ToUrl(string host) => $"https://{host}/";
}

/// <summary>Verdict of an auto-find run.</summary>
public enum AutoFindVerdict
{
    /// <summary>A variant reached at least one blocked target.</summary>
    Found,
    /// <summary>The engine ran but no variant reached any blocked target.</summary>
    NothingWorks,
    /// <summary>Not even the control site answered: the connection itself is down.</summary>
    NoInternet,
    /// <summary>The packet engine could not be started at all.</summary>
    EngineFailed,
}

/// <summary>Result of <see cref="AutoFind.Evaluate"/>.</summary>
public sealed record AutoFindOutcome(
    AutoFindVerdict Verdict, ProbeVariantResult? Winner, BypassProfile? Profile, bool IsBuiltIn, string? Detail);

/// <summary>Candidate set and result interpretation for the in-app "En iyi ayarı otomatik bul" feature.</summary>
public static class AutoFind
{
    /// <summary>Id of the auto-found profile; its shown name is the language key "Profile.auto.Name".</summary>
    public const string CustomProfileId = "auto";

    /// <summary>Each target is requested this many times per variant (robustness against one-off resets).</summary>
    public const int Rounds = 2;

    /// <summary>
    /// Short candidate list for the UI (~11 variants): every built-in profile plus the fake-TTL variants that
    /// differed most in <see cref="ProfileProbe.DefaultCandidates"/> runs. Built-ins come first so that on a tie
    /// in reach and latency a named profile wins.
    /// </summary>
    public static IReadOnlyList<ProbeVariant> QuickCandidates()
    {
        var s2d = new BypassProfile
        {
            Id = "probe", SplitTlsHello = true, SplitAtSni = false, SplitPosition = 2, DisorderSegments = true,
            FakePacket = true, Dns = DnsMode.DoH, BlockQuic = true, HttpHostTricks = true,
        };
        var list = new List<ProbeVariant>();
        foreach (var p in BuiltInProfiles.All) list.Add(new(p.DisplayName, p));
        foreach (int ttl in new[] { 3, 4, 6, 8 })
            list.Add(new(Strings.VariantTtlByte(ttl, 2), s2d with { FakeTtl = ttl }));
        list.Add(new(Strings.VariantTtlSni(4), s2d with { SplitAtSni = true, FakeTtl = 4 }));
        list.Add(new(Strings.VariantTtlRepByte(5, 2, 1), s2d with { SplitPosition = 1, FakeTtl = 5, FakeRepeats = 2 }));
        return list;
    }

    /// <summary>Picks the winner from ranked probe results (best first, as returned by <see cref="ProfileProbe.RunAsync"/>).</summary>
    public static AutoFindOutcome Evaluate(IReadOnlyList<ProbeVariantResult> ranked)
    {
        if (ranked.Count == 0 || ranked.All(r => r.EngineError is not null))
            return new(AutoFindVerdict.EngineFailed, null, null, false, ranked.FirstOrDefault()?.EngineError);

        var best = ranked
            .Where(r => r.EngineError is null)
            .OrderByDescending(BlockedOk)
            .ThenByDescending(r => r.OkCount)
            .ThenBy(r => r.AvgLatencyMs)
            .FirstOrDefault();

        if (best is null || BlockedOk(best) == 0)
        {
            bool controlEverOk = ranked.Any(r => r.Targets.Any(t => t.Ok && ProbeTargets.IsControl(t.Url)));
            return new(controlEverOk ? AutoFindVerdict.NothingWorks : AutoFindVerdict.NoInternet, best, null, false, null);
        }

        var p = best.Variant.Profile!;
        var builtIn = BuiltInProfiles.All.FirstOrDefault(b => b.Id == p.Id);
        if (builtIn is not null) return new(AutoFindVerdict.Found, best, builtIn, true, null);

        // name and description are resolved from the language files when shown (DisplayName / DisplayDescription)
        var custom = p with { Id = CustomProfileId, Name = "", Description = "" };
        return new(AutoFindVerdict.Found, best, custom, false, null);
    }

    /// <summary>Successful requests to blocked (non-control) targets.</summary>
    public static int BlockedOk(ProbeVariantResult r) => r.Targets.Count(t => t.Ok && !ProbeTargets.IsControl(t.Url));

    /// <summary>One-line summary of a profile's techniques in the UI language (same wording as the built-ins).</summary>
    public static string Describe(BypassProfile p)
    {
        var parts = new List<string>();
        if (p.FakePacket)
        {
            string ttl = p.FakeTtl == 0 ? Strings.DescAutoTtl : $"TTL {p.FakeTtl}";
            string rep = p.FakeRepeats > 1 ? $", x{p.FakeRepeats}" : "";
            parts.Add(Strings.DescFakePacket(ttl + rep));
        }
        if (p.SplitTlsHello) parts.Add(p.SplitAtSni ? Strings.DescSplitSni : Strings.DescSplitAt(p.SplitPosition));
        if (p.DisorderSegments) parts.Add(Strings.DescDisorder);
        if (p.Dns == DnsMode.DoH) parts.Add("DoH");
        if (p.BlockQuic) parts.Add(Strings.DescBlockQuic);
        return parts.Count == 0 ? Strings.DescPickedByTest : string.Join(" + ", parts);
    }
}
