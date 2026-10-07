using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LiDPI.Core;
using LiDPI.UI.Services;

namespace LiDPI.UI.ViewModels;

/// <summary>State of the whole UI. All members must be used on the UI thread.</summary>
public sealed class MainViewModel : ObservableObject
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
    private DohProvider _selectedDoh;
    private DateTime? _runningSinceUtc;
    private string? _lastError;
    private string _errorMessage = "";
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

        _selectedProfile = BuiltInProfiles.All.FirstOrDefault(p => p.Id == settings.ProfileId) ?? BuiltInProfiles.Standard;
        _selectedDoh = DohProviders.All.FirstOrDefault(p => p.Name == settings.DohProviderName) ?? DohProviders.Cloudflare;
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
            OnPropertyChanged(nameof(StatusText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => _state == EngineState.Running;
    public bool IsBusy => _state is EngineState.Starting or EngineState.Stopping;
    public bool IsError => _state == EngineState.Error;
    /// <summary>Running or about to run (what the tray toggle reflects).</summary>
    public bool IsActive => _state is EngineState.Running or EngineState.Starting;
    public bool CanSwitchMode => !IsBusy;

    public string StatusText => _state switch
    {
        EngineState.Starting => "Bağlanıyor…",
        EngineState.Stopping => "Bağlantı kesiliyor…",
        EngineState.Running => "Koruma Aktif",
        EngineState.Error => "Hata",
        _ => "Bağlı Değil",
    };

    public string Subtitle => $"{_selectedProfile.Name} · {ModeName(Mode)}";

    public string ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
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

    public IReadOnlyList<BypassProfile> Profiles => BuiltInProfiles.All;

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

    public IReadOnlyList<DohProvider> DohProviderList => DohProviders.All;

    public DohProvider SelectedDoh
    {
        get => _selectedDoh;
        set
        {
            if (value is null || !Set(ref _selectedDoh, value)) return;
            _settings.DohProviderName = value.Name;
            _settings.Save();
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
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0")
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
    private BypassProfile EffectiveProfile => _selectedProfile with { DohProviderName = _selectedDoh.Name };

    // Engine start/stop can do synchronous work (driver load, proxy setup) before its first await:
    // run it on the thread pool, serialised in call order, so the UI thread never stalls mid-animation.
    public Task ToggleAsync() { var (m, p) = (Mode, EffectiveProfile); return RunEngine(() => _controller.ToggleAsync(m, p)); }
    public Task ConnectAsync() { var (m, p) = (Mode, EffectiveProfile); return RunEngine(() => _controller.StartAsync(m, p)); }
    public Task DisconnectAsync() => RunEngine(_controller.StopAsync);

    private Task RunEngine(Func<Task> op)
    {
        var t = _engineChain.ContinueWith(_ => op(), CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        _engineChain = t;
        return t;
    }

    private void SetMode(EngineMode mode)
    {
        if (_settings.Mode == mode) return;
        _settings.Mode = mode;
        _settings.Save();
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsProxyMode));
        OnPropertyChanged(nameof(IsDivertMode));
        OnPropertyChanged(nameof(Subtitle));
        RestartIfRunning();
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
                AddLog(new LogEntry(DateTime.Now, "Info", enable ? "Windows ile başlatma etkinleştirildi." : "Windows ile başlatma kapatıldı."));
            }
            else
            {
                _startWithWindows = !enable;
                OnPropertyChanged(nameof(StartWithWindows));
                AddLog(new LogEntry(DateTime.Now, "Error", "Görev Zamanlayıcı kaydı değiştirilemedi."));
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
            ErrorMessage = _lastError ?? "Motor beklenmedik şekilde durdu. Ayrıntılar için kayıtlara bakın.";

        State = s;
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
            if (string.Equals(batch[i].Level, "Error", StringComparison.OrdinalIgnoreCase)) _lastError = batch[i].Message;
        for (var i = start; i < batch.Count; i++) AddLog(batch[i]);
    }

    private void AddLog(LogEntry e)
    {
        if (string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            _lastError = e.Message;
            if (_state == EngineState.Error) ErrorMessage = e.Message;
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

    public static string ModeName(EngineMode m) => m == EngineMode.Proxy ? "Sadece Tarayıcı" : "Tüm Uygulamalar";
}
