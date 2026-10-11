using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LiDPI.Core;
using LiDPI.Localization;
using LiDPI.UI.Services;

namespace LiDPI.UI.ViewModels;

/// <summary>State of the whole UI. All members must be used on the UI thread.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 200;

    private readonly EngineController _controller;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _logFlush;
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private int _logFlushScheduled;
    private Task _engineChain = Task.CompletedTask;

    private EngineState _state = EngineState.Stopped;
    private BypassProfile _selectedProfile;
    private DohOption _selectedDoh;
    private DateTime? _runningSinceUtc;
    private string? _lastError;
    private LocText _errorMessage;
    private string _uptimeText = "00:00:00";
    private long _packets, _connections, _dnsQueries;
    private bool _isViewActive;
    private bool _startWithWindows;
    private bool _autostartBusy;

    public MainViewModel(EngineController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Profiles = new ObservableCollection<BypassProfile>(BuiltInProfiles.All);
        if (settings.CustomProfile is { } custom)
        {
            // normalise whatever was persisted to the fixed id/name the UI keys on
            custom = custom with { Id = AutoFind.CustomProfileId, Name = "", Description = "" };
            settings.CustomProfile = custom;
            Profiles.Add(custom);
        }
        _selectedProfile = Profiles.FirstOrDefault(p => p.Id == settings.ProfileId) ?? BuiltInProfiles.Standard;
        DohProviderList = [.. DohProviders.All.Select(p => new DohOption(p.Name)), new DohOption(FastDns.ChipKey), new DohOption(DohProviders.CustomKey)];
        _selectedDoh = DohProviderList.FirstOrDefault(o => string.Equals(o.Key, settings.DohProviderName, StringComparison.OrdinalIgnoreCase))
                       ?? DohProviderList[0];
        InitFeatures();
        InitLanguage();
        InitGuard();
        _startWithWindows = settings.StartWithWindows;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshLive();

        // Controller events can arrive on any thread: marshal (never block) to the UI thread.
        _controller.StateChanged += (_, s) => _dispatcher.InvokeAsync(() => ApplyState(s));
        // Log lines can arrive in bursts (per-connection Debug lines): queue them off-thread and
        // append in one batch every ~200 ms instead of one dispatcher round-trip + layout per line.
        _logFlush = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _logFlush.Tick += (_, _) => FlushLogs();
        _controller.Log += (_, e) => EnqueueLog(e);

        ToggleCommand = new RelayCommand(() => _ = ToggleAsync(), () => _state != EngineState.Stopping);
        RetryCommand = new RelayCommand(() => _ = ConnectAsync());
        CopyLogsCommand = new RelayCommand(CopyLogs, () => Logs.Count > 0);
        ClearLogsCommand = new RelayCommand(() => Logs.Clear(), () => Logs.Count > 0);
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));

        ProbeRunner = (variants, progress, ct) => Task.Run(() => ProfileProbe.RunAsync(
            variants, ProbeTargets.Urls, progress, diagnostics: null, rounds: AutoFind.Rounds, ct: ct, packetTrace: false), ct);
    }

    // ===== Commands =====
    public ICommand ToggleCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CopyLogsCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand OpenUrlCommand { get; }

    // ===== Engine state =====
    public EngineState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(CanSwitchMode));
            OnPropertyChanged(nameof(CanAutoFind));
            OnPropertyChanged(nameof(StatusText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => _state == EngineState.Running;
    public bool IsBusy => _state is EngineState.Starting or EngineState.Stopping;
    public bool IsError => _state == EngineState.Error;
    /// <summary>Running or about to run (what the tray toggle reflects).</summary>
    public bool IsActive => _state is EngineState.Running or EngineState.Starting;
    public bool CanSwitchMode => !IsBusy && !IsAutoFinding;

    public string StatusText => _state switch
    {
        EngineState.Starting => Strings.StatusConnecting,
        EngineState.Stopping => Strings.StatusDisconnecting,
        EngineState.Running => Strings.StatusRunning,
        EngineState.Error => Strings.StatusError,
        _ => Strings.StatusStopped,
    };

    public string Subtitle => $"{_selectedProfile.DisplayName} · {ModeName(Mode)}";

    public string ErrorMessage => _errorMessage.ToString();

    private void SetErrorMessage(LocText text)
    {
        _errorMessage = text;
        OnPropertyChanged(nameof(ErrorMessage));
    }
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }
    public long Packets { get => _packets; private set => Set(ref _packets, value); }
    public long Connections { get => _connections; private set => Set(ref _connections, value); }
    public long DnsQueries { get => _dnsQueries; private set => Set(ref _dnsQueries, value); }

    public ObservableCollection<LogEntry> Logs { get; } = new();

    // ===== Mode / profile =====
    public EngineMode Mode => _settings.Mode;

    public bool IsProxyMode
    {
        get => _settings.Mode == EngineMode.Proxy;
        set => SetMode(value ? EngineMode.Proxy : EngineMode.Divert);
    }

    public bool IsDivertMode
    {
        get => _settings.Mode == EngineMode.Divert;
        set { if (value) SetMode(EngineMode.Divert); }
    }

    /// <summary>Built-in profiles, plus the auto-found custom profile (last) once a probe produced one.</summary>
    public ObservableCollection<BypassProfile> Profiles { get; }

    public BypassProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || !Set(ref _selectedProfile, value)) return;
            _settings.ProfileId = value.Id;
            _settings.Save();
            OnPropertyChanged(nameof(Subtitle));
            RestartIfRunning();
        }
    }

    /// <summary>Built-in providers plus "Özel…" (last).</summary>
    public IReadOnlyList<DohOption> DohProviderList { get; }

    public DohOption SelectedDoh
    {
        get => _selectedDoh;
        set
        {
            if (value is null || !Set(ref _selectedDoh, value)) return;
            _settings.DohProviderName = value.Key;
            _settings.Save();
            OnPropertyChanged(nameof(IsCustomDohSelected));
            OnPropertyChanged(nameof(IsFastDnsSelected));
            OnCustomDohSelectionChanged();
            RestartIfRunning();
        }
    }

    // ===== Settings =====
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_autostartBusy || !Set(ref _startWithWindows, value)) return;
            _ = ApplyAutostartAsync(value);
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { if (_settings.StartMinimized == value) return; _settings.StartMinimized = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool AutoConnectOnLaunch
    {
        get => _settings.AutoConnectOnLaunch;
        set { if (_settings.AutoConnectOnLaunch == value) return; _settings.AutoConnectOnLaunch = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { if (_settings.MinimizeToTray == value) return; _settings.MinimizeToTray = value; _settings.Save(); OnPropertyChanged(); }
    }

    public string AppVersion { get; } =
        (typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];

    /// <summary>True while the window is visible and not minimized. Drives the live timer.</summary>
    public bool IsViewActive
    {
        get => _isViewActive;
        set
        {
            if (!Set(ref _isViewActive, value)) return;
            UpdateTimer();
            if (value) RefreshLive();
        }
    }

    // ===== Actions =====
    private BypassProfile EffectiveProfile => WithDnsChoice(_selectedProfile);

    /// <summary>True while the "Hızlı DNS" (UDP redirect) chip is selected.</summary>
    public bool IsFastDnsSelected => _selectedDoh.IsFast;

    /// <summary>
    /// Applies the DNS chip to a profile: a DoH provider, or "Hızlı DNS" (UDP redirect; DoH via Cloudflare where the
    /// engine cannot redirect, i.e. the proxy).
    /// </summary>
    private BypassProfile WithDnsChoice(BypassProfile p)
    {
        // a profile that keeps the system resolver stays so (like it ignores the DoH provider chip)
        if (_selectedDoh.IsFast && p.Dns != DnsMode.System)
            return p with
            {
                Dns = DnsMode.UdpRedirect,
                DohProviderName = DohProviders.Cloudflare.Name,
                FastDnsServer = _settings.FastDnsServer,
                FastDnsServerV6 = _settings.FastDnsServerV6,
            };
        return p with
        {
            Dns = p.Dns == DnsMode.UdpRedirect ? DnsMode.DoH : p.Dns,
            DohProviderName = _selectedDoh.Key,
        };
    }

    // Engine start/stop can do synchronous work (driver load, proxy setup) before its first await:
    // run it on the thread pool, serialised in call order, so the UI thread never stalls mid-animation.
    // While the auto-find probe owns the packet driver, the controller's engine must stay off.
    // Every start passes the guard gate (other DPI tools, games with kernel anti-cheats) before the engine is touched.
    public Task ToggleAsync()
    {
        if (IsAutoFinding) return Task.CompletedTask;
        return IsActive ? DisconnectAsync() : ConnectAsync(StartOrigin.User);
    }

    public async Task ConnectAsync(StartOrigin origin = StartOrigin.User)
    {
        if (IsAutoFinding) return;
        var serial = ++_intentSerial;   // a later connect/disconnect wins over this one while the gate looks around
        if (!await GuardGateAsync(origin, autoFind: false, serial)) return;
        if (serial != _intentSerial || IsAutoFinding) return;
        _wantRunning = true;
        _gamePaused = false;
        var (m, p) = (Mode, EffectiveProfile);
        await RunEngine(() => _controller.StartAsync(m, p));
    }

    public Task DisconnectAsync()
    {
        _intentSerial++;
        _wantRunning = false;
        // the user turned it off: no automatic restart after a game, and "start anyway" is asked again next time
        _gamePaused = false;
        _gameOverride = false;
        CancelGameResume();
        return RunEngine(_controller.StopAsync);
    }

    private Task RunEngine(Func<Task> op)
    {
        var t = _engineChain.ContinueWith(_ => op(), CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        _engineChain = t;
        return t;
    }

    /// <summary>User's choice of mode: it also ends a temporary switch made by game protection.</summary>
    private void SetMode(EngineMode mode)
    {
        _gameSwitched = false;
        SetModeCore(mode, restart: true);
    }

    private void SetModeCore(EngineMode mode, bool restart)
    {
        if (_settings.Mode == mode) return;
        _settings.Mode = mode;
        _settings.Save();
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsProxyMode));
        OnPropertyChanged(nameof(IsDivertMode));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CanAutoFind));
        OnPropertyChanged(nameof(AutoFindToolTip));
        if (restart) RestartIfRunning();
    }

    private void RestartIfRunning()
    {
        // StartAsync stops the current engine first (serialised inside the controller).
        if (_state == EngineState.Running)
            _ = ConnectAsync();
    }

    /// <summary>Reads the real Task Scheduler state into the toggle (background, best effort).</summary>
    public async Task SyncAutostartAsync()
    {
        var exists = await AutostartService.IsEnabledAsync();
        // Re-register idempotently so the task follows a moved/renamed exe and current task settings.
        if (exists && _startWithWindows) await AutostartService.SetEnabledAsync(true);
        if (exists == _startWithWindows) return;
        _startWithWindows = exists;
        OnPropertyChanged(nameof(StartWithWindows));
        _settings.StartWithWindows = exists;
        _settings.Save();
    }

    private async Task ApplyAutostartAsync(bool enable)
    {
        _autostartBusy = true;
        try
        {
            var ok = await AutostartService.SetEnabledAsync(enable);
            if (ok)
            {
                _settings.StartWithWindows = enable;
                _settings.Save();
                AddLog(new LogEntry(DateTime.Now, "Info", enable ? Strings.LogAutostartOn : Strings.LogAutostartOff));
            }
            else
            {
                _startWithWindows = !enable;
                OnPropertyChanged(nameof(StartWithWindows));
                AddLog(new LogEntry(DateTime.Now, "Error", Strings.LogAutostartFailed));
            }
        }
        finally { _autostartBusy = false; }
    }

    private void ApplyState(EngineState s)
    {
        FlushLogs(); // error text logged just before the state change must be visible to it
        if (s == EngineState.Starting)
        {
            _lastError = null;
            Packets = Connections = DnsQueries = 0;
            UptimeText = "00:00:00";
        }

        if (s == EngineState.Running)
            _runningSinceUtc = _controller.Stats?.StartedAt ?? DateTime.UtcNow;
        else if (s != EngineState.Stopping)
            _runningSinceUtc = null;

        if (s == EngineState.Error)
            SetErrorMessage(_lastError is { } err ? LocText.Raw(err) : LocText.Of(nameof(Strings.ErrorUnexpectedStop)));

        State = s;
        OnEngineStateForReliability(s);
        UpdateGuardWatch();
        UpdateTimer();
        RefreshLive();
    }

    private void EnqueueLog(LogEntry e)
    {
        _pendingLogs.Enqueue(e);
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            _dispatcher.InvokeAsync(() => { if (!_logFlush.IsEnabled) _logFlush.Start(); }, DispatcherPriority.Background);
    }

    private void FlushLogs()
    {
        _logFlush.Stop();
        Volatile.Write(ref _logFlushScheduled, 0);
        if (_pendingLogs.IsEmpty) return;

        var batch = new List<LogEntry>();
        while (_pendingLogs.TryDequeue(out var e)) batch.Add(e);
        // only the newest MaxLogLines can survive the cap anyway
        var start = Math.Max(0, batch.Count - MaxLogLines);
        for (var i = 0; i < start; i++)
        {
            RememberForDiagnostics(batch[i]);
            if (string.Equals(batch[i].Level, "Error", StringComparison.OrdinalIgnoreCase)) _lastError = batch[i].Message;
        }
        for (var i = start; i < batch.Count; i++) AddLog(batch[i]);
    }

    private void AddLog(LogEntry e)
    {
        RememberForDiagnostics(e);
        if (string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            _lastError = e.Message;
            if (_state == EngineState.Error) SetErrorMessage(LocText.Raw(e.Message));
        }
        Logs.Add(e);
        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
    }

    private void UpdateTimer()
    {
        var shouldRun = _isViewActive && _state == EngineState.Running;
        if (shouldRun && !_timer.IsEnabled) _timer.Start();
        else if (!shouldRun && _timer.IsEnabled) _timer.Stop();
    }

    private void RefreshLive()
    {
        if (_state != EngineState.Running || !_isViewActive) return;
        UpdateHealthLabel();

        var since = _controller.Stats?.StartedAt ?? _runningSinceUtc;
        if (since is { } t)
        {
            var span = DateTime.UtcNow - t;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            UptimeText = $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        }

        if (_controller.Stats is { } st)
        {
            Packets = st.PacketsProcessed;
            Connections = st.ConnectionsHandled;
            DnsQueries = st.DnsQueriesResolved;
        }
    }

    private void CopyLogs()
    {
        var text = string.Join(Environment.NewLine,
            Logs.Select(l => $"{l.Time:yyyy-MM-dd HH:mm:ss} [{l.Level}] {l.Message}"));
        for (var i = 0; i < 3; i++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked */ }
    }

    public static string ModeName(EngineMode m) => m == EngineMode.Proxy ? Strings.ModeBrowser : Strings.ModeAll;

    // ===================== auto-find best settings =====================

    private AutoFindStage _autoFindStage = AutoFindStage.Idle;
    private CancellationTokenSource? _autoFindCts;
    private Task? _autoFindRun;
    private bool _autoFindWasActive;
    private bool _autoFindCancelling;
    private bool _autoFindVariantActive;
    private double _autoFindProgress;
    private int _autoFindTotal;
    private LocText _autoFindCurrent;
    private string _autoFindCounter = "";
    private LocText _autoFindTitle;
    private LocText _autoFindMessage;
    private bool _autoFindSuggestProxy;
    private string _winnerScore = "", _winnerLatency = "";
    private BypassProfile? _winnerProfile;
    private bool _winnerIsCustom, _winnerPartial;

    /// <summary>
    /// Runs the probe (replaceable for UI tests). Must not touch the UI thread; progress is marshalled by the caller.
    /// </summary>
    public Func<IReadOnlyList<ProbeVariant>, IProgress<ProbeProgress>, CancellationToken, Task<IReadOnlyList<ProbeVariantResult>>> ProbeRunner { get; set; }

    public AutoFindStage AutoFindStage
    {
        get => _autoFindStage;
        private set
        {
            if (!Set(ref _autoFindStage, value)) return;
            OnPropertyChanged(nameof(IsAutoFinding));
            OnPropertyChanged(nameof(CanSwitchMode));
            OnPropertyChanged(nameof(CanAutoFind));
            CommandManager.InvalidateRequerySuggested();
            RestartHealthTimer(first: _lastHealthUtc is null);
            UpdateGuardWatch();
        }
    }

    public bool IsAutoFinding => _autoFindStage == AutoFindStage.Running;
    /// <summary>Divert mode only: the probe drives the WinDivert engine directly.</summary>
    public bool CanAutoFind => IsDivertMode && !IsBusy && _autoFindStage == AutoFindStage.Idle;
    public string AutoFindToolTip => IsDivertMode
        ? Strings.AutoFindToolTip
        : Strings.AutoFindToolTipNeedsAllApps(Strings.ModeAll);

    public bool IsAutoFindCancelling { get => _autoFindCancelling; private set => Set(ref _autoFindCancelling, value); }
    /// <summary>True between a variant's start and its result (the view creeps the bar forward meanwhile).</summary>
    public bool AutoFindVariantActive { get => _autoFindVariantActive; private set => Set(ref _autoFindVariantActive, value); }
    /// <summary>Completed fraction, 0..1.</summary>
    public double AutoFindProgress { get => _autoFindProgress; private set => Set(ref _autoFindProgress, value); }
    public int AutoFindTotal { get => _autoFindTotal; private set => Set(ref _autoFindTotal, value); }
    public string AutoFindCurrent => _autoFindCurrent.ToString();
    public string AutoFindCounter { get => _autoFindCounter; private set => Set(ref _autoFindCounter, value); }
    public string AutoFindTitle => _autoFindTitle.ToString();
    public string AutoFindMessage => _autoFindMessage.ToString();
    public bool AutoFindSuggestProxy { get => _autoFindSuggestProxy; private set => Set(ref _autoFindSuggestProxy, value); }
    public string WinnerName => _winnerProfile?.DisplayName ?? "";
    public string WinnerDescription => _winnerProfile?.DisplayDescription ?? "";
    public string WinnerScore { get => _winnerScore; private set => Set(ref _winnerScore, value); }
    public string WinnerLatency { get => _winnerLatency; private set => Set(ref _winnerLatency, value); }
    public string WinnerNote => _winnerProfile is null ? ""
        : (_winnerIsCustom ? Strings.AutoFindNoteCustom : Strings.AutoFindNoteBuiltIn) + (_winnerPartial ? " " + Strings.AutoFindNotePartial : "");
    public bool WinnerIsCustom => _winnerIsCustom;

    private void SetAutoFindCurrent(LocText text)
    {
        _autoFindCurrent = text;
        OnPropertyChanged(nameof(AutoFindCurrent));
    }

    /// <summary>One line per finished variant, in test order.</summary>
    public ObservableCollection<AutoFindRow> AutoFindRows { get; } = new();

    /// <summary>
    /// Stops the engine (remembering whether it was on), then tests the quick candidate set off the UI thread.
    /// Completes when the run ends (result shown, failed or cancelled).
    /// </summary>
    public async Task StartAutoFindAsync()
    {
        if (!CanAutoFind) return;
        // the probe loads the packet driver too: same checks as a Divert start (a sheet asks if something is in the way)
        var serial = ++_intentSerial;
        if (!await GuardGateAsync(StartOrigin.User, autoFind: true, serial) || serial != _intentSerial || !CanAutoFind) return;

        var variants = AutoFind.QuickCandidates()
            .Select(v => v with { Profile = WithDnsChoice(v.Profile!) })
            .ToList();

        _autoFindWasActive = IsActive;
        AutoFindRows.Clear();
        AutoFindTotal = variants.Count;
        AutoFindCounter = $"0 / {variants.Count}";
        SetAutoFindCurrent(LocText.Of(nameof(Strings.AutoFindPreparing)));
        AutoFindVariantActive = false;
        AutoFindProgress = 0;
        IsAutoFindCancelling = false;
        AutoFindStage = AutoFindStage.Running;

        using var cts = new CancellationTokenSource();
        _autoFindCts = cts;
        try
        {
            await StopHealthForAutoFindAsync();       // no health request may overlap the probe
            await RunEngine(_controller.StopAsync);   // the probe needs the driver to itself
            cts.Token.ThrowIfCancellationRequested();
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogAutoFindStarted(variants.Count)));

            var progress = new DispatcherProgress(_dispatcher, OnAutoFindProgress);
            var run = ProbeRunner(variants, progress, cts.Token);
            _autoFindRun = run;
            var ranked = await run;
            cts.Token.ThrowIfCancellationRequested();
            ShowAutoFindOutcome(AutoFind.Evaluate(ranked));
        }
        catch (OperationCanceledException)
        {
            // the probe stopped its own engine in a finally; drop answers poisoned while it ran
            await Task.Run(LiDPI.Dns.DnsCache.Flush);
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogAutoFindCancelled));
            EndAutoFind(reconnect: _autoFindWasActive);
        }
        catch (Exception ex)
        {
            AddLog(new LogEntry(DateTime.Now, "Error", Strings.LogAutoFindFailed(ex.Message)));
            ShowAutoFindFailure(nameof(Strings.AutoFindFailTitleError), LocText.Raw(ex.Message), suggestProxy: false);
        }
        finally
        {
            _autoFindCts = null;
            IsAutoFindCancelling = false;
        }
    }

    /// <summary>App exit: cancel a running probe and wait until it has stopped its engine.</summary>
    public Task StopAutoFindAsync()
    {
        _autoFindWasActive = false;   // never reconnect on the way out
        CancelAutoFind();
        return _autoFindRun is { IsCompleted: false } run
            ? run.ContinueWith(_ => { }, TaskScheduler.Default)
            : Task.CompletedTask;
    }

    public void CancelAutoFind()
    {
        if (!IsAutoFinding || _autoFindCts is not { } cts) return;
        IsAutoFindCancelling = true;
        SetAutoFindCurrent(LocText.Of(nameof(Strings.AutoFindCancelling)));
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>"Kapat" on the result: back to whatever was running before the test (with the new profile, if any).</summary>
    public void DismissAutoFind()
    {
        if (_autoFindStage is AutoFindStage.Found or AutoFindStage.Failed) EndAutoFind(reconnect: _autoFindWasActive);
    }

    /// <summary>"Uygula ve Bağlan": the winner is already selected; connect with it.</summary>
    public void ApplyAutoFind()
    {
        if (_autoFindStage != AutoFindStage.Found) return;
        EndAutoFind(reconnect: true);
    }

    /// <summary>Failure suggestion: switch to the browser-only proxy mode.</summary>
    public void AutoFindSwitchToProxy()
    {
        if (_autoFindStage != AutoFindStage.Failed) return;
        EndAutoFind(reconnect: false);
        SetMode(EngineMode.Proxy);
        if (_autoFindWasActive) _ = ConnectAsync();
    }

    private void EndAutoFind(bool reconnect)
    {
        AutoFindStage = AutoFindStage.Idle;
        if (reconnect) _ = ConnectAsync();
    }

    private void OnAutoFindProgress(ProbeProgress p)
    {
        if (!IsAutoFinding || IsAutoFindCancelling) return;
        if (p.Completed is not { } r)
        {
            SetAutoFindCurrent(LocText.Raw(p.VariantName));
            AutoFindCounter = $"{p.Index + 1} / {p.Total}";
            AutoFindVariantActive = true;          // set before the progress so the view sees it
            AutoFindProgress = (double)p.Index / p.Total;
            return;
        }

        AutoFindRows.Add(AutoFindRow.From(r));
        AutoFindVariantActive = false;
        AutoFindProgress = (double)(p.Index + 1) / p.Total;
    }

    private void ShowAutoFindOutcome(AutoFindOutcome o)
    {
        AutoFindProgress = 1;
        RememberAutoFind(o);
        switch (o.Verdict)
        {
            case AutoFindVerdict.Found:
                var w = o.Winner!;
                var profile = o.Profile!;
                if (!o.IsBuiltIn) profile = SaveCustomProfile(profile);
                SelectProfileForce(profile);
                _winnerProfile = profile;
                _winnerIsCustom = !o.IsBuiltIn;
                _winnerPartial = AutoFind.BlockedOk(w) < w.Targets.Count(t => !ProbeTargets.IsControl(t.Url));
                WinnerScore = $"{w.OkCount}/{w.Targets.Count}";
                WinnerLatency = string.Format(Loc.Instance.Culture, "{0:0} ms", w.AvgLatencyMs);
                OnPropertyChanged(nameof(WinnerName));
                OnPropertyChanged(nameof(WinnerDescription));
                OnPropertyChanged(nameof(WinnerIsCustom));
                OnPropertyChanged(nameof(WinnerNote));
                AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogAutoFindPicked(w.Variant.Name, w.OkCount, w.Targets.Count, w.AvgLatencyMs)));
                AutoFindStage = AutoFindStage.Found;
                break;

            case AutoFindVerdict.NoInternet:
                ShowAutoFindFailure(nameof(Strings.AutoFindFailTitleNoInternet),
                    LocText.Of(nameof(Strings.AutoFindFailTextNoInternet)), suggestProxy: false);
                break;

            case AutoFindVerdict.EngineFailed:
                ShowAutoFindFailure(nameof(Strings.AutoFindFailTitleEngine),
                    o.Detail is { } detail ? LocText.Raw(detail) : LocText.Of(nameof(Strings.AutoFindFailTextEngine)),
                    suggestProxy: true);
                break;

            default:
                ShowAutoFindFailure(nameof(Strings.AutoFindFailTitleNothing),
                    LocText.Of(nameof(Strings.AutoFindFailTextNothing)), suggestProxy: true);
                break;
        }
    }

    private void ShowAutoFindFailure(string titleKey, LocText message, bool suggestProxy)
    {
        _autoFindTitle = LocText.Of(titleKey);
        _autoFindMessage = message;
        OnPropertyChanged(nameof(AutoFindTitle));
        OnPropertyChanged(nameof(AutoFindMessage));
        AutoFindSuggestProxy = suggestProxy;
        AutoFindStage = AutoFindStage.Failed;
    }

    /// <summary>Persists the auto-found profile and puts it (last) into the profile list.</summary>
    private BypassProfile SaveCustomProfile(BypassProfile p)
    {
        p = p with { Id = AutoFind.CustomProfileId, Name = "", Description = "" };
        _settings.CustomProfile = p;
        var idx = -1;
        for (var i = 0; i < Profiles.Count; i++)
            if (Profiles[i].Id == AutoFind.CustomProfileId) { idx = i; break; }
        if (idx >= 0) Profiles[idx] = p;
        else Profiles.Add(p);
        return p;
    }

    /// <summary>Selects <paramref name="p"/> even if an equal record is already selected (the list item may have been replaced).</summary>
    private void SelectProfileForce(BypassProfile p)
    {
        _selectedProfile = p;
        _settings.ProfileId = p.Id;
        _settings.Save();
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>IProgress that posts to the UI dispatcher without blocking the reporting (probe) thread.</summary>
    private sealed class DispatcherProgress(Dispatcher dispatcher, Action<ProbeProgress> handler) : IProgress<ProbeProgress>
    {
        public void Report(ProbeProgress value) => dispatcher.InvokeAsync(() => handler(value));
    }
}

