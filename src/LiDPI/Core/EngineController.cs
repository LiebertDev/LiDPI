namespace LiDPI.Core;

/// <summary>
/// The single object the UI talks to. Serialises start/stop, never throws to callers
/// (failures set <see cref="State"/> to Error and are logged), StopAsync is idempotent.
/// Events may be raised off the UI thread.
/// </summary>
public sealed class EngineController
{
    private readonly Func<EngineMode, IBypassEngine> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBypassEngine? _engine;
    private volatile EngineState _state = EngineState.Stopped;

    public EngineController(Func<EngineMode, IBypassEngine> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public EngineState State => _state;
    public EngineMode Mode { get; private set; } = EngineMode.Divert;
    public BypassProfile Profile { get; private set; } = BuiltInProfiles.Standard;
    /// <summary>Stats of the current engine, or null if none.</summary>
    public EngineStats? Stats => _engine?.Stats;

    public event EventHandler<EngineState>? StateChanged;
    public event EventHandler<LogEntry>? Log;

    /// <summary>Start (or restart) with the given mode and profile.</summary>
    public async Task StartAsync(EngineMode mode, BypassProfile p)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StartCoreAsync(mode, p).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Stop the current engine. Safe to call repeatedly.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Stop if running/starting, otherwise start.</summary>
    public async Task ToggleAsync(EngineMode mode, BypassProfile p)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is EngineState.Running or EngineState.Starting)
                await StopCoreAsync().ConfigureAwait(false);
            else
                await StartCoreAsync(mode, p).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync(EngineMode mode, BypassProfile p)
    {
        if (_engine is not null)
            await StopCoreAsync().ConfigureAwait(false);

        Mode = mode;
        Profile = p ?? BuiltInProfiles.Standard;
        SetState(EngineState.Starting);
        try
        {
            var engine = _factory(mode);
            engine.StateChanged += OnEngineStateChanged;
            engine.Log += OnEngineLog;
            _engine = engine;
            WriteLog("Info", $"{engine.DisplayName} başlatılıyor (profil: '{Profile.Name}').");
            await engine.StartAsync(Profile).ConfigureAwait(false);
            SetState(engine.State == EngineState.Error ? EngineState.Error : EngineState.Running);
        }
        catch (Exception ex)
        {
            WriteLog("Error", $"Başlatılamadı: {ex.Message}");
            await DisposeEngineAsync().ConfigureAwait(false);
            SetState(EngineState.Error);
        }
    }

    private async Task StopCoreAsync()
    {
        if (_engine is null)
        {
            if (_state != EngineState.Stopped) SetState(EngineState.Stopped);
            return;
        }

        SetState(EngineState.Stopping);
        try
        {
            await _engine.StopAsync().ConfigureAwait(false);
            WriteLog("Info", "Durduruldu.");
        }
        catch (Exception ex)
        {
            WriteLog("Error", $"Durdurma başarısız: {ex.Message}");
        }
        await DisposeEngineAsync().ConfigureAwait(false);
        SetState(EngineState.Stopped);
    }

    private async Task DisposeEngineAsync()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        engine.StateChanged -= OnEngineStateChanged;
        engine.Log -= OnEngineLog;
        try { await engine.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { WriteLog("Error", $"Kaynaklar serbest bırakılamadı: {ex.Message}"); }
    }

    private void OnEngineStateChanged(object? sender, EngineState s)
    {
        if (ReferenceEquals(sender, _engine)) SetState(s);
    }

    private void OnEngineLog(object? sender, LogEntry e) => SafeInvoke(() => Log?.Invoke(this, e));

    private void SetState(EngineState s)
    {
        if (_state == s) return;
        _state = s;
        SafeInvoke(() => StateChanged?.Invoke(this, s));
    }

    private void WriteLog(string level, string message) =>
        SafeInvoke(() => Log?.Invoke(this, new LogEntry(DateTime.Now, level, message)));

    private static void SafeInvoke(Action a)
    {
        try { a(); } catch { /* subscriber errors must not break the engine lifecycle */ }
    }
}
