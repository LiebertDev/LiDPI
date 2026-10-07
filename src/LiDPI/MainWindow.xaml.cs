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

        e.Cancel = true;
        if (_vm.MinimizeToTray)
        {
            Hide();
        }
        else
        {
            _ = ((App)Application.Current).ExitAsync();
        }
    }

    private void UpdateViewActive() =>
        _vm.IsViewActive = IsVisible && WindowState != WindowState.Minimized;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_logsOpen) { CloseLogs(); e.Handled = true; }
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
        var dur = TimeSpan.FromMilliseconds(460);
        var slide = new DoubleAnimation(ActualWidth, 0, dur) { EasingFunction = EaseOut };
        slide.Completed += (_, _) =>
        {
            if (!_settingsOpen) return;
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
        var slide = new DoubleAnimation(ActualWidth, dur) { EasingFunction = EaseOut };
        slide.Completed += (_, _) => { if (!_settingsOpen) SettingsPage.Visibility = Visibility.Hidden; };
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
        var dur = TimeSpan.FromMilliseconds(420);
        LogBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        LogSheetY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(LogSheet.Height, 0, dur) { EasingFunction = EaseOut });
        ScrollLogsToEnd();
    }

    private void CloseLogs()
    {
        if (!_logsOpen) return;
        _logsOpen = false;
        var dur = TimeSpan.FromMilliseconds(320);
        LogBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        var slide = new DoubleAnimation(LogSheet.Height, dur) { EasingFunction = EaseInOut };
        slide.Completed += (_, _) => { if (!_logsOpen) LogOverlay.Visibility = Visibility.Hidden; };
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

    // ===================== helpers =====================

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
