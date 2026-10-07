using System.Net.NetworkInformation;
using System.Windows.Threading;
using LiDPI.Core;
using LiDPI.UI.Services;

namespace LiDPI.UI.ViewModels;

public enum HealthStatusKind { None, Ok, Blocked, Offline }

/// <summary>One choice of the health-check interval selector. The label follows the UI language.</summary>
public sealed record HealthOption(int Minutes, string LabelKey)
{
    public string Label => LiDPI.Localization.Loc.T(LabelKey);
    public override string ToString() => Label;
}

/// <summary>
/// v1.1 reliability features: first-run prompt state, auto-reconnect on network change / resume and the periodic
/// health check. UI thread only, like the rest of the view model. (There is no automatic update check: GitHub is
/// only contacted when the user presses "Check for updates".)
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FirstHealthDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectQuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>The user wants protection on (connect / toggle-on), regardless of the engine's current state.</summary>
    private bool _wantRunning;
    private NetworkWatcher? _netWatcher;
    private DispatcherTimer? _reconnectRetry;
    private NetworkChangeReason _retryReason;
    private DateTime _lastReconnectUtc = DateTime.MinValue;
    private bool _servicesStarted;

    // ===================== hooks (replaceable for UI tests) =====================

    /// <summary>Runs one reachability check. Called on the thread pool; must not touch the UI.</summary>
    public Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<IReadOnlyList<HealthSample>>> HealthRunner { get; set; } = HealthCheck.RunAsync;

    /// <summary>Tray notification (title, text).</summary>
    public event Action<string, string>? BalloonRequested;

    /// <summary>Starts the network watcher. Call once at startup.</summary>
    public void StartBackgroundServices()
    {
        if (_servicesStarted) return;
        _servicesStarted = true;

        _netWatcher = new NetworkWatcher(_dispatcher);
        _netWatcher.Changed += OnNetworkChanged;
    }

    /// <summary>App exit: unhook system events, stop timers and any in-flight health request.</summary>
    public void StopBackgroundServices()
    {
        if (_netWatcher is { } w)
        {
            w.Changed -= OnNetworkChanged;
            w.Dispose();
        }
        _netWatcher = null;
        _reconnectRetry?.Stop();
        _healthTimer?.Stop();
        try { _healthCts?.Cancel(); } catch (ObjectDisposedException) { /* finished */ }
    }

    // ===================== first run =====================

    /// <summary>True until the user answered the first-run auto-find prompt (brand-new installs only).</summary>
    public bool IsFirstRun => !_settings.FirstRunCompleted;

    public void CompleteFirstRun()
    {
        if (_settings.FirstRunCompleted) return;
        _settings.FirstRunCompleted = true;
        _settings.Save();
        OnPropertyChanged(nameof(IsFirstRun));
    }

    // ===================== settings =====================

    public bool AutoReconnect
    {
        get => _settings.AutoReconnect;
        set
        {
            if (_settings.AutoReconnect == value) return;
            _settings.AutoReconnect = value;
            _settings.Save();
            if (!value) _reconnectRetry?.Stop();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<HealthOption> HealthOptions { get; } =
        [new(0, nameof(Strings.HealthOff)), new(5, nameof(Strings.Health5)), new(15, nameof(Strings.Health15))];

    public HealthOption SelectedHealthOption
    {
        get => HealthOptions.FirstOrDefault(o => o.Minutes == _settings.HealthCheckMinutes) ?? HealthOptions[1];
        set
        {
            if (value is null || value.Minutes == _settings.HealthCheckMinutes) return;
            _settings.HealthCheckMinutes = value.Minutes;
            _settings.Save();
            OnPropertyChanged();
            if (value.Minutes == 0) { ClearHealth(); CancelHealthCheck(); }
            RestartHealthTimer(first: _lastHealthUtc is null);
        }
    }

    // ===================== state hooks (called from the main file) =====================

    private void OnEngineStateForReliability(EngineState s)
    {
        if (s == EngineState.Starting)
        {
            ClearHealth();
            CancelHealthCheck();
        }
        if (s == EngineState.Running) RestartHealthTimer(first: true);
        else if (s != EngineState.Stopping) StopHealthTimer();
        if (s != EngineState.Running) ShowHealthWarning = false;
        OnPropertyChanged(nameof(ShowHealthStatus));
    }

    // ===================== auto-reconnect =====================

    private void OnNetworkChanged(NetworkChangeReason reason)
    {
        if (!AutoReconnect || !_wantRunning || _autoFindStage != AutoFindStage.Idle) return;
        if (IsBusy)
        {
            ScheduleReconnectRetry(reason, NetworkWatcher.NetworkDebounce);
            return;
        }
        // our own restart can produce a burst of change events: ignore those (not for a resume)
        if (reason == NetworkChangeReason.Network && DateTime.UtcNow - _lastReconnectUtc < ReconnectQuietPeriod) return;
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogReconnectNoNetwork));
            return;   // the "available again" event brings us back here
        }
        _ = ReconnectAsync(reason == NetworkChangeReason.Resume ? Strings.ReasonResume : Strings.ReasonNetwork);
    }

    private void ScheduleReconnectRetry(NetworkChangeReason reason, TimeSpan delay)
    {
        if (_reconnectRetry is null)
        {
            _reconnectRetry = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _reconnectRetry.Tick += (_, _) =>
            {
                _reconnectRetry.Stop();
                OnNetworkChanged(_retryReason);
            };
        }
        if (!_reconnectRetry.IsEnabled || reason == NetworkChangeReason.Resume) _retryReason = reason;
        _reconnectRetry.Interval = delay;   // (re)starts the timer
        _reconnectRetry.Start();
    }

    /// <summary>Restart the engine with the current settings, then flush the system DNS cache.</summary>
    private async Task ReconnectAsync(string reason)
    {
        _lastReconnectUtc = DateTime.UtcNow;
        _healthBlockedStreak = 0;
        AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogReconnecting(reason)));
        await ConnectAsync();
        _lastReconnectUtc = DateTime.UtcNow;
        if (await Task.Run(LiDPI.Dns.DnsCache.Flush))
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogDnsFlushed));
    }

    // ===================== health check =====================

    private DispatcherTimer? _healthTimer;
    private CancellationTokenSource? _healthCts;
    private Task _healthRun = Task.CompletedTask;
    private int _healthEpoch;
    private int _healthBlockedStreak;
    private bool _healthBalloonShown;
    private DateTime? _lastHealthUtc;
    private HealthStatusKind _healthStatus = HealthStatusKind.None;
    private string _healthStatusText = "";
    private bool _showHealthWarning;

    public HealthStatusKind HealthStatus
    {
        get => _healthStatus;
        private set { if (Set(ref _healthStatus, value)) { OnPropertyChanged(nameof(ShowHealthStatus)); OnPropertyChanged(nameof(HealthToolTip)); } }
    }

    /// <summary>"Son kontrol: 2 dk önce" (the view adds the ✓ / ! mark).</summary>
    public string HealthStatusText { get => _healthStatusText; private set => Set(ref _healthStatusText, value); }

    public bool ShowHealthStatus => IsRunning && _healthStatus != HealthStatusKind.None && _settings.HealthCheckMinutes > 0;

    public string HealthToolTip => _healthStatus switch
    {
        HealthStatusKind.Ok => Strings.HealthToolTipOk,
        HealthStatusKind.Blocked => Strings.HealthToolTipBlocked,
        HealthStatusKind.Offline => Strings.HealthToolTipOffline,
        _ => "",
    };

    /// <summary>Soft warning card: blocked sites failed twice in a row while the control site worked.</summary>
    public bool ShowHealthWarning { get => _showHealthWarning; private set => Set(ref _showHealthWarning, value); }

    private void RestartHealthTimer(bool first)
    {
        if (_state != EngineState.Running || _settings.HealthCheckMinutes <= 0 || _autoFindStage != AutoFindStage.Idle)
        {
            StopHealthTimer();
            return;
        }
        if (_healthTimer is null)
        {
            _healthTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _healthTimer.Tick += (_, _) =>
            {
                _healthTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.HealthCheckMinutes));
                _ = RunHealthCheckAsync();
            };
        }
        _healthTimer.Interval = first ? FirstHealthDelay : TimeSpan.FromMinutes(_settings.HealthCheckMinutes);
        _healthTimer.Start();
    }

    private void StopHealthTimer() => _healthTimer?.Stop();

    /// <summary>One reachability check of the auto-find targets through the running engine. Never during auto-find.</summary>
    public async Task RunHealthCheckAsync()
    {
        if (_state != EngineState.Running || _autoFindStage != AutoFindStage.Idle || _settings.HealthCheckMinutes <= 0) return;
        if (!_healthRun.IsCompleted) return;

        var epoch = _healthEpoch;
        var cts = new CancellationTokenSource();
        _healthCts = cts;
        var urls = ProbeTargets.Urls;
        var runner = HealthRunner;
        var run = Task.Run(() => runner(urls, HealthTimeout, cts.Token), cts.Token);
        _healthRun = run;
        IReadOnlyList<HealthSample> samples;
        try { samples = await run; }
        catch { return; }   // cancelled (engine restart / auto-find) or failed: no verdict
        finally
        {
            if (ReferenceEquals(_healthCts, cts)) _healthCts = null;
            cts.Dispose();
        }
        // stale: the engine restarted, stopped or an auto-find began while we were waiting
        if (epoch != _healthEpoch || _state != EngineState.Running || _autoFindStage != AutoFindStage.Idle) return;
        ApplyHealth(samples);
    }

    private void ApplyHealth(IReadOnlyList<HealthSample> samples)
    {
        var blocked = samples.Where(s => !ProbeTargets.IsControl(s.Url)).ToList();
        var blockedOk = blocked.Count(s => s.Ok);
        var controlOk = samples.Any(s => s.Ok && ProbeTargets.IsControl(s.Url));
        _lastHealthUtc = DateTime.UtcNow;
        RememberHealth(samples, blockedOk, blocked.Count, controlOk);

        if (blockedOk > 0 || blocked.Count == 0)
        {
            _healthBlockedStreak = 0;
            HealthStatus = HealthStatusKind.Ok;
            ShowHealthWarning = false;
            AddLog(new LogEntry(DateTime.Now, "Debug", Strings.LogHealthOk(blockedOk, blocked.Count)));
        }
        else if (controlOk)
        {
            _healthBlockedStreak++;
            HealthStatus = HealthStatusKind.Blocked;
            AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogHealthBlocked));
            if (_healthBlockedStreak >= 2 && !ShowHealthWarning)
            {
                ShowHealthWarning = true;
                if (!_healthBalloonShown)
                {
                    _healthBalloonShown = true;
                    BalloonRequested?.Invoke(Strings.HealthBalloonTitle, Strings.HealthBalloonText);
                }
            }
        }
        else
        {
            // even the control site failed: the line is down, not blocked — no warning
            _healthBlockedStreak = 0;
            HealthStatus = HealthStatusKind.Offline;
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogHealthOffline));
        }
        UpdateHealthLabel();
    }

    /// <summary>Refreshes the relative time in the status label (cheap; called from the live timer).</summary>
    private void UpdateHealthLabel()
    {
        if (_lastHealthUtc is not { } t) { HealthStatusText = ""; return; }
        var age = DateTime.UtcNow - t;
        var rel = age.TotalMinutes < 1 ? Strings.HealthJustNow
                : age.TotalHours < 1 ? Strings.HealthMinutesAgo((int)age.TotalMinutes)
                : Strings.HealthHoursAgo((int)age.TotalHours);
        HealthStatusText = Strings.HealthLastCheck(rel);
    }

    private void ClearHealth()
    {
        _healthEpoch++;
        _healthBlockedStreak = 0;
        _lastHealthUtc = null;
        HealthStatus = HealthStatusKind.None;
        HealthStatusText = "";
    }

    private void CancelHealthCheck()
    {
        _healthEpoch++;
        try { _healthCts?.Cancel(); } catch (ObjectDisposedException) { /* finished */ }
    }

    /// <summary>Auto-find start: no health request may overlap the probe. Cancels and waits (bounded).</summary>
    private async Task StopHealthForAutoFindAsync()
    {
        StopHealthTimer();
        ShowHealthWarning = false;
        CancelHealthCheck();
        var run = _healthRun;
        if (!run.IsCompleted)
            await Task.WhenAny(run, Task.Delay(HealthTimeout + TimeSpan.FromSeconds(1)));
    }

    /// <summary>Warning card: "Otomatik bul".</summary>
    public void HealthWarningAutoFind()
    {
        ShowHealthWarning = false;
        _healthBlockedStreak = 0;
        _ = StartAutoFindAsync();
    }

    /// <summary>Warning card: "Yeniden bağlan".</summary>
    public void HealthWarningReconnect()
    {
        ShowHealthWarning = false;
        _ = ReconnectAsync(Strings.HealthWarnReconnect);
    }

    public void DismissHealthWarning()
    {
        ShowHealthWarning = false;
        _healthBlockedStreak = 0;
    }
}
