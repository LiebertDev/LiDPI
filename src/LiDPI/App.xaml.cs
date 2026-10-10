using System.Windows;
using LiDPI.Core;
using LiDPI.Localization;
using LiDPI.UI.Services;
using LiDPI.UI.ViewModels;

namespace LiDPI;

/// <summary>
/// Application entry point: single instance, engine controller, tray, window lifetime.
/// Guarantees the engine is stopped before the process exits.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(6);

    private SingleInstance? _instance;
    private EngineController? _controller;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayService? _tray;
    private int _exiting;

    /// <summary>True once an exit sequence has started (window close must not be cancelled any more).</summary>
    public static bool IsExiting => Current is App app && Volatile.Read(ref app._exiting) != 0;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hidden diagnostics mode: LiDPI.exe --probe "<report file>" (no window, tray or single-instance lock).
        int probeIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase));
        if (probeIdx >= 0)
        {
            string outFile = probeIdx + 1 < e.Args.Length
                ? e.Args[probeIdx + 1]
                : System.IO.Path.Combine(AppContext.BaseDirectory, "probe.txt");
            _ = RunProbeAsync(outFile);
            return;
        }

        _instance = SingleInstance.TryAcquire(() => Dispatcher.InvokeAsync(ShowMainWindow));
        if (_instance is null)
        {
            // another instance is running and has been asked to show itself
            Shutdown();
            return;
        }

        // No global frame-rate cap: short transitions run at the display's refresh rate (a 60 fps cap looks
        // stuttery on 120-240 Hz panels). Only the endless idle loops set DesiredFrameRate = 60 themselves
        // (PowerButton breath/spin, auto-find shimmer, Spinner style) to keep idle GPU use low.

        DispatcherUnhandledException += (_, args) =>
        {
            AddLog("Error", LiDPI.UI.Strings.LogUnexpectedError(args.Exception.Message));
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => StopEngineBlocking();

        // Restore the system proxy if a previous run died while in proxy mode.
        try { LiDPI.Engines.Proxy.ProxyEngine.RestoreIfDirty(); } catch { /* best effort */ }

        var settings = AppSettings.Load();
        // before anything builds text: saved choice, else the Windows display language (tr → Turkish, others → English)
        Loc.Instance.SetLanguage(settings.Language);
        _controller = new EngineController(EngineFactory.Create); // ENGINE-WIRE (UI testing: m => new NullEngine(m))
        _vm = new MainViewModel(_controller, settings);
        _tray = new TrayService(_vm, ShowMainWindow, () => _ = ExitAsync());

        var startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase))
                          || settings.StartMinimized;
        // Started in the tray: the window is not even built until it is first shown.
        if (!startInTray) EnsureWindow().Show();
        else MemoryTrimmer.TrimSoon(TimeSpan.FromSeconds(15));   // drop start-up garbage once things settle

        _ = _vm.SyncAutostartAsync();
        if (settings.AutoConnectOnLaunch) _ = _vm.ConnectAsync(StartOrigin.Background);
        _vm.StartBackgroundServices();
    }

    /// <summary>
    /// Runs <see cref="ProfileProbe"/> headless against the user's test sites (Settings → Test sites, plus the internal
    /// control site), writes a UTF-8 report and exits. The report is a developer tool and stays in English.
    /// </summary>
    private async Task RunProbeAsync(string outFile)
    {
        int exitCode = 0;
        var header = new List<string>();
        var sb = new System.Text.StringBuilder();
        try
        {
            var settings = AppSettings.Load();
            Loc.Instance.SetLanguage(settings.Language);   // variant / profile names in the report
            ProbeTargets.Configure(settings.TestSites);
            var targets = ProbeTargets.Urls;
            header.Add("targets: " + string.Join(", ", targets.Select(ProbeTargets.HostOf))
                       + (settings.TestSites is null ? " (default)" : " (custom)"));

            System.IO.File.WriteAllText(outFile, $"LiDPI probe running... {DateTime.Now:HH:mm:ss}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
            var progress = new Action<ProbeProgress>(p =>
            {
                if (p.Completed is not { } r) return;
                lock (sb) sb.AppendLine($"[{p.Index + 1}/{p.Total}] {p.VariantName}: {r.OkCount}/{r.Targets.Count}");
                try { lock (sb) System.IO.File.WriteAllText(outFile, "LiDPI probe running..." + Environment.NewLine + sb, new System.Text.UTF8Encoding(false)); }
                catch { /* progress file is best effort */ }
            });
            var results = await Task.Run(() => ProfileProbe.RunAsync(ProfileProbe.DefaultCandidates(), targets,
                progress: new SyncProgress(progress), diagnostics: m => { lock (header) header.Add("diag: " + m); }, rounds: 2));
            System.IO.File.WriteAllText(outFile, ProfileProbe.FormatReport(results, header), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            try { System.IO.File.WriteAllText(outFile, $"PROBE ERROR: {ex}{Environment.NewLine}{string.Join(Environment.NewLine, header)}{Environment.NewLine}{sb}", new System.Text.UTF8Encoding(false)); }
            catch { /* nowhere to report */ }
        }
        Shutdown(exitCode);
    }

    private sealed class SyncProgress(Action<ProbeProgress> report) : IProgress<ProbeProgress>
    {
        public void Report(ProbeProgress value) => report(value);
    }

    private void ShowMainWindow()
    {
        if (IsExiting || _vm is null) return;
        EnsureWindow().ShowFromTray();
    }

    /// <summary>
    /// The window only exists while it is on screen: closing it to the tray destroys it (visual tree,
    /// storyboards, bitmaps) and the next show builds a fresh one from the long-lived view model, which
    /// replays the entrance animation. Tray, engine and background services never depend on it.
    /// </summary>
    private MainWindow EnsureWindow()
    {
        if (_window is { } existing) return existing;
        var w = new MainWindow(_vm!);
        w.Closed += OnWindowClosed;
        _window = w;
        MainWindow = w;
        return w;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow w) w.Closed -= OnWindowClosed;
        if (!ReferenceEquals(sender, _window)) return;
        _window = null;
        MainWindow = null;
        if (IsExiting) return;
        // let the close animation-free teardown finish, then give the freed memory back to the OS
        MemoryTrimmer.TrimSoon(TimeSpan.FromSeconds(2));
    }

    /// <summary>Graceful exit: stop the engine (bounded), remove the tray icon, shut down.</summary>
    public async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;
        _window?.Hide();
        _vm?.StopBackgroundServices();
        if (_vm is not null)
        {
            // an auto-find probe runs its own packet engine: let it shut that down first (bounded)
            try { await Task.WhenAny(_vm.StopAutoFindAsync(), Task.Delay(StopTimeout)); }
            catch { /* best effort */ }
        }
        if (_controller is not null)
        {
            try { await Task.WhenAny(_controller.StopAsync(), Task.Delay(StopTimeout)); }
            catch { /* controller never throws, but be defensive */ }
        }
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown: there is no time for async work, block briefly.
        Interlocked.Exchange(ref _exiting, 1);
        StopEngineBlocking();
        _tray?.Dispose();
        _tray = null;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net for any path that reaches Shutdown without ExitAsync.
        Interlocked.Exchange(ref _exiting, 1);
        try { _vm?.StopBackgroundServices(); } catch { /* best effort */ }
        StopEngineBlocking();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Synchronous stop. Safe on the UI thread: the controller uses ConfigureAwait(false) and
    /// the view model only marshals with non-blocking InvokeAsync.
    /// </summary>
    private void StopEngineBlocking()
    {
        var c = _controller;
        if (c is null || c.State == EngineState.Stopped) return;
        try { Task.Run(c.StopAsync).Wait(StopTimeout); }
        catch { /* best effort during teardown */ }
    }

    private void AddLog(string level, string message)
    {
        try { _vm?.Logs.Add(new LogEntry(DateTime.Now, level, message)); }
        catch { /* ignore */ }
    }
}
