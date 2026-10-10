using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiDPI.UI.Services;

/// <summary>A program that, while running, means a kernel anti-cheat is (about to be) watching drivers.</summary>
/// <param name="Process">Process name without ".exe" (compared case-insensitively).</param>
/// <param name="Game">Shown to the user: "League of Legends açıldı…".</param>
public sealed record AntiCheatTrigger(string Process, string Game);

/// <summary>One kernel anti-cheat: its name, the processes that mean "a protected game is running" and its services.</summary>
public sealed record AntiCheatInfo(string Name, IReadOnlyList<AntiCheatTrigger> Triggers, IReadOnlyList<string> Services);

/// <summary>What one scan of the process list found: the protected game (if any) and running DPI tools.</summary>
/// <param name="Conflicts">Running conflicting DPI tool processes ("tool.exe, other.exe"), "" if none.</param>
public sealed record GuardSnapshot(AntiCheatHit? Game, string Conflicts)
{
    public static readonly GuardSnapshot Empty = new(null, "");
    public bool HasConflict => Conflicts.Length > 0;
}

/// <summary>A trigger process that is running right now.</summary>
public sealed record AntiCheatHit(AntiCheatInfo AntiCheat, AntiCheatTrigger Trigger)
{
    public string AntiCheatName => AntiCheat.Name;
    public string Game => Trigger.Game;
}

