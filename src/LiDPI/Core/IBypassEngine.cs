namespace LiDPI.Core;

/// <summary>A DPI bypass engine implementation.</summary>
public interface IBypassEngine : IAsyncDisposable
{
    EngineMode Mode { get; }
    string DisplayName { get; }
    bool RequiresAdmin { get; }
    EngineState State { get; }
    EngineStats Stats { get; }

    /// <summary>Raised on every state transition (may be raised off the UI thread).</summary>
    event EventHandler<EngineState>? StateChanged;
    /// <summary>Raised for log output (may be raised off the UI thread).</summary>
    event EventHandler<LogEntry>? Log;

    /// <summary>Start the engine with the given profile.</summary>
    Task StartAsync(BypassProfile profile, CancellationToken ct = default);
    /// <summary>Stop the engine and release resources.</summary>
    Task StopAsync();
}
