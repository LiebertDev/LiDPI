namespace LiDPI.Core;

/// <summary>No-op engine for testing: simulates Starting -> Running with delays.</summary>
public sealed class NullEngine : IBypassEngine
{
    private readonly TimeSpan _delay;

    public NullEngine(EngineMode mode = EngineMode.Divert, TimeSpan? delay = null)
    {
        Mode = mode;
        _delay = delay ?? TimeSpan.FromMilliseconds(500);
    }

    public EngineMode Mode { get; }
    public string DisplayName => $"Null ({Mode})";
    public bool RequiresAdmin => false;
    public EngineState State { get; private set; } = EngineState.Stopped;
    public EngineStats Stats { get; } = new();

    public event EventHandler<EngineState>? StateChanged;
    public event EventHandler<LogEntry>? Log;

    public async Task StartAsync(BypassProfile profile, CancellationToken ct = default)
    {
        SetState(EngineState.Starting);
        Write($"Simulating start with profile '{profile.DisplayName}'.");
        await Task.Delay(_delay, ct).ConfigureAwait(false);
        Stats.Reset();
        Stats.StartedAt = DateTime.UtcNow;
        SetState(EngineState.Running);
        Write("Running.");
    }

    public async Task StopAsync()
    {
        if (State == EngineState.Stopped) return;
        SetState(EngineState.Stopping);
        await Task.Delay(_delay / 2).ConfigureAwait(false);
        SetState(EngineState.Stopped);
        Write("Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void SetState(EngineState s)
    {
        State = s;
        StateChanged?.Invoke(this, s);
    }

    private void Write(string msg) => Log?.Invoke(this, new LogEntry(DateTime.Now, "Info", msg));
}
