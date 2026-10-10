using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LiDPI.Engines.Divert;
using Microsoft.Win32;

namespace LiDPI.UI.Services;

public enum ConflictKind { Process, Service, Driver }

/// <summary>One running thing that conflicts with the packet engine.</summary>
/// <param name="Name">Process name without ".exe", service name, or WinDivert driver service name.</param>
public sealed record ConflictItem(ConflictKind Kind, string Name)
{
    /// <summary>"tool.exe", "Tool (service)", "WinDivert1.4 (driver)" (log / report / sheet text).</summary>
    public string Display => Kind switch
    {
        ConflictKind.Process => Name.ToLowerInvariant() + ".exe",
        ConflictKind.Service => Strings.ConflictServiceName(Name),
        _ => Strings.ConflictDriverName(Name),
    };
}

/// <summary>
/// Other DPI tools that use WinDivert. Two packet engines on the same traffic double-process every packet and the
/// connection dies, so the packet engine never starts next to one. <see cref="Scan"/> is read-only; only
/// <see cref="ResolveAsync"/> (called when the user presses "Close and continue") stops or kills anything, and it
/// never deletes a service. Blocking work: call off the UI thread.
/// </summary>
public static class DpiConflicts
{
    /// <summary>Process names (same list the engine warns about).</summary>
    public static IReadOnlyList<string> ProcessNames => DivertEngine.ConflictingTools;

    /// <summary>Windows services these tools install.</summary>
    public static readonly string[] ServiceNames = ["GoodbyeDPI", "zapret", "winws"];

