using System.Windows.Threading;
using LiDPI.Core;
using LiDPI.Localization;
using LiDPI.UI.Services;

namespace LiDPI.UI.ViewModels;

/// <summary>Who asked for a start: the user (a sheet may ask back) or a background path (auto-connect, reconnect, resume).</summary>
public enum StartOrigin { User, Background }

/// <summary>Which face the guard sheet shows.</summary>
public enum GuardSheetKind { None, GameBlocked, GamePaused, GameSwitched, GameNotified, Conflict }

/// <summary>One choice of the "When a game starts" selector. The label follows the UI language.</summary>
public sealed record GameActionOption(GameAction Action, string LabelKey)
{
    public string Label => Loc.T(LabelKey);
    public override string ToString() => Label;
}

/// <summary>
/// v1.1.1 guards around the packet engine:
/// <list type="bullet">
/// <item>Game protection: kernel anti-cheats (Vanguard, FACEIT, EAC, BattlEye) may close the game when a driver loads.
/// Every Divert start is checked first; a game that starts while Divert runs pauses it (or switches to the proxy), and
/// the previous state comes back a few seconds after the game closed. "Start anyway" lasts for the current game run.</item>
/// <item>Conflicting DPI tools: two WinDivert users double-process packets and the line dies, so Divert never starts
/// next to one, and stops at once if one appears. Nothing is closed unless the user presses "Close and continue".</item>
/// </list>
/// Every Divert start (button, tray, auto-connect, auto-reconnect, resume, auto-find) passes <see cref="GuardGateAsync"/>.
/// The process list is polled only while Divert runs/starts, an auto-find runs, or a game pause waits for the game to end.
/// UI thread only.
/// </summary>
public sealed partial class MainViewModel
{
    private AntiCheatWatcher _guard = null!;
    private int _intentSerial;
    private bool _gameOverride;        // "Start anyway" for the current game run
    private bool _gamePaused;          // stopped by game protection: reconnect when the game is gone
    private bool _gameSwitched;        // switched to the proxy by game protection: switch back when the game is gone
    private DispatcherTimer? _gameResumeTimer;
    private bool _guardPendingAutoFind;
    private string _guardGameName = "";
    private GuardSheetKind _guardSheet;
    private LocText _guardTitle, _guardText, _guardHint;
    private bool _conflictBusy, _conflictDisableAutostart, _conflictCanDisable, _conflictDriverOnly;
    private string? _gameLogKey, _gameBalloonKey;   // last game/anti-cheat logged / shown in the tray (no double notices)

    // ===================== hooks (replaceable for tests) =====================

    /// <summary>The detector (tests replace its process lister and read its poll state).</summary>
    public AntiCheatWatcher GuardWatcher => _guard;

    /// <summary>Quiet period after the last game process exited before the previous state comes back.</summary>
    public TimeSpan GameResumeDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Conflict check (arg: include foreign WinDivert drivers). Called on the UI thread, must not block it.</summary>
    public Func<bool, Task<IReadOnlyList<ConflictItem>>> ConflictScanner { get; set; } = null!;

    /// <summary>"Close and continue" (arg: disable autostart). Returns what still conflicts.</summary>
    public Func<bool, Task<IReadOnlyList<ConflictItem>>> ConflictResolver { get; set; } = null!;

    private void InitGuard()
    {
        _guard = new AntiCheatWatcher(_dispatcher);
        _guard.Changed += OnGuardChanged;
        ConflictScanner = includeDrivers =>
        {
            var lister = _guard.ProcessLister;
            return Task.Run(() => DpiConflicts.Scan(lister(), includeDrivers));
        };
        ConflictResolver = disable => Task.Run(() => DpiConflicts.ResolveAsync(disable,
            (level, msg) => EnqueueLog(new LogEntry(DateTime.Now, level, msg))));
    }

    private void DisposeGuard()
    {
        _gameResumeTimer?.Stop();
        _guard.Changed -= OnGuardChanged;
        _guard.Dispose();
    }

    // ===================== settings =====================

