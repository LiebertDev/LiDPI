using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LiDPI.Core;

namespace LiDPI.UI.Controls;

/// <summary>
/// Hero power button. Visual state follows <see cref="State"/>; the endless animations
/// (breathing glow, spinner) only run while <see cref="AnimationsEnabled"/> is true.
/// </summary>
public partial class PowerButton : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(EngineState), typeof(PowerButton),
        new PropertyMetadata(EngineState.Stopped, (d, _) => ((PowerButton)d).ApplyState(animate: true)));

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(
        nameof(AnimationsEnabled), typeof(bool), typeof(PowerButton),
        new PropertyMetadata(true, (d, _) => ((PowerButton)d).UpdateLoops()));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(PowerButton), new PropertyMetadata(null));

    private static readonly Duration Fade = TimeSpan.FromMilliseconds(380);
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static readonly TimeSpan BreathPeriod = TimeSpan.FromSeconds(2.5);
    private static readonly DoubleAnimation BreathOpacity = Frozen(new DoubleAnimation(0.55, 1.0, BreathPeriod)
    {
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseInOut,
    });
    private static readonly DoubleAnimation BreathScale = Frozen(new DoubleAnimation(0.95, 1.04, BreathPeriod)
    {
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseInOut,
    });
    private static readonly DoubleAnimation Spin = Frozen(new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
    {
        RepeatBehavior = RepeatBehavior.Forever,
    });

    private bool _breathing;
    private bool _spinning;
    private bool _paused;
    private AnimationClock? _introClock, _breathOpClock, _breathScaleClock, _spinClock;

    public PowerButton()
    {
        InitializeComponent();
        Btn.SetBinding(Button.CommandProperty, new System.Windows.Data.Binding(nameof(Command)) { Source = this });
        Btn.MouseEnter += (_, _) => ScaleBody(1.04, 260);
        Btn.MouseLeave += (_, _) => ScaleBody(1.0, 320);
        Btn.PreviewMouseLeftButtonDown += (_, _) => ScaleBody(0.96, 120);
        Btn.PreviewMouseLeftButtonUp += (_, _) => ScaleBody(Btn.IsMouseOver ? 1.04 : 1.0, 260);
        Btn.Click += (_, _) => PlayRipple();
        Loaded += (_, _) => { ApplyState(animate: false); UpdateLoops(); };
        Unloaded += (_, _) => StopLoops();
    }

    public EngineState State
    {
        get => (EngineState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool AnimationsEnabled
    {
        get => (bool)GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private void ApplyState(bool animate)
    {
        var s = State;
        var running = s == EngineState.Running;
        var busy = s is EngineState.Starting or EngineState.Stopping;
        var error = s == EngineState.Error;

        To(RingOn, running ? 1 : 0, animate);
        To(RingError, error ? 0.9 : 0, animate);
        To(RingTrack, running ? 0 : 1, animate);
        To(Spinner, busy ? 1 : 0, animate);
        To(CoreTintLayer, running ? 1 : 0, animate);
        To(IconOff, s == EngineState.Stopped ? 1 : 0, animate);
        To(IconBusy, busy ? 1 : 0, animate);
        To(IconOn, running ? 1 : 0, animate);
        To(IconError, error ? 1 : 0, animate);

        if (!running) To(Glow, 0, animate);
        UpdateLoops();
    }

    /// <summary>
    /// Freeze the endless animations in place (clocks paused, current values held, no frames requested)
    /// while the button is covered, e.g. by the settings page. Unlike stopping, resuming causes no visual jump.
    /// </summary>
    public bool LoopsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            foreach (var c in new[] { _introClock, _breathOpClock, _breathScaleClock, _spinClock })
                SetPaused(c, value);
        }
    }

    private static void SetPaused(AnimationClock? c, bool paused)
    {
        if (c?.Controller is not { } ctl) return;
        if (paused) ctl.Pause(); else ctl.Resume();
    }

    private AnimationClock Start(Animatable target, DependencyProperty dp, AnimationTimeline anim)
    {
        var clock = anim.CreateClock();
        target.ApplyAnimationClock(dp, clock);
        if (_paused) SetPaused(clock, true);
        return clock;
    }

    private AnimationClock Start(UIElement target, DependencyProperty dp, AnimationTimeline anim)
    {
        var clock = anim.CreateClock();
        target.ApplyAnimationClock(dp, clock);
        if (_paused) SetPaused(clock, true);
        return clock;
    }

    /// <summary>Start/stop the endless animations so a hidden window costs no CPU/GPU.</summary>
    private void UpdateLoops()
    {
        var enabled = AnimationsEnabled && IsLoaded;
        var wantBreath = enabled && State == EngineState.Running;
        var wantSpin = enabled && State is EngineState.Starting or EngineState.Stopping;

        if (wantBreath && !_breathing)
        {
            _breathing = true;
            // fade in smoothly first, then breathe (only Opacity / ScaleTransform animate: compositor-cheap)
            var intro = new DoubleAnimation(0.55, TimeSpan.FromMilliseconds(500)) { EasingFunction = EaseOut };
            intro.Freeze();
            var introClock = intro.CreateClock();
            introClock.Completed += (_, _) =>
            {
                if (!_breathing || !ReferenceEquals(_introClock, introClock)) return;
                _introClock = null;
                _breathOpClock = Start(Glow, OpacityProperty, BreathOpacity);
            };
            _introClock = introClock;
            Glow.ApplyAnimationClock(OpacityProperty, introClock);
            if (_paused) SetPaused(introClock, true);

            _breathScaleClock = BreathScale.CreateClock();
            GlowScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _breathScaleClock);
            GlowScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, _breathScaleClock);
            if (_paused) SetPaused(_breathScaleClock, true);
        }
        else if (!wantBreath && _breathing)
        {
            _breathing = false;
            _breathScaleClock = null;
            GlowScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            GlowScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            if (State == EngineState.Running)
            {
                // hidden while running: freeze glow at a static level (no clock running)
                _introClock = null;
                _breathOpClock = null;
                Glow.ApplyAnimationClock(OpacityProperty, null);
                Glow.Opacity = 0.8;
            }
            else
            {
                // ApplyState already started the fade-out on Glow.Opacity; just drop our references
                _introClock = null;
                _breathOpClock = null;
            }
        }

        if (wantSpin && !_spinning)
        {
            _spinning = true;
            _spinClock = Start(SpinnerRotate, RotateTransform.AngleProperty, Spin);
        }
        else if (!wantSpin && _spinning)
        {
            _spinning = false;
            _spinClock = null;
            SpinnerRotate.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        }
    }

    private void StopLoops()
    {
        _breathing = false;
        _spinning = false;
        _introClock = _breathOpClock = _breathScaleClock = _spinClock = null;
        Glow.ApplyAnimationClock(OpacityProperty, null);
        GlowScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
        GlowScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
        SpinnerRotate.ApplyAnimationClock(RotateTransform.AngleProperty, null);
    }

    private void ScaleBody(double to, int ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        a.Freeze();
        BodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        BodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void PlayRipple()
    {
        if (!AnimationsEnabled) return;
        var dur = TimeSpan.FromMilliseconds(700);
        var scale = new DoubleAnimation(0.86, 1.22, dur) { EasingFunction = EaseOut };
        var op = new DoubleAnimation(0.7, 0, dur) { EasingFunction = EaseOut };
        scale.Freeze(); op.Freeze();
        RippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        Ripple.BeginAnimation(OpacityProperty, op);
    }

    private static void To(UIElement el, double value, bool animate)
    {
        if (!animate)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = value;
            return;
        }
        var a = new DoubleAnimation(value, Fade) { EasingFunction = EaseOut };
        a.Freeze();
        el.BeginAnimation(OpacityProperty, a);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
