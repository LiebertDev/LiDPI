using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LiDPI.UI.Services;
using LiDPI.UI.ViewModels;

namespace LiDPI;

/// <summary>Main (and only) window. Pure view logic: animations, page transitions, window chrome.</summary>
public partial class MainWindow : Window
{
    private static readonly IEasingFunction EaseOut = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction PillEase = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });

    private readonly MainViewModel _vm;
    private bool _entrancePlayed;
    private bool _profilesOpen;
    private bool _settingsOpen;
    private bool _logsOpen;
    private bool _autoFindOpen;
    private bool _guardOpen;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => UpdateViewActive();
        StateChanged += (_, _) => UpdateViewActive();
        PreviewKeyDown += OnPreviewKeyDown;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Logs.CollectionChanged += OnLogsChanged;
        _vm.AutoFindRows.CollectionChanged += OnAutoFindRowsChanged;
    }

    /// <summary>Bring the window back from the tray / taskbar.</summary>
    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // nudge to the foreground without staying topmost
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ===================== window chrome =====================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.Apply(hwnd);
        // On Windows 11 DWM draws the rounded border itself (in our border colour).
        if (WindowEffects.SupportsRoundedCorners) RootBorder.BorderThickness = new Thickness(0);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // buttons mark the event handled, so this only fires on empty title-bar space
        if (e.ClickCount != 1) return;
        try { DragMove(); } catch (InvalidOperationException) { /* mouse already released */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (App.IsExiting) return;

        // To the tray: let the close go through. The window (visual tree, storyboards, bitmaps) is destroyed
        // and App recreates it from the same view model on the next show, so the tray costs almost nothing.
        if (_vm.MinimizeToTray) return;

        e.Cancel = true;
        _ = ((App)Application.Current).ExitAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        // the view model outlives this window: drop every strong reference it holds to us
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Logs.CollectionChanged -= OnLogsChanged;
        _vm.AutoFindRows.CollectionChanged -= OnAutoFindRowsChanged;
        _vm.IsViewActive = false;
        StopShimmer();
        GuardIconPulse.BeginAnimation(OpacityProperty, null);
        base.OnClosed(e);
    }

    private void UpdateViewActive() =>
        _vm.IsViewActive = IsVisible && WindowState != WindowState.Minimized;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_guardOpen) { GuardEscape(); e.Handled = true; }
        else if (_autoFindOpen) { AutoFindEscape(); e.Handled = true; }
        else if (_logsOpen) { CloseLogs(); e.Handled = true; }
        else if (_settingsOpen) { CloseSettings(); e.Handled = true; }
        else if (_profilesOpen) { SetProfilesOpen(false); e.Handled = true; }
    }

    // ===================== entrance =====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateViewActive();
        LayoutPill(animate: false);
        ApplyRunningVisuals(animate: false);
        if (_entrancePlayed) return;
        _entrancePlayed = true;

        UIElement[] items = [Hero, StatusBlock, ModeCard, ProfileCard, ErrorCard, StatsRow, Footer];
        for (var i = 0; i < items.Length; i++)
        {
            var el = items[i];
            var tt = new TranslateTransform(0, 22);
            el.RenderTransform = tt;
            el.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(60 + i * 65);
            var dur = TimeSpan.FromMilliseconds(620);
            var fade = new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = EaseOut };
            var slide = new DoubleAnimation(22, 0, dur) { BeginTime = begin, EasingFunction = EaseOut };
            fade.Completed += (_, _) =>
            {
                // hand control back to styles/triggers (e.g. ModeCard disabled opacity)
                el.BeginAnimation(OpacityProperty, null);
                el.ClearValue(OpacityProperty);
            };
            el.BeginAnimation(OpacityProperty, fade);
            tt.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // notices that became due before the window was shown appear once the entrance has settled
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(EntranceDuration);
            _entranceDone = true;
            if (_vm.IsFirstRun) ShowFirstRunPrompt();
            if (_vm.ShowHealthWarning) ShowNotice(HealthWarnCard);
            if (_pendingToast is { } t) { _pendingToast = null; ShowToast(t); }
            SyncGuardSheet();   // e.g. a conflict found by a background start while the window was closed
        });

        RestoreAutoFindSheet();
    }

    /// <summary>
    /// The window is destroyed when hidden to the tray and rebuilt from the same view model on show. An auto-find
    /// that was running (or finished but not yet dismissed) meanwhile must get its sheet back, or nothing could
    /// apply / dismiss / cancel it and every feature gated on <see cref="MainViewModel.IsAutoFinding"/> stays blocked.
    /// </summary>
    private void RestoreAutoFindSheet()
    {
        AutoFindEditSites.IsEnabled = !_vm.IsAutoFinding;
        if (_vm.AutoFindStage == AutoFindStage.Idle) return;
        OnAutoFindStageChanged();   // opens the sheet (sets _autoFindOpen) and shows the result face if finished
        if (_vm.AutoFindStage == AutoFindStage.Running) AnimateAutoFindBar();   // resume from the current progress
        if (_vm.AutoFindRows.Count > 0)
            Dispatcher.InvokeAsync(() => AutoFindScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ===================== state visuals =====================

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsProxyMode):
                LayoutPill(animate: true);
                break;
            case nameof(MainViewModel.State):
                ApplyRunningVisuals(animate: IsLoaded && IsVisible);
                break;
            case nameof(MainViewModel.AutoFindStage):
                AutoFindEditSites.IsEnabled = !_vm.IsAutoFinding;
                OnAutoFindStageChanged();
                break;
            case nameof(MainViewModel.AutoFindProgress):
                AnimateAutoFindBar();
                break;
            case nameof(MainViewModel.GuardSheet):
                if (_entranceDone) SyncGuardSheet();
                break;
            case nameof(MainViewModel.ShowHealthWarning):
                if (!_entranceDone) break;
                if (_vm.ShowHealthWarning) ShowNotice(HealthWarnCard); else HideNotice(HealthWarnCard);
                break;
        }
    }

    private void ApplyRunningVisuals(bool animate)
    {
        var running = _vm.IsRunning;
        Fade(UptimePill, running ? 1 : 0, animate, 400);
        Fade(AmbientOn, running ? 1 : 0, animate, 900);
        Fade(AmbientIdle, running ? 0 : 1, animate, 900);
    }

    private void SegmentGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutPill(animate: false);

    private void LayoutPill(bool animate)
    {
        var half = SegmentGrid.ActualWidth / 2;
        if (half <= 0) return;
        SegmentPill.Width = half;
        var x = _vm.IsProxyMode ? half : 0;
        if (!animate)
        {
            PillX.BeginAnimation(TranslateTransform.XProperty, null);
            PillX.X = x;
            return;
        }
        var a = new DoubleAnimation(x, TimeSpan.FromMilliseconds(420)) { EasingFunction = PillEase };
        PillX.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // ===================== profile selector =====================

    private void ProfileHeader_Click(object sender, RoutedEventArgs e) => SetProfilesOpen(!_profilesOpen);

    private void ProfileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // collapse shortly after a pick so the selection animation is visible
        if (!_profilesOpen) return;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(220);
            SetProfilesOpen(false);
        });
    }

    private void SetProfilesOpen(bool open)
    {
        _profilesOpen = open;
        var dur = TimeSpan.FromMilliseconds(open ? 420 : 320);

        // RevealPanel: the list inside is measured/arranged once; only the host's reported height
        // (list height x Progress) changes per frame, so nothing inside the list is re-laid out.
        var h = new DoubleAnimation(open ? 1 : 0, dur) { EasingFunction = open ? EaseOut : EaseInOut };
        h.Completed += (_, _) =>
        {
            if (open && _profilesOpen) ProfileCard.BringIntoView();
        };
        ProfileListHost.BeginAnimation(LiDPI.UI.Controls.RevealPanel.ProgressProperty, h);

        var rot = new DoubleAnimation(open ? 180 : 0, dur) { EasingFunction = EaseOut };
        ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rot);
    }

    // ===================== settings page =====================

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        if (_profilesOpen) SetProfilesOpen(false);
        // SettingsPage stays laid out while closed (Visibility=Hidden), so this costs no layout pass.
        SettingsPage.Visibility = Visibility.Visible;
        CacheWhileSliding(SettingsPage, true);
        var dur = TimeSpan.FromMilliseconds(460);
        var slide = new DoubleAnimation(ActualWidth, 0, dur) { EasingFunction = EaseOut };
        slide.Completed += (_, _) =>
        {
            if (!_settingsOpen) return;
            CacheWhileSliding(SettingsPage, false);
            // fully covered by the opaque settings page: stop rendering it and freeze its loops
            MainPage.Visibility = Visibility.Hidden;
            Power.LoopsPaused = true;
        };
        SettingsX.BeginAnimation(TranslateTransform.XProperty, slide);
        MainPageX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -70, dur) { EasingFunction = EaseOut });
        MainPage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, dur) { EasingFunction = EaseOut });
    }

    private void CloseSettings()
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        MainPage.Visibility = Visibility.Visible;
        Power.LoopsPaused = false;
        var dur = TimeSpan.FromMilliseconds(400);
        CacheWhileSliding(SettingsPage, true);
        var slide = new DoubleAnimation(ActualWidth, dur) { EasingFunction = EaseOut };
        slide.Completed += (_, _) =>
        {
            if (_settingsOpen) return;
            SettingsPage.Visibility = Visibility.Hidden;
            CacheWhileSliding(SettingsPage, false);
        };
        SettingsX.BeginAnimation(TranslateTransform.XProperty, slide);
        MainPageX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = EaseOut });
        MainPage.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = EaseOut });
    }

    // ===================== log drawer =====================

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenLogs();
    private void CloseLogs_Click(object sender, RoutedEventArgs e) => CloseLogs();
    private void LogBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseLogs();

    private void OpenLogs()
    {
        if (_logsOpen) return;
        _logsOpen = true;
        LogOverlay.Visibility = Visibility.Visible;
        CacheWhileSliding(LogSheet, true);
        var dur = TimeSpan.FromMilliseconds(420);
        LogBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        var slide = new DoubleAnimation(LogSheet.Height, 0, dur) { EasingFunction = EaseOut };
        slide.Completed += (_, _) => { if (_logsOpen) CacheWhileSliding(LogSheet, false); };
        LogSheetY.BeginAnimation(TranslateTransform.YProperty, slide);
        ScrollLogsToEnd();
    }

    private void CloseLogs()
    {
        if (!_logsOpen) return;
        _logsOpen = false;
        var dur = TimeSpan.FromMilliseconds(320);
        LogBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        CacheWhileSliding(LogSheet, true);
        var slide = new DoubleAnimation(LogSheet.Height, dur) { EasingFunction = EaseInOut };
        slide.Completed += (_, _) =>
        {
            if (_logsOpen) return;
            LogOverlay.Visibility = Visibility.Hidden;
            CacheWhileSliding(LogSheet, false);
        };
        LogSheetY.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_logsOpen && e.Action == NotifyCollectionChangedAction.Add) ScrollLogsToEnd();
    }

    private bool _scrollPending;

    /// <summary>Coalesced: at most one ScrollIntoView per dispatcher round, however many lines arrived.</summary>
    private void ScrollLogsToEnd()
    {
        if (_vm.Logs.Count == 0 || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _scrollPending = false;
            if (_vm.Logs.Count > 0) LogList.ScrollIntoView(_vm.Logs[^1]);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ===================== auto-find sheet =====================

    private static readonly IEasingFunction CreepEase = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 });
    private static readonly IEasingFunction PopEase = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });

    private void AutoFindEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanAutoFind) return;
        if (_profilesOpen) SetProfilesOpen(false);
        // the sheet opens when the run actually begins (the guard gate may ask something first)
        _ = _vm.StartAutoFindAsync();
    }

    private void AutoFindCancel_Click(object sender, RoutedEventArgs e) => _vm.CancelAutoFind();
    private void AutoFindDismiss_Click(object sender, RoutedEventArgs e) => _vm.DismissAutoFind();
    private void AutoFindApply_Click(object sender, RoutedEventArgs e) => _vm.ApplyAutoFind();
    private void AutoFindProxy_Click(object sender, RoutedEventArgs e) => _vm.AutoFindSwitchToProxy();

    // a stray click outside the sheet must not abort a running test; it only closes a finished one
    private void AutoFindBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _vm.DismissAutoFind();

    private void AutoFindEscape()
    {
        if (_vm.IsAutoFinding) _vm.CancelAutoFind();
        else _vm.DismissAutoFind();
    }

    private void OpenAutoFind()
    {
        if (_autoFindOpen) return;
        _autoFindOpen = true;

        // back to the "running" face, bar empty
        foreach (var p in new UIElement[] { AutoFindFoundPanel, AutoFindFailPanel })
        {
            p.BeginAnimation(OpacityProperty, null);
            p.Opacity = 0;
            p.Visibility = Visibility.Hidden;
        }
        AutoFindRunPanel.BeginAnimation(OpacityProperty, null);
        AutoFindRunPanel.Opacity = 1;
        AutoFindRunPanel.Visibility = Visibility.Visible;
        AutoFindBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AutoFindBarScale.ScaleX = 0;

        AutoFindOverlay.Visibility = Visibility.Visible;
        var dur = TimeSpan.FromMilliseconds(440);
        AutoFindBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        AutoFindSheetY.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(AutoFindSheet.Height, 0, dur) { EasingFunction = EaseOut });
        StartShimmer();
    }

    private void CloseAutoFind()
    {
        if (!_autoFindOpen) return;
        _autoFindOpen = false;
        var dur = TimeSpan.FromMilliseconds(320);
        AutoFindBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        var slide = new DoubleAnimation(AutoFindSheet.Height, dur) { EasingFunction = EaseInOut };
        slide.Completed += (_, _) =>
        {
            if (_autoFindOpen) return;
            AutoFindOverlay.Visibility = Visibility.Hidden;
            StopShimmer();   // no endless animation on a hidden element
        };
        AutoFindSheetY.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnAutoFindStageChanged()
    {
        switch (_vm.AutoFindStage)
        {
            case AutoFindStage.Idle:
                CloseAutoFind();
                break;
            case AutoFindStage.Running:
                OpenAutoFind();   // no-op when opened by the entry button
                break;
            case AutoFindStage.Found:
            case AutoFindStage.Failed:
                if (!_autoFindOpen) OpenAutoFind();
                ShowAutoFindResult(_vm.AutoFindStage == AutoFindStage.Found);
                break;
        }
    }

    private void ShowAutoFindResult(bool found)
    {
        StopShimmer();
        var panel = found ? AutoFindFoundPanel : AutoFindFailPanel;
        var other = found ? AutoFindFailPanel : AutoFindFoundPanel;
        var slideY = found ? AutoFindFoundY : AutoFindFailY;
        var badge = found ? FoundBadgeScale : FailBadgeScale;

        other.BeginAnimation(OpacityProperty, null);
        other.Opacity = 0;
        other.Visibility = Visibility.Hidden;

        var outFade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        outFade.Completed += (_, _) =>
        {
            if (_vm.AutoFindStage is AutoFindStage.Found or AutoFindStage.Failed) AutoFindRunPanel.Visibility = Visibility.Hidden;
        };
        AutoFindRunPanel.BeginAnimation(OpacityProperty, outFade);

        panel.Visibility = Visibility.Visible;
        var begin = TimeSpan.FromMilliseconds(140);
        var dur = TimeSpan.FromMilliseconds(460);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = EaseOut });
        slideY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, dur) { BeginTime = begin, EasingFunction = EaseOut });

        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = begin, EasingFunction = PopEase };
        badge.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        badge.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        if (found)
        {
            // one soft ring pulse around the check mark
            var ringBegin = TimeSpan.FromMilliseconds(420);
            var ringDur = TimeSpan.FromMilliseconds(950);
            var grow = new DoubleAnimation(1, 1.55, ringDur) { BeginTime = ringBegin, EasingFunction = EaseOut };
            FoundRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            FoundRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            FoundRing.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.85, 0, ringDur) { BeginTime = ringBegin, EasingFunction = EaseOut });
        }
    }

    /// <summary>
    /// Completed fraction changed: glide there, and while a variant is being tested keep creeping toward the
    /// end of its slice so the bar never looks frozen during the ~10 s a variant takes. Render-only (ScaleX).
    /// </summary>
    private void AnimateAutoFindBar()
    {
        if (!_autoFindOpen) return;
        var value = Math.Clamp(_vm.AutoFindProgress, 0, 1);
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), EaseOut));
        if (_vm.AutoFindVariantActive && value < 1)
        {
            var slice = 1.0 / Math.Max(1, _vm.AutoFindTotal);
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Min(1, value + slice * 0.85),
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(11)), CreepEase));
        }
        AutoFindBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    private void StartShimmer()
    {
        var w = AutoFindTrack.ActualWidth > 0 ? AutoFindTrack.ActualWidth : AutoFindSheet.ActualWidth - 70;
        AutoFindShimmer.Opacity = 1;
        var shimmer = new DoubleAnimation(-AutoFindShimmer.Width, w, TimeSpan.FromMilliseconds(1500))
        {
            EasingFunction = EaseInOut,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(shimmer, 60);   // endless idle loop: capped (transitions are not)
        AutoFindShimmerX.BeginAnimation(TranslateTransform.XProperty, shimmer);
    }

    private void StopShimmer()
    {
        AutoFindShimmerX.BeginAnimation(TranslateTransform.XProperty, null);
        AutoFindShimmerX.X = -AutoFindShimmer.Width;
        AutoFindShimmer.Opacity = 0;
    }

    private bool _autoFindScrollPending;

    private void OnAutoFindRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_autoFindOpen || e.Action != NotifyCollectionChangedAction.Add || _autoFindScrollPending) return;
        _autoFindScrollPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _autoFindScrollPending = false;
            AutoFindScroll.ScrollToEnd();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ===================== guard sheet (games with anti-cheat, other DPI tools) =====================

    private void GuardBrowser_Click(object sender, RoutedEventArgs e) => _vm.GuardSwitchToBrowser();
    private void GuardAnyway_Click(object sender, RoutedEventArgs e) => _vm.GuardStartAnyway();
    private void GuardDismiss_Click(object sender, RoutedEventArgs e) => _vm.DismissGuardSheet();
    private void ConflictClose_Click(object sender, RoutedEventArgs e) => _ = _vm.ConflictCloseAndContinueAsync();

    private void GuardBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // a stray click only closes the informational faces; a question needs an answer
        if (_vm.IsGuardInfoSheet) _vm.DismissGuardSheet();
    }

    private void GuardEscape()
    {
        if (!_vm.ConflictBusy) _vm.DismissGuardSheet();
    }

    /// <summary>Opens/closes the sheet to match the view model (also after the window was rebuilt from the tray).</summary>
    private void SyncGuardSheet()
    {
        if (_vm.GuardSheet == GuardSheetKind.None) CloseGuard();
        else OpenGuard();
    }

    private void OpenGuard()
    {
        if (_guardOpen)
        {
            // face changed while open (e.g. failed "close and continue"): a small nudge instead of a re-slide
            GuardContentY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = EaseOut });
            GuardContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = EaseOut });
            return;
        }
        _guardOpen = true;
        if (_profilesOpen) SetProfilesOpen(false);
        GuardOverlay.Visibility = Visibility.Visible;
        var dur = TimeSpan.FromMilliseconds(440);
        GuardBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        GuardSheetY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(GuardSlide, 0, dur) { EasingFunction = EaseOut });

        // icon pops in, then one soft ring
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = TimeSpan.FromMilliseconds(160), EasingFunction = PopEase };
        GuardIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        GuardIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        var ringBegin = TimeSpan.FromMilliseconds(420);
        var ringDur = TimeSpan.FromMilliseconds(950);
        var grow = new DoubleAnimation(1, 1.6, ringDur) { BeginTime = ringBegin, EasingFunction = EaseOut };
        GuardRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        GuardRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        GuardIconPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, 0, ringDur) { BeginTime = ringBegin, EasingFunction = EaseOut });
    }

    private void CloseGuard()
    {
        if (!_guardOpen) return;
        _guardOpen = false;
        var dur = TimeSpan.FromMilliseconds(320);
        GuardBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        var slide = new DoubleAnimation(GuardSlide, dur) { EasingFunction = EaseInOut };
        slide.Completed += (_, _) =>
        {
            if (!_guardOpen) GuardOverlay.Visibility = Visibility.Hidden;
        };
        GuardSheetY.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Slide distance: the sheet's own height (auto-sized) or a safe default before the first layout.</summary>
    private double GuardSlide => GuardSheet.ActualHeight > 0 ? GuardSheet.ActualHeight + 4 : 460;

    // ===================== notices (first run, health, toast) =====================

    /// <summary>Entrance: last item starts at 60 + 6 x 65 ms and runs 620 ms.</summary>
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(1150);
    private static readonly IEasingFunction NoticeEase = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
    private bool _entranceDone;
    private string? _pendingToast;
    private int _toastSerial;

    private void ShowFirstRunPrompt()
    {
        if (!_vm.IsFirstRun) return;
        ShowNotice(FirstRunCard);
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(620))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            EasingFunction = PopEase,
        };
        FirstRunIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        FirstRunIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void FirstRunAuto_Click(object sender, RoutedEventArgs e)
    {
        _vm.CompleteFirstRun();
        HideNotice(FirstRunCard);
        if (_vm.CanAutoFind)
        {
            _ = _vm.StartAutoFindAsync();
        }
        else
        {
            SetProfilesOpen(true);
        }
    }

    private void FirstRunManual_Click(object sender, RoutedEventArgs e)
    {
        _vm.CompleteFirstRun();
        HideNotice(FirstRunCard);
        SetProfilesOpen(true);
    }

    private void HealthAutoFind_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanAutoFind) return;
        if (_profilesOpen) SetProfilesOpen(false);
        _vm.HealthWarningAutoFind();
    }

    private void HealthReconnect_Click(object sender, RoutedEventArgs e) => _vm.HealthWarningReconnect();
    private void HealthDismiss_Click(object sender, RoutedEventArgs e) => _vm.DismissHealthWarning();

    /// <summary>Slide up + fade in. One Visibility change (single layout pass), then render-only animation.</summary>
    private static void ShowNotice(FrameworkElement el)
    {
        if (el.Visibility == Visibility.Visible && el.Tag is not "hiding") return;
        el.Tag = null;
        el.Visibility = Visibility.Visible;
        var dur = TimeSpan.FromMilliseconds(520);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = EaseOut });
        if (el.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, dur) { EasingFunction = NoticeEase });
    }

    private static void HideNotice(FrameworkElement el)
    {
        if (el.Visibility != Visibility.Visible || el.Tag is "hiding") return;
        el.Tag = "hiding";
        var dur = TimeSpan.FromMilliseconds(260);
        var fade = new DoubleAnimation(0, dur) { EasingFunction = EaseInOut };
        fade.Completed += (_, _) =>
        {
            if (el.Tag is not "hiding") return;   // re-shown meanwhile
            el.Tag = null;
            el.Visibility = Visibility.Collapsed;
        };
        el.BeginAnimation(OpacityProperty, fade);
        if (el.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, dur) { EasingFunction = EaseInOut });
    }

    private void ShowToast(string text)
    {
        if (!_entranceDone) { _pendingToast = text; return; }
        ToastText.Text = text;
        ToastText.Foreground = (Brush)FindResource("Brush.Mint");
        var serial = ++_toastSerial;
        ShowNotice(Toast);
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(3600);
            if (serial == _toastSerial) HideNotice(Toast);
        });
    }

    // ===================== settings features (update check, test sites, custom DoH, diagnostics) =====================

    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => _ = _vm.CheckForUpdatesManualAsync();

    private void CustomDohSave_Click(object sender, RoutedEventArgs e) => _vm.SaveCustomDoh();
    private void CustomDohTest_Click(object sender, RoutedEventArgs e) => _ = _vm.TestCustomDohAsync();

    private void TestSiteAdd_Click(object sender, RoutedEventArgs e) => _vm.AddTestSite();
    private void TestSitesReset_Click(object sender, RoutedEventArgs e) => _vm.ResetTestSites();

    private void TestSiteRemove_Click(object sender, RoutedEventArgs e) =>
        _vm.RemoveTestSite((sender as FrameworkElement)?.DataContext as TestSiteItem);

    private void NewTestSite_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _vm.AddTestSite();
    }

    /// <summary>Auto-find sheet "Edit": close the (finished) sheet and jump to the test-site list in Settings.</summary>
    private void AutoFindEditSites_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsAutoFinding) return;
        _vm.DismissAutoFind();
        OpenSettings();
        ScrollSettingsTo(TestSitesCaption);
    }

    /// <summary>Scrolls the settings page so that <paramref name="el"/> is near the top (after layout).</summary>
    private void ScrollSettingsTo(FrameworkElement el)
    {
        Dispatcher.InvokeAsync(() =>
        {
            DependencyObject? d = el;
            while (d is not null and not System.Windows.Controls.ScrollViewer) d = VisualTreeHelper.GetParent(d);
            if (d is not System.Windows.Controls.ScrollViewer sv) return;
            var y = el.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
            sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + y - 10));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool _diagBusy;

    /// <summary>Settings / log drawer "Report": save dialog (Desktop by default), then build + write off the UI thread.</summary>
    private async void DiagReport_Click(object sender, RoutedEventArgs e)
    {
        if (_diagBusy) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = LiDPI.UI.Strings.DiagDialogTitle,
            Filter = LiDPI.UI.Strings.DiagFileFilter,
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = DiagnosticReport.DefaultFileName(DateTime.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        _diagBusy = true;
        try
        {
            if (await _vm.SaveDiagnosticReportAsync(dlg.FileName)) ShowToast(LiDPI.UI.Strings.DiagSaved);
        }
        finally { _diagBusy = false; }
    }

    // ===================== helpers =====================

    /// <summary>
    /// While a mostly static page/sheet slides, render it once into a GPU bitmap so each frame is a textured quad
    /// instead of a full re-render of its visual tree; dropped afterwards so text stays ClearType-crisp.
    /// </summary>
    private static void CacheWhileSliding(UIElement el, bool on) =>
        el.CacheMode = on
            ? new BitmapCache { SnapsToDevicePixels = true, RenderAtScale = VisualTreeHelper.GetDpi(el).DpiScaleX }
            : null;

    private static void Fade(UIElement el, double to, bool animate, int ms)
    {
        if (!animate)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseInOut });
    }

    private static IEasingFunction Frozen(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }
}