    public bool GameGuardEnabled
    {
        get => _settings.GameGuard;
        set
        {
            if (_settings.GameGuard == value) return;
            _settings.GameGuard = value;
            _settings.Save();
            if (!value)
            {
                CancelGameResume();
                _gamePaused = _gameSwitched = _gameOverride = false;
                if (_guardSheet is not (GuardSheetKind.None or GuardSheetKind.Conflict)) HideGuardSheet();
            }
            OnPropertyChanged();
            UpdateGuardWatch();
        }
    }

    public IReadOnlyList<GameActionOption> GameActionOptions { get; } =
    [
        new(GameAction.Pause, nameof(Strings.GameActionPause)),
        new(GameAction.SwitchToBrowser, nameof(Strings.GameActionBrowser)),
        new(GameAction.Nothing, nameof(Strings.GameActionNothing)),
    ];

    public GameActionOption SelectedGameAction
    {
        get => GameActionOptions.FirstOrDefault(o => o.Action == _settings.GameGuardAction) ?? GameActionOptions[0];
        set
        {
            if (value is null || value.Action == _settings.GameGuardAction) return;
            _settings.GameGuardAction = value.Action;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool GameGuardResume
    {
        get => _settings.GameGuardResume;
        set
        {
            if (_settings.GameGuardResume == value) return;
            _settings.GameGuardResume = value;
            _settings.Save();
            if (!value)
            {
                CancelGameResume();
                _gamePaused = _gameSwitched = false;
                UpdateGuardWatch();
            }
            OnPropertyChanged();
        }
    }

    // ===================== sheet state =====================

    public GuardSheetKind GuardSheet
    {
        get => _guardSheet;
        private set
        {
            if (!Set(ref _guardSheet, value)) return;
            OnPropertyChanged(nameof(IsGameBlockedSheet));
            OnPropertyChanged(nameof(IsConflictSheet));
            OnPropertyChanged(nameof(IsGuardInfoSheet));
            OnPropertyChanged(nameof(GuardIsDanger));
            OnPropertyChanged(nameof(ShowConflictDisable));
            OnPropertyChanged(nameof(IsConflictToolSheet));
            OnPropertyChanged(nameof(IsConflictDriverSheet));
        }
    }

    public bool IsGameBlockedSheet => _guardSheet == GuardSheetKind.GameBlocked;
    public bool IsConflictSheet => _guardSheet == GuardSheetKind.Conflict;
    /// <summary>Conflict with a known DPI tool (process/service): "Close and continue" is offered.</summary>
    public bool IsConflictToolSheet => IsConflictSheet && !_conflictDriverOnly;
    /// <summary>Only a foreign WinDivert driver, no known tool behind it: nothing to close, offer the proxy instead.</summary>
    public bool IsConflictDriverSheet => IsConflictSheet && _conflictDriverOnly;

    private bool ConflictDriverOnly
    {
        get => _conflictDriverOnly;
        set
        {
            if (_conflictDriverOnly == value) return;
            _conflictDriverOnly = value;
            OnPropertyChanged(nameof(IsConflictToolSheet));
            OnPropertyChanged(nameof(IsConflictDriverSheet));
        }
    }
    public bool IsGuardInfoSheet => _guardSheet is GuardSheetKind.GamePaused or GuardSheetKind.GameSwitched or GuardSheetKind.GameNotified;
    /// <summary>Red (conflict) rather than amber (game) accent.</summary>
    public bool GuardIsDanger => _guardSheet == GuardSheetKind.Conflict;
    public string GuardTitle => _guardTitle.ToString();
    public string GuardText => _guardText.ToString();
    public string GuardHint => _guardHint.ToString();
    public bool HasGuardHint => !_guardHint.IsEmpty;

    public bool ConflictBusy
    {
        get => _conflictBusy;
        private set { if (Set(ref _conflictBusy, value)) OnPropertyChanged(nameof(ConflictIdle)); }
    }

    public bool ConflictIdle => !_conflictBusy;

    /// <summary>A tool service is installed: offer "don't start with Windows" (sets Disabled, never deletes).</summary>
    public bool ConflictCanDisable
    {
        get => _conflictCanDisable;
        private set { if (Set(ref _conflictCanDisable, value)) OnPropertyChanged(nameof(ShowConflictDisable)); }
    }

    public bool ShowConflictDisable => IsConflictSheet && _conflictCanDisable;

    public bool ConflictDisableAutostart { get => _conflictDisableAutostart; set => Set(ref _conflictDisableAutostart, value); }

    private void ShowGuardSheet(GuardSheetKind kind, LocText title, LocText text, LocText hint)
    {
        _guardTitle = title;
        _guardText = text;
        _guardHint = hint;
        OnPropertyChanged(nameof(GuardTitle));
        OnPropertyChanged(nameof(GuardText));
        OnPropertyChanged(nameof(GuardHint));
        OnPropertyChanged(nameof(HasGuardHint));
        GuardSheet = kind;
    }

    private void HideGuardSheet()
    {
        if (_conflictBusy) return;
        GuardSheet = GuardSheetKind.None;
    }

    private void ShowConflictSheet(IReadOnlyList<ConflictItem> items, bool stoppedWhileRunning, bool pendingAutoFind)
    {
        var text = DpiConflicts.Describe(items);
        _guardPendingAutoFind = pendingAutoFind;
        ConflictDisableAutostart = false;
        if (DpiConflicts.IsDriverOnly(items))
        {
            // a bare WinDivert driver of some other program: it can't be closed from here (stopping a driver in use
            // fails), so don't offer it; the proxy needs no driver
            ConflictCanDisable = false;
            ConflictDriverOnly = true;
            ShowGuardSheet(GuardSheetKind.Conflict, LocText.Of("ConflictTitle"), LocText.Of("ConflictDriverText", text), LocText.Of("ConflictDriverHint"));
            return;
        }
        ConflictDriverOnly = false;
        ConflictCanDisable = items.Any(i => i.Kind == ConflictKind.Service)
                             || (_conflictInstalledProbe?.Invoke() ?? false);
        ShowGuardSheet(GuardSheetKind.Conflict, LocText.Of("ConflictTitle"),
            LocText.Of(stoppedWhileRunning ? "ConflictStoppedText" : "ConflictText", text), LocText.Of("ConflictHint"));
    }

    /// <summary>Whether a tool service is installed (registry); replaceable so UI tests don't depend on the machine.</summary>
    private Func<bool>? _conflictInstalledProbe = () => DpiConflicts.AnyServiceInstalled([]);

    private void ShowGameSheet(GuardSheetKind kind, AntiCheatHit hit)
    {
        _guardGameName = hit.Game;
        switch (kind)
        {
            case GuardSheetKind.GameBlocked:
                ShowGuardSheet(kind, LocText.Of("GameBlockedTitle"), LocText.Of("GameBlockedText", hit.AntiCheatName), LocText.Of("SettingGameNote"));
                break;
            case GuardSheetKind.GamePaused:
                ShowGuardSheet(kind, LocText.Of("GamePausedTitle"), LocText.Of("GamePausedText", hit.Game),
                    GameGuardResume ? LocText.Of("GamePausedResumeHint") : LocText.Empty);
                break;
            case GuardSheetKind.GameSwitched:
                ShowGuardSheet(kind, LocText.Of("GameSwitchedTitle"), LocText.Of("GameSwitchedText", hit.Game),
                    GameGuardResume ? LocText.Of("GameSwitchedResumeHint") : LocText.Empty);
                break;
            default:
                ShowGuardSheet(kind, LocText.Of("GameNotifiedTitle"), LocText.Of("GameNotifiedText", hit.Game), LocText.Of("SettingGameNote"));
                break;
        }
    }

    // ===================== the gate =====================

    private bool DivertActive => _controller.Mode == EngineMode.Divert && _state is EngineState.Running or EngineState.Starting;

    /// <summary>
    /// Checks a start of the current <see cref="Mode"/> (or an auto-find). True: go ahead (the mode may have been
    /// switched to the proxy by game protection). False: held — a sheet asks the user, or (background) the configured
    /// action ran and a tray balloon tells what happened.
    /// </summary>
    private async Task<bool> GuardGateAsync(StartOrigin origin, bool autoFind, int serial)
    {
        var interactive = autoFind || (origin == StartOrigin.User && IsViewActive);
        var divert = autoFind || Mode == EngineMode.Divert;

        // 1) other DPI tools: never next to the packet engine. A foreign WinDivert driver only counts while ours is off.
        var ownDriverIdle = _state is EngineState.Stopped or EngineState.Error && !IsAutoFinding;
        var conflicts = await ConflictScanner(ownDriverIdle);
        if (serial != _intentSerial) return false;
        if (conflicts.Count > 0 && conflicts.All(c => c.Kind == ConflictKind.Driver))
        {
            // only a driver: may still be our own, stopping a moment ago — look again before blaming anyone
            await Task.Delay(1500);
            conflicts = await ConflictScanner(_state is EngineState.Stopped or EngineState.Error && !IsAutoFinding);
            if (serial != _intentSerial) return false;
        }
        if (conflicts.Count > 0)
        {
            var text = DpiConflicts.Describe(conflicts);
            if (!divert)
            {
                AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogConflictProxy(text)));
                return true;
            }
            AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogConflictHeld(text)));
            _wantRunning = false;
            StopIfDivertRunning();   // a restart (profile/DoH/mode change, reconnect) was held: don't keep running next to it
            ShowConflictSheet(conflicts, stoppedWhileRunning: false, pendingAutoFind: autoFind);
            if (!interactive) BalloonRequested?.Invoke(Strings.ConflictBalloonTitle, Strings.ConflictHeldBalloon(text));
            return false;
        }
        if (!divert || !GameGuardEnabled) return true;

        // 2) games with a kernel anti-cheat
        var snap = await _guard.ScanAsync();
        if (serial != _intentSerial) return false;
        if (snap.Game is not { } hit)
        {
            _gameOverride = false;
            _gameLogKey = _gameBalloonKey = null;
            return true;
        }
        if (_gameOverride) return true;

        if (FirstGameNotice(hit, ref _gameLogKey)) AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGameDetected(hit.Game, hit.AntiCheatName)));
        if (interactive)
        {
            _guardPendingAutoFind = autoFind;
            StopIfDivertRunning();
            ShowGameSheet(GuardSheetKind.GameBlocked, hit);
            return false;
        }
        switch (_settings.GameGuardAction)
        {
            case GameAction.Nothing:
                _gameOverride = true;   // the user chose to keep running next to games
                AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogGameIgnored(hit.Game)));
                return true;
            case GameAction.SwitchToBrowser:
                _gameSwitched = true;
                SetModeCore(EngineMode.Proxy, restart: false);
                AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGameSwitched(hit.Game)));
                if (FirstGameNotice(hit, ref _gameBalloonKey)) BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GameStartBrowserText(hit.AntiCheatName));
                UpdateGuardWatch();
                return true;
            default:
                _gamePaused = true;
                _wantRunning = false;
                StopIfDivertRunning();
                AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGameStartHeld(hit.AntiCheatName)));
                if (FirstGameNotice(hit, ref _gameBalloonKey))
                    BalloonRequested?.Invoke(Strings.GameBalloonTitle, GameGuardResume
                        ? Strings.GameStartHeldResumeText(hit.AntiCheatName)
                        : Strings.GameStartHeldText(hit.AntiCheatName));
                UpdateGuardWatch();
                return false;
        }
    }

    /// <summary>The gate held a restart while the packet engine still runs: stop it (keeps the game-resume intent).</summary>
    private void StopIfDivertRunning()
    {
        if (DivertActive) StopForGuard();
    }

    /// <summary>True the first time this game/anti-cheat is reported on this channel (until it closes): no doubled log line / balloon.</summary>
    private static bool FirstGameNotice(AntiCheatHit hit, ref string? last)
    {
        var key = hit.AntiCheatName + "|" + hit.Game;
        if (key == last) return false;
        last = key;
        return true;
    }

    // ===================== while running =====================

    /// <summary>Poll only while something can go wrong: the packet driver is (about to be) loaded, or a game pause waits.</summary>
    private void UpdateGuardWatch()
    {
        var on = DivertActive || IsAutoFinding || (GameGuardEnabled && (_gamePaused || _gameSwitched));
        _guard.SetActive(on);
    }

    private void OnGuardChanged(GuardSnapshot snap)
    {
        // a DPI tool appeared next to the running packet engine: stop now, before the line goes dark
        if (snap.HasConflict && (DivertActive || IsAutoFinding))
        {
            var items = snap.Conflicts.Split(", ").Select(n => new ConflictItem(ConflictKind.Process, n[..^4])).ToList();
            AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogConflictStopped(snap.Conflicts)));
            CancelGameResume();
            _gamePaused = false;
            var wasAutoFind = IsAutoFinding;
            if (wasAutoFind)
            {
                _autoFindWasActive = false;
                CancelAutoFind();
            }
            else
            {
                StopForGuard();
            }
            ShowConflictSheet(items, stoppedWhileRunning: true, pendingAutoFind: false);
            BalloonRequested?.Invoke(Strings.ConflictBalloonTitle, Strings.ConflictStoppedText(snap.Conflicts));
            UpdateGuardWatch();
            return;
        }

        if (snap.Game is not { } hit)
        {
            if (_gameOverride || _gamePaused || _gameSwitched) AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGameEnded));
            _gameOverride = false;
            _gameLogKey = _gameBalloonKey = null;
            if (_guardSheet == GuardSheetKind.GameBlocked) HideGuardSheet();   // the question is moot now
            if (_gamePaused || _gameSwitched)
            {
                if (GameGuardResume) StartGameResume();
                else _gamePaused = _gameSwitched = false;
            }
            UpdateGuardWatch();
            return;
        }

        CancelGameResume();   // the game came back within the quiet period
        if (!GameGuardEnabled || _gameOverride) return;

        if (IsAutoFinding)
        {
            // the probe loads the same driver: end it; reconnect after the game if the engine was on before
            _gamePaused = _autoFindWasActive;
            _autoFindWasActive = false;
            CancelAutoFind();
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGamePaused(hit.Game)));
            ShowGameSheet(GuardSheetKind.GamePaused, hit);
            BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GamePausedText(hit.Game));
            UpdateGuardWatch();
            return;
        }
        if (!DivertActive) return;

        if (FirstGameNotice(hit, ref _gameLogKey)) AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGameDetected(hit.Game, hit.AntiCheatName)));
        switch (_settings.GameGuardAction)
        {
            case GameAction.Nothing:
                _gameOverride = true;
                AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogGameIgnored(hit.Game)));
                ShowGameSheet(GuardSheetKind.GameNotified, hit);
                if (FirstGameNotice(hit, ref _gameBalloonKey)) BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GameNotifiedText(hit.Game));
                break;
            case GameAction.SwitchToBrowser:
                _gameSwitched = true;
                SetModeCore(EngineMode.Proxy, restart: false);
                _ = ConnectAsync(StartOrigin.Background);   // proxy start replaces (and unloads) the packet engine
                AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGameSwitched(hit.Game)));
                ShowGameSheet(GuardSheetKind.GameSwitched, hit);
                if (FirstGameNotice(hit, ref _gameBalloonKey)) BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GameSwitchedText(hit.Game));
                break;
            default:
                _gamePaused = true;
                StopForGuard();
                AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogGamePaused(hit.Game)));
                ShowGameSheet(GuardSheetKind.GamePaused, hit);
                if (FirstGameNotice(hit, ref _gameBalloonKey)) BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GamePausedText(hit.Game));
                break;
        }
        UpdateGuardWatch();
    }

    /// <summary>Stops the engine without the user's "off" meaning (a game pause keeps its resume intent).</summary>
    private void StopForGuard()
    {
        _intentSerial++;
        _wantRunning = false;
        _reconnectRetry?.Stop();
        _ = RunEngine(_controller.StopAsync);
    }

    private void StartGameResume()
    {
        if (_gameResumeTimer is null)
        {
            _gameResumeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _gameResumeTimer.Tick += (_, _) =>
            {
                _gameResumeTimer.Stop();
                _ = ResumeAfterGameAsync();
            };
        }
        _gameResumeTimer.Interval = GameResumeDelay;
        _gameResumeTimer.Stop();
        _gameResumeTimer.Start();
    }

    private void CancelGameResume() => _gameResumeTimer?.Stop();

    /// <summary>Game gone for the whole quiet period: back to the mode / state from before it started.</summary>
    private async Task ResumeAfterGameAsync()
    {
        var snap = await _guard.ScanAsync();
        if (snap.Game is not null || !GameGuardEnabled || !GameGuardResume) return;

        if (_guardSheet is GuardSheetKind.GamePaused or GuardSheetKind.GameSwitched or GuardSheetKind.GameNotified) HideGuardSheet();
        if (_gameSwitched)
        {
            _gameSwitched = false;
            if (Mode == EngineMode.Proxy)
            {
                var wasOn = _wantRunning || IsActive;
                SetModeCore(EngineMode.Divert, restart: false);
                if (wasOn) await ConnectAsync(StartOrigin.Background);
                BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GameRestoredText);
            }
        }
        if (_gamePaused)
        {
            _gamePaused = false;
            await ConnectAsync(StartOrigin.Background);
            if (_wantRunning) BalloonRequested?.Invoke(Strings.GameBalloonTitle, Strings.GameResumedText);
        }
        UpdateGuardWatch();
    }

    // ===================== sheet buttons =====================

    /// <summary>Game sheet: "Sadece Tarayıcı moduna geç" — a user choice, so it is kept after the game.</summary>
    public void GuardSwitchToBrowser()
    {
        HideGuardSheet();
        CancelGameResume();
        _gamePaused = false;
        SetMode(EngineMode.Proxy);
        _ = ConnectAsync(StartOrigin.User);
    }

    /// <summary>Game sheet: "Yine de başlat" — remembered until the game closes.</summary>
    public void GuardStartAnyway()
    {
        HideGuardSheet();
        CancelGameResume();
        _gameOverride = true;
        _gamePaused = false;
        AddLog(new LogEntry(DateTime.Now, "Warn", Strings.LogGameOverride(_guardGameName)));
        if (_guardPendingAutoFind) _ = StartAutoFindAsync();
        else _ = ConnectAsync(StartOrigin.User);
        UpdateGuardWatch();
    }

    /// <summary>"İptal" / "Kapat".</summary>
    public void DismissGuardSheet() => HideGuardSheet();

    /// <summary>Conflict sheet: "Kapat ve devam et" — the only path that stops/ends another program.</summary>
    public async Task ConflictCloseAndContinueAsync()
    {
        if (_guardSheet != GuardSheetKind.Conflict || _conflictBusy || _conflictDriverOnly) return;
        ConflictBusy = true;
        IReadOnlyList<ConflictItem> left;
        try
        {
            var found = await ConflictScanner(_state is EngineState.Stopped or EngineState.Error && !IsAutoFinding);
            AddLog(new LogEntry(DateTime.Now, "Info", Strings.LogConflictUserResolve(found.Count > 0 ? DpiConflicts.Describe(found) : "—")));
            left = await ConflictResolver(_conflictDisableAutostart);
            FlushLogs();
        }
        catch (Exception ex)
        {
            AddLog(new LogEntry(DateTime.Now, "Error", ex.Message));
            left = [new ConflictItem(ConflictKind.Process, "?")];
        }
        finally { ConflictBusy = false; }

        if (DpiConflicts.IsDriverOnly(left))
        {
            // the tools are gone but another program still holds the packet driver: nothing more to close here
            ShowConflictSheet(left, stoppedWhileRunning: false, pendingAutoFind: _guardPendingAutoFind);
            return;
        }
        if (left.Count > 0)
        {
            ShowGuardSheet(GuardSheetKind.Conflict, LocText.Of("ConflictTitle"),
                LocText.Of("ConflictFailedText", DpiConflicts.Describe(left)), LocText.Of("ConflictHint"));
            return;
        }
        HideGuardSheet();
        if (_guardPendingAutoFind) _ = StartAutoFindAsync();
        else _ = ConnectAsync(StartOrigin.User);
    }
}
