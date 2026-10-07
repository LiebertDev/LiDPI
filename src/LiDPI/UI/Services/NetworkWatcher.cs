using System.Net.NetworkInformation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiDPI.UI.Services;

/// <summary>Why <see cref="NetworkWatcher.Changed"/> fired.</summary>
public enum NetworkChangeReason { Network, Resume }

/// <summary>
/// Raises a debounced <see cref="Changed"/> on the UI dispatcher after network address/availability changes
/// (~3 s quiet period) or after resume from sleep (~5 s delay). System events arrive on arbitrary threads;
/// nothing here blocks them.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    public static readonly TimeSpan NetworkDebounce = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private NetworkChangeReason _pendingReason;
    private bool _suspended;
    private bool _disposed;

    public NetworkWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<NetworkChangeReason>? Changed;

    private void OnAddressChanged(object? sender, EventArgs e) => Schedule(NetworkChangeReason.Network, NetworkDebounce);
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Schedule(NetworkChangeReason.Network, NetworkDebounce);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                lock (_lock)
                {
                    _suspended = true;
                    if (!_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                break;
            case PowerModes.Resume:
                lock (_lock) _suspended = false;
                Schedule(NetworkChangeReason.Resume, ResumeDelay);
                break;
        }
    }

    private void Schedule(NetworkChangeReason reason, TimeSpan delay)
    {
        lock (_lock)
        {
            if (_disposed || _suspended) return;
            // a pending resume keeps its (stronger) reason; every new event restarts the quiet period
            if (reason == NetworkChangeReason.Resume || _pendingReason != NetworkChangeReason.Resume) _pendingReason = reason;
            if (_pendingReason == NetworkChangeReason.Resume && delay < ResumeDelay) delay = ResumeDelay;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        NetworkChangeReason reason;
        lock (_lock)
        {
            if (_disposed || _suspended) return;
            reason = _pendingReason;
            _pendingReason = NetworkChangeReason.Network;
        }
        _dispatcher.InvokeAsync(() =>
        {
            // disposed (app exit / update) between the timer tick and this dispatch: no restart
            lock (_lock) if (_disposed) return;
            if (_dispatcher.HasShutdownStarted) return;
            Changed?.Invoke(reason);
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Changed = null;
        }
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Dispose();
    }
}
