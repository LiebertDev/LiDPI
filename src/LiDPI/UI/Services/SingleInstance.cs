namespace LiDPI.UI.Services;

/// <summary>
/// Single-instance guard: a named mutex owns the instance, a named event lets a second
/// launch ask the first one to show its window.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "LiDPI_SingleInstance";
    private const string EventName = "LiDPI_SingleInstance_Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, Action onShowRequested)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => { if (!_disposed) onShowRequested(); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Returns the guard if this is the first instance; otherwise signals the running instance and returns null.
    /// <paramref name="onShowRequested"/> is invoked on a thread-pool thread.
    /// </summary>
    public static SingleInstance? TryAcquire(Action onShowRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // The previous owner may have crashed without releasing it.
            try { createdNew = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { createdNew = true; }
        }

        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var ev))
                    using (ev) ev.Set();
            }
            catch { /* best effort */ }
            return null;
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return new SingleInstance(mutex, showEvent, onShowRequested);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
