using System.Runtime;
using System.Runtime.InteropServices;

namespace LiDPI.UI.Services;

/// <summary>
/// Gives memory back to the OS once the app goes to the tray: an optimized (GC-decides) collection, with a full
/// compacting one including the large object heap at most every 10 minutes, then trims the process working set
/// (pages are faulted back in lazily if they are touched again). Runs on a background thread after a delay, so
/// hiding the window never waits for it.
/// </summary>
public static class MemoryTrimmer
{
    private static readonly long FullCompactionIntervalMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    private static long _lastFullCompaction = long.MinValue / 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    private static int _busy;

    /// <summary>Collects and trims on a background thread. Never throws; overlapping calls are coalesced.</summary>
    public static void TrimSoon(TimeSpan delay)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
                Trim();
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>GC + working-set trim, synchronously (call from a background thread). Never throws.</summary>
    public static void Trim()
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastFullCompaction) >= FullCompactionIntervalMs)
            {
                Interlocked.Exchange(ref _lastFullCompaction, now);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            else
            {
                // let the GC decide whether a collection is worth it now; no LOH compaction
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            }
        }
        catch { /* best effort */ }

        try { SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1); }
        catch { /* best effort */ }
    }
}
