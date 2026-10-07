using System.Runtime;
using System.Runtime.InteropServices;

namespace LiDPI.UI.Services;

/// <summary>
/// Gives memory back to the OS once the app goes to the tray: full blocking GC with LOH compaction,
/// then trims the process working set (pages are faulted back in lazily if they are touched again).
/// </summary>
public static class MemoryTrimmer
{
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

    /// <summary>Full compacting GC + working-set trim, synchronously. Never throws.</summary>
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { /* best effort */ }

        try { SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1); }
        catch { /* best effort */ }
    }
}