public enum AutoFindStage { Idle, Running, Found, Failed }

/// <summary>One DoH provider chip. <see cref="Key"/> is what gets persisted; <see cref="Name"/> is shown (live, see LiveTextConverter).</summary>
public sealed record DohOption(string Key)
{
    public bool IsCustom => Key == DohProviders.CustomKey;
    /// <summary>The "Hızlı DNS" (UDP redirect) chip rather than a DoH provider.</summary>
    public bool IsFast => Key == FastDns.ChipKey;
    public string Name => IsCustom ? Strings.DohCustomChip : IsFast ? Strings.DnsFastChip : Key;
    public override string ToString() => Name;
}

/// <summary>One finished variant in the live results list. <see cref="Level"/>: 2 all blocked sites, 1 some, 0 none.</summary>
public sealed record AutoFindRow(string Name, string Score, string Latency, int Level)
{
    public static AutoFindRow From(ProbeVariantResult r)
    {
        var blockedTotal = r.Targets.Count(t => !ProbeTargets.IsControl(t.Url));
        var blockedOk = AutoFind.BlockedOk(r);
        var level = r.EngineError is not null || blockedOk == 0 ? 0 : blockedOk == blockedTotal ? 2 : 1;
        var latency = r.EngineError is not null && r.Targets.Count == 0 ? Strings.AutoFindRowError
                    : double.IsInfinity(r.AvgLatencyMs) ? "—" : string.Format(Loc.Instance.Culture, "{0:0} ms", r.AvgLatencyMs);
        return new AutoFindRow(r.Variant.Name, $"{r.OkCount}/{r.Targets.Count}", latency, level);
    }
}
