using System.Runtime.CompilerServices;
using LiDPI.Localization;

namespace LiDPI.UI;

/// <summary>
/// Typed access to the UI strings for C# code. The texts live in <c>Localization/*.json</c>; the key of each member is
/// its own name (XAML uses the same keys through <c>{l:T Key}</c>). Methods take string.Format arguments and format
/// with the active language's culture. Every call reads the current language, so never cache the result in a field
/// that outlives a language switch (use <see cref="LocText"/> for that).
/// </summary>
public static class Strings
{
    private static string L([CallerMemberName] string key = "") => Loc.Instance.Get(key);
    private static string F(object?[] args, [CallerMemberName] string key = "") => Loc.Instance.Format(key, args);

    // ===== main window =====
    public static string StatusConnecting => L();
    public static string StatusDisconnecting => L();
    public static string StatusRunning => L();
    public static string StatusError => L();
    public static string StatusStopped => L();
    public static string ModeAll => L();
    public static string ModeBrowser => L();
    public static string EngineProxyName => L();
    public static string ErrorUnexpectedStop => L();
    /// <summary>{0}: number already formatted ("12,5").</summary>
    public static string NumThousand(string n) => F([n]);
    public static string NumMillion(string n) => F([n]);
    public static string NumBillion(string n) => F([n]);

    // ===== auto-find =====
    public static string AutoFindToolTip => L();
    /// <summary>{0}: name of the "all apps" mode.</summary>
    public static string AutoFindToolTipNeedsAllApps(string mode) => F([mode]);
    public static string AutoFindPreparing => L();
    public static string AutoFindCancelling => L();
    public static string AutoFindRowError => L();
    public static string AutoFindNoteBuiltIn => L();
    public static string AutoFindNoteCustom => L();
    public static string AutoFindNotePartial => L();
    public static string AutoFindFailTitleNoInternet => L();
    public static string AutoFindFailTextNoInternet => L();
    public static string AutoFindFailTitleEngine => L();
    public static string AutoFindFailTextEngine => L();
    public static string AutoFindFailTitleNothing => L();
    public static string AutoFindFailTextNothing => L();
    public static string AutoFindFailTitleError => L();
    /// <summary>{0}: comma-separated host list.</summary>
    public static string AutoFindTestedSites(string hosts) => F([hosts]);
    public static string LogAutoFindStarted(int count) => F([count]);
    public static string LogAutoFindCancelled => L();
    public static string LogAutoFindFailed(string error) => F([error]);
    /// <summary>{0}: variant, {1}/{2}: ok/total requests, {3}: average ms.</summary>
    public static string LogAutoFindPicked(string variant, int ok, int total, double avgMs) => F([variant, ok, total, avgMs]);

    // ===== profiles (names and descriptions are keyed "Profile.{id}.Name" / "Profile.{id}.Desc") =====
    public static string DescFakePacket(string detail) => F([detail]);
    public static string DescAutoTtl => L();
    public static string DescSplitSni => L();
    public static string DescSplitAt(int position) => F([position]);
    public static string DescDisorder => L();
    public static string DescBlockQuic => L();
    public static string DescBadChecksum => L();
    public static string DescBadSeq => L();
    public static string DescFakeRepeat(int count) => F([count]);
    public static string DescHostTrick => L();
    public static string DescSystemDns => L();
    public static string DescNoTechnique => L();
    public static string DescPickedByTest => L();
    public static string VariantTtlByte(int ttl, int position) => F([ttl, position]);
    public static string VariantTtlSni(int ttl) => F([ttl]);
    public static string VariantTtlRepByte(int ttl, int repeats, int position) => F([ttl, repeats, position]);

    // ===== first run / reconnect / health =====
    public static string ReasonNetwork => L();
    public static string ReasonResume => L();
    public static string LogReconnecting(string reason) => F([reason]);
    public static string LogReconnectNoNetwork => L();
    public static string LogDnsFlushed => L();
    public static string HealthWarnReconnect => L();
    public static string HealthBalloonTitle => L();
    public static string HealthBalloonText => L();
    /// <summary>{0}: relative time ("az önce", "2 dk önce").</summary>
    public static string HealthLastCheck(string when) => F([when]);
    public static string HealthJustNow => L();
    public static string HealthMinutesAgo(int minutes) => F([minutes]);
    public static string HealthHoursAgo(int hours) => F([hours]);
    public static string HealthToolTipOk => L();
    public static string HealthToolTipBlocked => L();
    public static string HealthToolTipOffline => L();
    public static string HealthOff => L();
    public static string Health5 => L();
    public static string Health15 => L();
    public static string LogHealthOk(int ok, int total) => F([ok, total]);
    public static string LogHealthBlocked => L();
    public static string LogHealthOffline => L();

