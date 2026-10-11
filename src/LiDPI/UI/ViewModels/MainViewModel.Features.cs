using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using LiDPI.Core;
using LiDPI.Dns;
using LiDPI.Localization;
using LiDPI.UI.Services;

namespace LiDPI.UI.ViewModels;

/// <summary>Visual tone of a one-line status next to a settings button.</summary>
public enum InlineStatusKind { None, Busy, Ok, Info, Error }

/// <summary>One row of the "Test siteleri" list.</summary>
public sealed record TestSiteItem(string Url)
{
    public string Host => ProbeTargets.HostOf(Url);
    /// <summary>Shown under the host when the URL has a path ("/channels").</summary>
    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.PathAndQuery != "/" ? u.PathAndQuery : "";
}

/// <summary>
/// v1.1 settings features: UI language, manual update check, custom test sites, custom DoH provider, diagnostic report.
/// UI thread only; network and file work runs on the thread pool.
/// </summary>
public sealed partial class MainViewModel
{
    private const int DiagLogLines = 300;
    private static readonly TimeSpan DohTestTimeout = TimeSpan.FromSeconds(6);

    private readonly Queue<LogEntry> _diagLog = new();
    private string _lastHealthSummary = "";
    private string _lastAutoFindSummary = "";

    private void InitFeatures()
    {
        ProbeTargets.Configure(_settings.TestSites);
        foreach (var u in ProbeTargets.BlockedUrls) TestSites.Add(new TestSiteItem(u));
        TestSites.CollectionChanged += (_, _) => OnTestSitesChanged();

        _customDohUrl = _settings.CustomDohUrl ?? "";
        _customDohBootstrap = _settings.CustomDohBootstrap ?? "";
        DohProviders.Custom = DohProviders.CreateCustom(_settings.CustomDohUrl, _settings.CustomDohBootstrap);
        if (_selectedDoh.IsCustom && DohProviders.Custom is null) SetDohStatus(nameof(Strings.DohCustomNotSaved), InlineStatusKind.Info);
    }

    // ===================== UI language =====================

    private void InitLanguage() => Loc.LanguageChanged += (_, _) => OnLanguageChanged();

    /// <summary>Built-in languages plus any language files found in %AppData%\LiDPI\lang or next to the exe.</summary>
    public IReadOnlyList<LanguageInfo> Languages => Loc.Instance.Languages;