/// <summary>
/// Detects games protected by kernel anti-cheats (Vanguard, FACEIT, EAC, BattlEye), which may close the game when the
/// WinDivert driver loads. Polls the process list every few seconds only while <see cref="Active"/> (the view model
/// switches it on while the packet engine runs or a game pause is pending); otherwise no timer exists and nothing runs.
/// Polling was chosen over a WMI Win32_ProcessStartTrace subscription: one snapshot per 3 s is a few milliseconds,
/// needs no WMI service/COM apartment, sees processes that were already running and reports exits the same way.
/// The same snapshot also reports conflicting DPI tools (<see cref="DpiConflicts.ProcessNames"/>), which must never
/// run next to the packet engine. <see cref="Changed"/> is raised on the UI dispatcher when the result changes.
/// </summary>
public sealed class AntiCheatWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The protected titles. Order matters: the first running trigger wins, so specific games come before launchers
    /// ("League of Legends" is reported rather than "Riot Client" when both run).
    /// </summary>
    public static IReadOnlyList<AntiCheatInfo> Known { get; } =
    [
        new("Riot Vanguard",
        [
            new("League of Legends", "League of Legends"),
            new("LeagueClient", "League of Legends"),
            new("VALORANT-Win64-Shipping", "VALORANT"),
            new("VALORANT", "VALORANT"),
            new("RiotClientServices", "Riot Client"),
            new("RiotClientUx", "Riot Client"),
        ], ["vgk", "vgc"]),
        new("FACEIT Anti-Cheat",
        [
            new("FACEIT", "FACEIT"),
            new("faceitclient", "FACEIT"),
        ], ["FACEIT"]),
        new("Easy Anti-Cheat",
        [
            new("start_protected_game", "Easy Anti-Cheat"),
            new("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
            new("EasyAntiCheat", "Easy Anti-Cheat"),
        ], ["EasyAntiCheat_EOS", "EasyAntiCheat"]),
        new("BattlEye",
        [
            new("BEService", "BattlEye"),
            new("BEService_x64", "BattlEye"),
        ], ["BEService", "BEDaisy"]),
    ];

    private static readonly Dictionary<string, AntiCheatHit> ByProcess = BuildIndex();

    private readonly Dispatcher _dispatcher;
    private readonly object _lock = new();
    private Timer? _timer;
    private int _scanning;
    private bool _disposed;
    private GuardSnapshot _current = GuardSnapshot.Empty;

    public AntiCheatWatcher(Dispatcher dispatcher, TimeSpan? interval = null)
    {
        _dispatcher = dispatcher;
        Interval = interval ?? DefaultInterval;
    }

    public TimeSpan Interval { get; }

    /// <summary>Lists running process names (replaceable for tests). Called on the thread pool.</summary>
    public Func<IEnumerable<string>> ProcessLister { get; set; } = ListProcessNames;

    /// <summary>Number of scans so far (tests: proves that nothing runs while inactive).</summary>
    public int ScanCount => Volatile.Read(ref _scanCount);
    private int _scanCount;

    /// <summary>True while the poll timer exists.</summary>
    public bool IsPolling { get { lock (_lock) return _timer is not null; } }

    /// <summary>Last result of a poll or <see cref="ScanAsync"/> (UI thread).</summary>
    public GuardSnapshot Current => _current;

    /// <summary>Raised on the UI thread when the scan result changes.</summary>
    public event Action<GuardSnapshot>? Changed;

    /// <summary>Starts (first scan immediately) or stops polling. Idempotent, UI thread (it resets <see cref="Current"/>).</summary>
    public void SetActive(bool on)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (on)
            {
                if (_timer is not null) return;
                _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, Interval);
            }
            else if (_timer is { } t)
            {
                _timer = null;
                t.Dispose();
                // unwatched from now on: the next activation reports whatever it finds as new
                _current = GuardSnapshot.Empty;
            }
        }
    }

    /// <summary>One scan off the UI thread; updates <see cref="Current"/> (and raises <see cref="Changed"/> if it differs).</summary>
    public async Task<GuardSnapshot> ScanAsync()
    {
        var hit = await Task.Run(Scan).ConfigureAwait(true);
        Apply(hit);
        return hit;
    }

    private void Poll()
    {
        // a slow snapshot must not pile up timer callbacks
        if (Interlocked.Exchange(ref _scanning, 1) != 0) return;
        try
        {
            var hit = Scan();
            lock (_lock) if (_disposed || _timer is null) return;
            _dispatcher.InvokeAsync(() =>
            {
                lock (_lock) if (_disposed || _timer is null) return;   // stopped meanwhile: stale
                Apply(hit);
            });
        }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    private void Apply(GuardSnapshot hit)
    {
        if (hit == _current) return;
        _current = hit;
        Changed?.Invoke(hit);
    }

    /// <summary>First running trigger in <see cref="Known"/> order (or null) plus running DPI tools. Never throws.</summary>
    private GuardSnapshot Scan()
    {
        Interlocked.Increment(ref _scanCount);
        AntiCheatHit? best = null;
        var bestRank = int.MaxValue;
        SortedSet<string>? conflicts = null;
        try
        {
            foreach (var name in ProcessLister())
            {
                if (DpiConflicts.IsConflictingProcess(name))
                    (conflicts ??= new(StringComparer.OrdinalIgnoreCase)).Add(name.ToLowerInvariant() + ".exe");
                if (!ByProcess.TryGetValue(name, out var hit)) continue;
                var rank = Rank(hit);
                if (rank < bestRank) { best = hit; bestRank = rank; }
            }
        }
        catch { /* snapshot failed: report nothing rather than guess */ }
        return new GuardSnapshot(best, conflicts is null ? "" : string.Join(", ", conflicts));
    }

    /// <summary>Process names of the current session snapshot (no handles are opened).</summary>
    public static IEnumerable<string> ListProcessNames()
    {
        var procs = Process.GetProcesses();
        var names = new List<string>(procs.Length);
        foreach (var p in procs)
        {
            try { names.Add(p.ProcessName); } catch { /* exited meanwhile */ }
            p.Dispose();
        }
        return names;
    }

    /// <summary>Every running trigger, for the diagnostic report ("LeagueClient.exe (Riot Vanguard)").</summary>
    public static IReadOnlyList<string> RunningTriggers()
    {
        var found = new List<string>();
        foreach (var name in ListProcessNames())
            if (ByProcess.TryGetValue(name, out var hit))
                found.Add($"{hit.Trigger.Process}.exe ({hit.AntiCheatName})");
        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Installed anti-cheat services ("vgk: running"), for the diagnostic report only. Vanguard's vgk runs from boot on
    /// every PC with LoL/VALORANT installed, so an installed service alone never triggers anything.
    /// </summary>
    public static IReadOnlyList<string> InstalledServices()
    {
        var found = new List<string>();
        foreach (var ac in Known)
            foreach (var svc in ac.Services)
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                if (k is null) continue;
                found.Add($"{svc} ({ac.Name}): {ServiceState(svc)}");
            }
        return found;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        Changed = null;
    }

    // ===================== helpers =====================

    private static Dictionary<string, AntiCheatHit> BuildIndex()
    {
        var d = new Dictionary<string, AntiCheatHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var ac in Known)
            foreach (var t in ac.Triggers)
                d.TryAdd(t.Process, new AntiCheatHit(ac, t));
        return d;
    }

    private static int Rank(AntiCheatHit hit)
    {
        var rank = 0;
        foreach (var ac in Known)
            foreach (var t in ac.Triggers)
            {
                if (ReferenceEquals(t, hit.Trigger)) return rank;
                rank++;
            }
        return rank;
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;

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
    private static extern bool CloseServiceHandle(IntPtr h);

    private static string ServiceState(string name)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return "installed";
        try
        {
            var svc = OpenService(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return "installed";
            try
            {
                if (!QueryServiceStatus(svc, out var st)) return "installed";
                return st.CurrentState switch
                {
                    1 => "stopped",
                    2 => "starting",
                    3 => "stopping",
                    4 => "running",
                    _ => $"state {st.CurrentState}",
                };
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