    // ===== game protection / conflicting DPI tools =====
    public static string GameActionPause => L();
    public static string GameActionBrowser => L();
    public static string GameActionNothing => L();
    public static string GameBalloonTitle => L();
    /// <summary>{0}: anti-cheat name ("Riot Vanguard").</summary>
    public static string GameStartHeldText(string antiCheat) => F([antiCheat]);
    public static string GameStartHeldResumeText(string antiCheat) => F([antiCheat]);
    public static string GameStartBrowserText(string antiCheat) => F([antiCheat]);
    /// <summary>{0}: game name ("League of Legends").</summary>
    public static string GamePausedText(string game) => F([game]);
    public static string GameSwitchedText(string game) => F([game]);
    public static string GameNotifiedText(string game) => F([game]);
    public static string GameResumedText => L();
    public static string GameRestoredText => L();
    public static string LogGameDetected(string game, string antiCheat) => F([game, antiCheat]);
    public static string LogGamePaused(string game) => F([game]);
    public static string LogGameSwitched(string game) => F([game]);
    public static string LogGameStartHeld(string antiCheat) => F([antiCheat]);
    public static string LogGameOverride(string game) => F([game]);
    public static string LogGameIgnored(string game) => F([game]);
    public static string LogGameEnded => L();
    public static string ConflictBalloonTitle => L();
    /// <summary>{0}: what was found ("tool.exe, WinDivert1.4 (driver)").</summary>
    public static string ConflictText(string tools) => F([tools]);
    public static string ConflictStoppedText(string tools) => F([tools]);
    public static string ConflictHeldBalloon(string tools) => F([tools]);
    public static string ConflictFailedText(string tools) => F([tools]);
    public static string ConflictServiceName(string service) => F([service]);
    public static string ConflictDriverName(string service) => F([service]);
    public static string LogConflictHeld(string tools) => F([tools]);
    public static string LogConflictStopped(string tools) => F([tools]);
    public static string LogConflictProxy(string tools) => F([tools]);
    public static string LogConflictUserResolve(string tools) => F([tools]);
    public static string LogConflictServiceDisabled(string service) => F([service]);
    public static string LogConflictServiceDisableFailed(string service, int error) => F([service, error]);
    public static string LogConflictServiceStopped(string service) => F([service]);
    public static string LogConflictServiceStopFailed(string service, int error) => F([service, error]);
    public static string LogConflictProcessEnded(string process, int pid) => F([process, pid]);
    public static string LogConflictProcessEndFailed(string process, string error) => F([process, error]);
    public static string LogConflictResolved => L();
    public static string LogConflictStill(string tools) => F([tools]);

    // ===== settings =====
    public static string SettingStartWithWindows => L();
    public static string LogAutostartOn => L();
    public static string LogAutostartOff => L();
    public static string LogAutostartFailed => L();
    public static string LogUnexpectedError(string message) => F([message]);

    // ===== manual update check (Settings → About, tray) =====
    public static string UpdateCheckBusy => L();
    public static string UpdateCheckUpToDate => L();
    public static string UpdateCheckFailed => L();
    public static string UpdateCheckOpened(string version) => F([version]);
    public static string AboutVersion(string version) => F([version]);
    public static string TrayUpdateCheck => L();
    public static string TrayUpdateBalloonTitle => L();
    public static string LogUpdateAvailable(string version) => F([version]);
    public static string LogUpdatePageFailed => L();

    // ===== test sites =====
    public static string TestSitesInvalid => L();
    public static string TestSitesDuplicate => L();
    public static string TestSitesLastOne => L();
    public static string TestSitesFull(int max) => F([max]);

