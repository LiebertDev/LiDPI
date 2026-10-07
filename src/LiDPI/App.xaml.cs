using System.Windows;
using System.Windows.Media.Animation;
using LiDPI.Core;
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

        // Cap every animation at 60 fps.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 60 });

        DispatcherUnhandledException += (_, args) =>
        {
            AddLog("Error", $"Beklenmeyen hata: {args.Exception.Message}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => StopEngineBlocking();

        // Restore the system proxy if a previous run died while in proxy mode.
        try { LiDPI.Engines.Proxy.ProxyEngine.RestoreIfDirty(); } catch { /* best effort */ }

        var settings = AppSettings.Load();
        _controller = new EngineController(EngineFactory.Create); // ENGINE-WIRE (UI testing: m => new NullEngine(m))
        _vm = new MainViewModel(_controller, settings);
        _window = new MainWindow(_vm);
        MainWindow = _window;
        _tray = new TrayService(_vm, ShowMainWindow, () => _ = ExitAsync());

        var startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase))
                          || settings.StartMinimized;
        if (!startInTray) _window.Show();

        _ = _vm.SyncAutostartAsync();
        if (settings.AutoConnectOnLaunch) _ = _vm.ConnectAsync();
    }

    /// <summary>Runs <see cref="ProfileProbe"/> headless, writes a UTF-8 report and exits.</summary>
    private async Task RunProbeAsync(string outFile)
    {
        int exitCode = 0;
        var header = new List<string>();
        var sb = new System.Text.StringBuilder();
        try
        {
            System.IO.File.WriteAllText(outFile, $"LiDPI sonda çalışıyor... {DateTime.Now:HH:mm:ss}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
            var progress = new Action<ProbeProgress>(p =>
            {
                if (p.Completed is not { } r) return;
                lock (sb) sb.AppendLine($"[{p.Index + 1}/{p.Total}] {p.VariantName}: {r.OkCount}/{r.Targets.Count}");
                try { lock (sb) System.IO.File.WriteAllText(outFile, "LiDPI sonda çalışıyor..." + Environment.NewLine + sb, new System.Text.UTF8Encoding(false)); }
                catch { /* progress file is best effort */ }
            });
            var results = await Task.Run(() => ProfileProbe.RunAsync(ProfileProbe.DefaultCandidates(),
                progress: new SyncProgress(progress), diagnostics: m => { lock (header) header.Add("diag: " + m); }, rounds: 2));
            System.IO.File.WriteAllText(outFile, ProfileProbe.FormatReport(results, header), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            exitCode = 1;
            try { System.IO.File.WriteAllText(outFile, $"SONDA HATASI: {ex}{Environment.NewLine}{string.Join(Environment.NewLine, header)}{Environment.NewLine}{sb}", new System.Text.UTF8Encoding(false)); }
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
        if (IsExiting) return;
        _window?.ShowFromTray();
    }

    /// <summary>Graceful exit: stop the engine (bounded), remove the tray icon, shut down.</summary>
    public async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;
        _window?.Hide();
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