    /// <summary>WinDivert driver service names (any version).</summary>
    public static readonly string[] DriverNames = ["WinDivert", "WinDivert1.4", "WinDivert1.3", "WinDivert1.2", "WinDivert1.1", "WinDivert14"];

    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);

    public static bool IsConflictingProcess(string name)
    {
        foreach (var n in DivertEngine.ConflictingTools)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Everything that conflicts right now: tool processes from <paramref name="processNames"/>, running tool services
    /// and — only when our own engine is off (<paramref name="includeDrivers"/>) — WinDivert driver services in the
    /// Running state, which then belong to another program. Never throws.
    /// </summary>
    public static IReadOnlyList<ConflictItem> Scan(IEnumerable<string> processNames, bool includeDrivers)
    {
        var list = new List<ConflictItem>();
        try
        {
            foreach (var p in processNames.Where(IsConflictingProcess).Distinct(StringComparer.OrdinalIgnoreCase))
                list.Add(new ConflictItem(ConflictKind.Process, p));
        }
        catch { /* snapshot failed */ }
        foreach (var svc in ServiceNames)
        {
            // a service that runs a listed process is the same tool: Describe folds the two for display
            if (QueryState(svc) is not (null or Stopped)) list.Add(new ConflictItem(ConflictKind.Service, svc));
        }
        if (includeDrivers)
            foreach (var d in DriverNames)
                if (QueryState(d) == Running && !IsOwnDriver(d)) list.Add(new ConflictItem(ConflictKind.Driver, d));
        return list;
    }

    /// <summary>
    /// Only WinDivert drivers, no known tool process/service: some other program uses the packet driver. Still a
    /// conflict (two WinDivert users double-process packets), but there is nothing LiDPI may close.
    /// </summary>
    public static bool IsDriverOnly(IReadOnlyList<ConflictItem> items) =>
        items.Count > 0 && items.All(i => i.Kind == ConflictKind.Driver);

    /// <summary>True if any listed tool service is installed (the sheet then offers "don't start with Windows").</summary>
    public static bool AnyServiceInstalled(IEnumerable<ConflictItem> items) =>
        items.Any(i => i.Kind == ConflictKind.Service) || ServiceNames.Any(Exists);

    /// <summary>Sheet text: "tool.exe, Tool (service)" — a service named like a listed process is folded into it.</summary>
    public static string Describe(IReadOnlyList<ConflictItem> items)
    {
        var shown = items.Where(i => !(i.Kind == ConflictKind.Service
                                       && items.Any(p => p.Kind == ConflictKind.Process && string.Equals(p.Name, i.Name, StringComparison.OrdinalIgnoreCase))));
        return string.Join(", ", shown.Select(i => i.Display));
    }

    /// <summary>
    /// The user pressed "Close and continue": stop the tool services (optionally set them to Disabled — never deleted),
    /// end the tool processes, then wait (≤ <see cref="ResolveTimeout"/>) until no WinDivert driver of theirs runs.
    /// Every step is logged. Returns what still conflicts afterwards (empty = safe to start).
    /// </summary>
    public static async Task<IReadOnlyList<ConflictItem>> ResolveAsync(bool disableAutostart, Action<string, string> log)
    {
        // 1) services first, so their recovery options cannot restart the process we are about to end
        foreach (var svc in ServiceNames)
        {
            if (!Exists(svc)) continue;
            if (disableAutostart)
            {
                if (SetDisabled(svc, out var err)) log("Info", Strings.LogConflictServiceDisabled(svc));
                else log("Warn", Strings.LogConflictServiceDisableFailed(svc, err));
            }
            if (QueryState(svc) is { } st && st != Stopped)
            {
                if (StopService(svc, out var err)) log("Info", Strings.LogConflictServiceStopped(svc));
                else log("Warn", Strings.LogConflictServiceStopFailed(svc, err));
            }
        }

        // 2) processes
        foreach (var name in ProcessNames)
        {
            Process[] procs = [];
            try { procs = Process.GetProcessesByName(name); } catch { /* ignore */ }
            foreach (var p in procs)
            {
                try
                {
                    var pid = p.Id;
                    p.Kill();
                    p.WaitForExit(3000);
                    log("Info", Strings.LogConflictProcessEnded(name + ".exe", pid));
                }
                catch (Exception ex) { log("Warn", Strings.LogConflictProcessEndFailed(name + ".exe", ex.Message)); }
                finally { p.Dispose(); }
            }
        }

        // 3) wait until their driver is gone (WinDivert unloads itself once the last handle closes)
        var sw = Stopwatch.StartNew();
        var stopAsked = false;
        while (sw.Elapsed < ResolveTimeout)
        {
            var running = DriverNames.Where(d => QueryState(d) is { } s && s != Stopped && !IsOwnDriver(d)).ToList();
            var svcBusy = ServiceNames.Any(s => QueryState(s) is { } st && st != Stopped);
            if (running.Count == 0 && !svcBusy) break;
            if (!stopAsked && sw.Elapsed > TimeSpan.FromSeconds(3))
            {
                // the owner is gone but the driver is still loaded: ask it to stop (it is no longer in use)
                stopAsked = true;
                foreach (var d in running)
                    if (StopService(d, out _)) log("Info", Strings.LogDriverStopped(d));
            }
            await Task.Delay(250).ConfigureAwait(false);
        }

        var left = Scan(ListNames(), includeDrivers: true);
        if (left.Count == 0) log("Info", Strings.LogConflictResolved);
        else log("Warn", Strings.LogConflictStill(Describe(left)));
        return left;
    }

    /// <summary>
    /// True if the driver service points at LiDPI's own WinDivert64.sys (%ProgramData%\LiDPI\bin, or the old per-user
    /// folder). WinDivert 2.x keeps its driver loaded after the last handle closes, so "running" alone does not mean
    /// another program is using it.
    /// </summary>
    private static bool IsOwnDriver(string service)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
            if (k?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image) return false;
            var path = Environment.ExpandEnvironmentVariables(image.Trim().Trim('"'));
            foreach (var prefix in new[] { @"\??\", @"\\?\" })
                if (path.StartsWith(prefix, StringComparison.Ordinal)) { path = path[prefix.Length..]; break; }
            path = Path.GetFullPath(path);
            string[] own =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LiDPI", "bin", "WinDivert64.sys"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiDPI", "bin", "WinDivert64.sys"),
            ];
            return own.Any(o => string.Equals(Path.GetFullPath(o), path, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static IEnumerable<string> ListNames() => AntiCheatWatcher.ListProcessNames();

    // ===================== service control =====================

    private const uint Stopped = 1, Running = 4;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004, ServiceStop = 0x0020, ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF, ServiceDisabled = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr svc, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr svc, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start, uint error, string? path, string? group,
        IntPtr tag, string? deps, string? user, string? password, string? display);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr h);

    private static bool Exists(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return k is not null;
        }
        catch { return false; }
    }

    /// <summary>Current state, or null if the service doesn't exist / can't be queried.</summary>
    private static uint? QueryState(string name)
    {
        uint? result = null;
        WithService(name, ServiceQueryStatus, svc =>
        {
            if (QueryServiceStatus(svc, out var st)) result = st.CurrentState;
            return true;
        }, out _);
        return result;
    }

    private static bool StopService(string name, out int error) =>
        WithService(name, ServiceQueryStatus | ServiceStop, svc =>
            ControlService(svc, 1 /* SERVICE_CONTROL_STOP */, out _), out error);

    private static bool SetDisabled(string name, out int error) =>
        WithService(name, ServiceChangeConfig, svc =>
            ChangeServiceConfig(svc, ServiceNoChange, ServiceDisabled, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null),
            out error);

    private static bool WithService(string name, uint access, Func<IntPtr, bool> action, out int error)
    {
        error = 0;
        try
        {
            var scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) { error = Marshal.GetLastPInvokeError(); return false; }
            try
            {
                var svc = OpenService(scm, name, access);
                if (svc == IntPtr.Zero) { error = Marshal.GetLastPInvokeError(); return false; }
                try
                {
                    var ok = action(svc);
                    if (!ok) error = Marshal.GetLastPInvokeError();
                    return ok;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }
        catch (Exception ex)
        {
            error = ex.HResult;
            return false;
        }
    }
}