    // ===== custom DoH provider =====
    public static string DohCustomChip => L();
    /// <summary>{0}: host of the custom DoH URL.</summary>
    public static string DohCustomName(string host) => F([host]);
    public static string DohCustomTestTimeout => L();
    public static string DohCustomTesting => L();
    public static string DohCustomSaved => L();
    public static string DohCustomNotSaved => L();
    public static string DohCustomInvalidUrl => L();
    public static string DohCustomInvalidIp => L();
    public static string DohCustomTestOk(int ms) => F([ms]);
    public static string DohCustomTestFailed(string error) => F([error]);
    public static string LogDohBootstrapResolved(string host, string ip) => F([host, ip]);
    public static string LogDohBootstrapFailed(string host, string error) => F([host, error]);
    public static string LogDohCustomSaved(string host) => F([host]);

    // ===== diagnostic report =====
    public static string DiagDialogTitle => L();
    public static string DiagFileFilter => L();
    public static string DiagSaved => L();
    public static string LogDiagSaved(string file) => F([file]);
    public static string LogDiagFailed(string error) => F([error]);

    // ===== tray =====
    public static string TrayConnect => L();
    public static string TrayDisconnect => L();
    public static string TrayShow => L();
    public static string TrayExit => L();

    // ===== engine controller =====
    public static string LogEngineStarting(string engine, string profile) => F([engine, profile]);
    public static string LogEngineStartFailed(string error) => F([error]);
    public static string LogEngineStopped => L();
    public static string LogEngineStopFailed(string error) => F([error]);
    public static string LogEngineDisposeFailed(string error) => F([error]);
    public static string LogRestartAppsHint => L();

    // ===== packet engine (WinDivert) =====
    public static string LogDivertStartFailed(string error) => F([error]);
    public static string LogDivertStopped => L();
    public static string ErrDivertPrepare(string error) => F([error]);
    public static string LogNoTechniques => L();
    public static string ErrDivertFilter(string error) => F([error]);
    public static string LogAutoTtlOff(int ttl, string error) => F([ttl, error]);
    public static string LogDivertRunning(string techniques) => F([techniques]);
    public static string LogRecvFailed(int error) => F([error]);
    public static string LogPacketFailed(string error) => F([error]);
    public static string FailWinDivertError(int error) => F([error]);
    public static string FailUnknown => L();
    public static string LogLoopDied(string reason) => F([reason]);
    public static string LogTtlListenerStopped(string error) => F([error]);
    public static string LogDohPassthrough(string error) => F([error]);
    public static string LogDnsInjectFailed(string error) => F([error]);
    public static string LogConflictTool(string name) => F([name]);
    public static string LogForeignDriver(string service) => F([service]);
    public static string LogTcpDnsOverflow => L();
    public static string LogTcpDnsDohFailed(string error) => F([error]);
    public static string ErrResourceMissing(string name) => F([name]);
    public static string LogDriverStopped(string service) => F([service]);
    public static string LogDriverStaleDeleted(string service, string path) => F([service, path]);
    public static string LogDriverStaleDeleteFailed(string service, int error) => F([service, error]);
    /// <summary>WinDivertOpen failure explanation ("ErrOpen5", ...); unknown codes use ErrOpenOther.</summary>
    public static string ErrOpen(int code, Func<string> systemMessage) =>
        Loc.Instance.TryGet("ErrOpen" + code, out var s) ? s : Loc.Instance.Format("ErrOpenOther", systemMessage(), code);

    // ===== proxy engine =====
    public static string LogProxyStartFailed(string error) => F([error]);
    public static string LogProxyStoppedRestored => L();
    public static string LogProxyStopped => L();
    public static string LogProxyRunning(int port, string techniques) => F([port, techniques]);
    public static string LogProxyIgnoredTechniques => L();
    public static string LogAcceptFailed(string error) => F([error]);
    public static string LogConnectionError(string error) => F([error]);
    public static string LogDohFallbackSystem(string host) => F([host]);
    public static string LogResolveFailed(string host) => F([host]);
    public static string LogNoAddress(string host) => F([host]);
    public static string LogConnectFailed(string host, int port) => F([host, port]);

    // ===== DNS / network =====
    public static string ErrDohInvalidResponse => L();
    public static string ErrDohNoAddress => L();
    public static string LogDohSwitch(string from, string to) => F([from, to]);
    public static string ErrDohAllFailed => L();
    public static string ErrInvalidHostName => L();
    public static string ErrInvalidUrl => L();
    public static string ErrTimeoutSeconds(double seconds) => F([seconds]);
}