    public LanguageInfo? SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => string.Equals(l.Code, Loc.Instance.Code, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || string.Equals(value.Code, Loc.Instance.Code, StringComparison.OrdinalIgnoreCase)) return;
            _settings.Language = value.Code;
            _settings.Save();
            Loc.Instance.SetLanguage(value.Code);   // raises LanguageChanged → OnLanguageChanged
        }
    }

    /// <summary>
    /// Every text property is computed from the current language (or stored as <see cref="LocText"/>), so one
    /// "all properties changed" notification re-renders the whole window in the new language.
    /// </summary>
    private void OnLanguageChanged()
    {
        UpdateHealthLabel();
        OnPropertyChanged(string.Empty);
    }

    public string AboutVersionText => Strings.AboutVersion(AppVersion);

    // ===================== manual update check =====================

    /// <summary>
    /// The only GitHub request the app makes, and only when the user presses "Check for updates". Network on the thread pool.
    /// </summary>
    public Func<CancellationToken, Task<UpdateCheckResult>> ManualUpdateChecker { get; set; } = UpdateService.CheckDetailedAsync;

    /// <summary>Opens a release page (replaceable for UI tests). Returns false if refused / failed.</summary>
    public Func<UpdateInfo, bool> ReleasePageOpener { get; set; } = UpdateService.OpenReleasePage;

    private bool _updateCheckBusy;
    private LocText _updateCheckText;
    private InlineStatusKind _updateCheckKind;

    public bool UpdateCheckBusy
    {
        get => _updateCheckBusy;
        private set { if (Set(ref _updateCheckBusy, value)) OnPropertyChanged(nameof(CanCheckUpdates)); }
    }

    public bool CanCheckUpdates => !_updateCheckBusy;
    public string UpdateCheckText => _updateCheckText.ToString();
    public InlineStatusKind UpdateCheckKind { get => _updateCheckKind; private set => Set(ref _updateCheckKind, value); }

    /// <summary>
    /// "Check for updates" (Settings → About, tray): asks GitHub now; a newer release opens directly in the browser
    /// (validated URL). Returns the status line (also used by the tray balloon); null if a check is already running.
    /// </summary>
    public async Task<string?> CheckForUpdatesManualAsync()
    {
        if (_updateCheckBusy) return null;
        UpdateCheckBusy = true;
        SetUpdateCheck(LocText.Of(nameof(Strings.UpdateCheckBusy)), InlineStatusKind.Busy);
        try
        {
            var checker = ManualUpdateChecker;
            UpdateCheckResult r;
            try { r = await Task.Run(() => checker(CancellationToken.None)); }
            catch { r = new UpdateCheckResult(UpdateCheckStatus.Failed, null); }

            switch (r.Status)
            {
                case UpdateCheckStatus.Available when r.Info is { } info:
                    AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogUpdateAvailable(info.DisplayVersion)));
                    if (UpdateService.IsTrustedReleasePage(info.HtmlUrl) && ReleasePageOpener(info))
                        SetUpdateCheck(LocText.Of(nameof(Strings.UpdateCheckOpened), info.DisplayVersion), InlineStatusKind.Ok);
                    else
                    {
                        AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogUpdatePageFailed));
                        SetUpdateCheck(LocText.Of(nameof(Strings.LogUpdatePageFailed)), InlineStatusKind.Error);
                    }
                    break;
                case UpdateCheckStatus.UpToDate:
                    SetUpdateCheck(LocText.Of(nameof(Strings.UpdateCheckUpToDate)), InlineStatusKind.Ok);
                    break;
                default:
                    SetUpdateCheck(LocText.Of(nameof(Strings.UpdateCheckFailed)), InlineStatusKind.Error);
                    break;
            }
            return UpdateCheckText;
        }
        finally { UpdateCheckBusy = false; }
    }

    private void SetUpdateCheck(LocText text, InlineStatusKind kind)
    {
        _updateCheckText = text;
        OnPropertyChanged(nameof(UpdateCheckText));
        UpdateCheckKind = kind;
    }

    // ===================== test sites =====================

    private string _newTestSite = "";
    private LocText _testSiteError;

    /// <summary>Editable list of sites tested by auto-find and the health check (control site excluded).</summary>
    public ObservableCollection<TestSiteItem> TestSites { get; } = new();

    public string NewTestSite
    {
        get => _newTestSite;
        set { if (Set(ref _newTestSite, value ?? "")) SetTestSiteError(LocText.Empty); }
    }

    public string TestSiteError => _testSiteError.ToString();

    private void SetTestSiteError(LocText text)
    {
        _testSiteError = text;
        OnPropertyChanged(nameof(TestSiteError));
    }

    public bool CanResetTestSites => !ProbeTargets.IsDefault(TestSites.Select(t => t.Url).ToList());
    public bool CanRemoveTestSite => TestSites.Count > 1;

    /// <summary>"Test edilen siteler: discord.com, www.roblox.com, …" (auto-find sheet).</summary>
    public string TestSitesSummary => Strings.AutoFindTestedSites(
        string.Join(", ", TestSites.Take(4).Select(t => t.Host)) + (TestSites.Count > 4 ? ", …" : ""));

    /// <summary>Validates <see cref="NewTestSite"/> and appends it. Returns true on success.</summary>
    public bool AddTestSite()
    {
        if (!ProbeTargets.TryNormalize(NewTestSite, out var url)) { SetTestSiteError(LocText.Of(nameof(Strings.TestSitesInvalid))); return false; }
        if (TestSites.Any(t => string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase))) { SetTestSiteError(LocText.Of(nameof(Strings.TestSitesDuplicate))); return false; }
        if (TestSites.Count >= ProbeTargets.MaxSites) { SetTestSiteError(LocText.Of(nameof(Strings.TestSitesFull), ProbeTargets.MaxSites)); return false; }
        TestSites.Add(new TestSiteItem(url));
        NewTestSite = "";
        return true;
    }

    public void RemoveTestSite(TestSiteItem? item)
    {
        if (item is null) return;
        if (TestSites.Count <= 1) { SetTestSiteError(LocText.Of(nameof(Strings.TestSitesLastOne))); return; }
        TestSites.Remove(item);
    }

    public void ResetTestSites()
    {
        TestSites.Clear();
        foreach (var u in ProbeTargets.DefaultBlockedUrls) TestSites.Add(new TestSiteItem(u));
        SetTestSiteError(LocText.Empty);
    }

    private void OnTestSitesChanged()
    {
        var urls = TestSites.Select(t => t.Url).ToList();
        _settings.TestSites = ProbeTargets.IsDefault(urls) ? null : urls;
        _settings.Save();
        ProbeTargets.Configure(urls);
        // a verdict about the old list says nothing about the new one
        _healthBlockedStreak = 0;
        OnPropertyChanged(nameof(CanResetTestSites));
        OnPropertyChanged(nameof(CanRemoveTestSite));
        OnPropertyChanged(nameof(TestSitesSummary));
    }

    // ===================== custom DoH provider =====================

    private string _customDohUrl = "";
    private string _customDohBootstrap = "";
    private LocText _customDohStatus;
    private InlineStatusKind _customDohStatusKind;
    private bool _customDohTesting;

    public bool IsCustomDohSelected => _selectedDoh.IsCustom;

    public string CustomDohUrl
    {
        get => _customDohUrl;
        set { if (Set(ref _customDohUrl, value ?? "")) ClearDohStatusUnlessBusy(); }
    }

    public string CustomDohBootstrap
    {
        get => _customDohBootstrap;
        set { if (Set(ref _customDohBootstrap, value ?? "")) ClearDohStatusUnlessBusy(); }
    }

    public string CustomDohStatus => _customDohStatus.ToString();
    public InlineStatusKind CustomDohStatusKind { get => _customDohStatusKind; private set => Set(ref _customDohStatusKind, value); }

    public bool CustomDohTesting
    {
        get => _customDohTesting;
        private set { if (Set(ref _customDohTesting, value)) OnPropertyChanged(nameof(CanTestCustomDoh)); }
    }

    public bool CanTestCustomDoh => !_customDohTesting;

    /// <summary>Validates the two fields; on success returns the provider they describe.</summary>
    private DohProvider? ValidateCustomDoh()
    {
        if (!DohProviders.TryNormalizeUrl(_customDohUrl, out _, out _))
        {
            SetDohStatus(nameof(Strings.DohCustomInvalidUrl), InlineStatusKind.Error);
            return null;
        }
        if (!string.IsNullOrWhiteSpace(_customDohBootstrap) && !DohProviders.TryParseBootstrap(_customDohBootstrap, out _))
        {
            SetDohStatus(nameof(Strings.DohCustomInvalidIp), InlineStatusKind.Error);
            return null;
        }
        return DohProviders.CreateCustom(_customDohUrl, _customDohBootstrap);
    }

    /// <summary>"Kaydet": stores the custom provider and applies it (restart if running with it selected).</summary>
    public bool SaveCustomDoh()
    {
        if (ValidateCustomDoh() is not { } p) return false;
        DohProviders.TryNormalizeUrl(_customDohUrl, out var url, out var host);
        _settings.CustomDohUrl = url;
        _settings.CustomDohBootstrap = string.IsNullOrWhiteSpace(_customDohBootstrap) ? null : p.BootstrapIp;
        _settings.Save();
        DohProviders.Custom = p;
        _customDohUrl = url;
        OnPropertyChanged(nameof(CustomDohUrl));
        if (_settings.CustomDohBootstrap is { } ip) { _customDohBootstrap = ip; OnPropertyChanged(nameof(CustomDohBootstrap)); }
        SetDohStatus(nameof(Strings.DohCustomSaved), InlineStatusKind.Ok);
        AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogDohCustomSaved(host)));
        if (_selectedDoh.IsCustom) RestartIfRunning();
        return true;
    }

    /// <summary>"Test et": one DoH query for example.com against the typed provider only.</summary>
    public async Task TestCustomDohAsync()
    {
        if (_customDohTesting || ValidateCustomDoh() is not { } p) return;
        CustomDohTesting = true;
        SetDohStatus(nameof(Strings.DohCustomTesting), InlineStatusKind.Busy);
        try
        {
            var elapsed = await Task.Run(() => DohTester(p, DohTestTimeout, CancellationToken.None));
            SetDohStatus(LocText.Of(nameof(Strings.DohCustomTestOk), Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds))), InlineStatusKind.Ok);
        }
        catch (OperationCanceledException)
        {
            SetDohStatus(LocText.Of(nameof(Strings.DohCustomTestFailed), LocText.Of(nameof(Strings.DohCustomTestTimeout))), InlineStatusKind.Error);
        }
        catch (Exception ex)
        {
            var msg = (ex.InnerException ?? ex).Message;
            if (msg.Length > 90) msg = msg[..90] + "…";
            SetDohStatus(LocText.Of(nameof(Strings.DohCustomTestFailed), msg), InlineStatusKind.Error);
        }
        finally { CustomDohTesting = false; }
    }

    /// <summary>The DoH test (replaceable for UI tests). Called on the thread pool.</summary>
    public Func<DohProvider, TimeSpan, CancellationToken, Task<TimeSpan>> DohTester { get; set; } = DohResolver.TestAsync;

    private void OnCustomDohSelectionChanged()
    {
        if (_customDohTesting) return;
        if (_selectedDoh.IsCustom && DohProviders.Custom is null) SetDohStatus(nameof(Strings.DohCustomNotSaved), InlineStatusKind.Info);
        else SetDohStatus(LocText.Empty, InlineStatusKind.None);
    }

    private void ClearDohStatusUnlessBusy()
    {
        if (!_customDohTesting && _customDohStatusKind != InlineStatusKind.None) SetDohStatus(LocText.Empty, InlineStatusKind.None);
    }

    private void SetDohStatus(string key, InlineStatusKind kind) => SetDohStatus(LocText.Of(key), kind);

    private void SetDohStatus(LocText text, InlineStatusKind kind)
    {
        _customDohStatus = text;
        OnPropertyChanged(nameof(CustomDohStatus));
        CustomDohStatusKind = kind;
    }

    // ===================== diagnostic report =====================

    private void RememberForDiagnostics(LogEntry e)
    {
        _diagLog.Enqueue(e);
        while (_diagLog.Count > DiagLogLines) _diagLog.Dequeue();
    }

    private void RememberHealth(IReadOnlyList<HealthSample> samples, int blockedOk, int blockedTotal, bool controlOk)
    {
        var sb = new StringBuilder();
        sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {blockedOk}/{blockedTotal} test sites ok, control {(controlOk ? "ok" : "failed")}");
        foreach (var s in samples)
            sb.Append($"{Environment.NewLine}  {ProbeTargets.HostOf(s.Url)}: {(s.Ok ? $"ok {s.LatencyMs:0} ms" : "failed")}");
        _lastHealthSummary = sb.ToString();
    }

    private void RememberAutoFind(AutoFindOutcome o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: verdict {o.Verdict}" +
                      (o.Winner is { } w ? $", winner '{w.Variant.Name}' {w.OkCount}/{w.Targets.Count}, avg {w.AvgLatencyMs:0} ms" : "") +
                      (o.Detail is { } d ? $", detail: {d}" : ""));
        sb.AppendLine("  targets: " + string.Join(", ", ProbeTargets.Urls.Select(ProbeTargets.HostOf)));
        foreach (var r in AutoFindRows) sb.AppendLine($"  {r.Name}: {r.Score}, {r.Latency}");
        _lastAutoFindSummary = sb.ToString();
    }

    /// <summary>Captures the report inputs (UI thread), then builds and writes the file on the thread pool.</summary>
    public async Task<bool> SaveDiagnosticReportAsync(string path)
    {
        FlushLogs();
        var settings = new List<KeyValuePair<string, string>>
        {
            new("StartWithWindows", _settings.StartWithWindows.ToString()),
            new("StartMinimized", _settings.StartMinimized.ToString()),
            new("AutoConnectOnLaunch", _settings.AutoConnectOnLaunch.ToString()),
            new("MinimizeToTray", _settings.MinimizeToTray.ToString()),
            new("AutoReconnect", _settings.AutoReconnect.ToString()),
            new("HealthCheckMinutes", _settings.HealthCheckMinutes.ToString()),
            new("GameGuard", _settings.GameGuard.ToString()),
            new("GameGuardAction", _settings.GameGuardAction.ToString()),
            new("GameGuardResume", _settings.GameGuardResume.ToString()),
            new("GameGuardState", $"polling={_guard.IsPolling}, paused={_gamePaused}, switched={_gameSwitched}, override={_gameOverride}, sheet={_guardSheet}"),
            new("Language", (_settings.Language ?? "(auto) ") + Loc.Instance.Code),
            new("FirstRunCompleted", _settings.FirstRunCompleted.ToString()),
            new("HasCustomProfile", (_settings.CustomProfile is not null).ToString()),
            new("TestSites", (_settings.TestSites is null ? "(default) " : "") + string.Join(", ", ProbeTargets.BlockedUrls.Select(ProbeTargets.HostOf))),
        };
        var dohText = _selectedDoh.IsCustom
            ? DohProviders.Custom is { } c ? $"custom ({new Uri(c.Url).Host}, bootstrap {(string.IsNullOrEmpty(c.BootstrapIp) ? "system DNS" : "set")})" : "custom (not saved → Cloudflare)"
            : _selectedDoh.IsFast
                ? $"fast DNS, UDP redirect to {FastDns.Resolve(_settings.FastDnsServer, System.Net.Sockets.AddressFamily.InterNetwork)} / " +
                  $"{FastDns.Resolve(_settings.FastDnsServerV6, System.Net.Sockets.AddressFamily.InterNetworkV6)} (proxy mode: DoH Cloudflare)"
                : _selectedDoh.Key;
        var snap = new DiagnosticSnapshot(
            AppVersion, $"{Mode} ({ModeName(Mode)})", _state.ToString(), EffectiveProfile, dohText, settings,
            _lastHealthSummary, _lastAutoFindSummary, [.. _diagLog]);
        try
        {
            await Task.Run(() =>
            {
                var text = DiagnosticReport.Build(snap);
                File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            });
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogDiagSaved(System.IO.Path.GetFileName(path))));
            return true;
        }
        catch (Exception ex)
        {
            AddLog(new LogEntry(DateTime.Now, "Error", Strings.LogDiagFailed(ex.Message)));
            return false;
        }
    }
}
