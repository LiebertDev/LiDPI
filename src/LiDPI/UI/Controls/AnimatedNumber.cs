using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace LiDPI.UI.Controls;

/// <summary>TextBlock that counts smoothly to its <see cref="Value"/> and abbreviates large numbers (1,2 B / 3,4 Mn).</summary>
public sealed class AnimatedNumber : TextBlock
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(long), typeof(AnimatedNumber), new PropertyMetadata(0L, OnValueChanged));

    private static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        "Display", typeof(double), typeof(AnimatedNumber), new PropertyMetadata(0d, OnDisplayChanged));

    public AnimatedNumber() => Text = "0";

    public long Value
    {
        get => (long)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AnimatedNumber)d;
        var to = (double)(long)e.NewValue;
        var from = (double)self.GetValue(DisplayProperty);
        if (to < from || !self.IsVisible)
        {
            // resets (and hidden updates) jump straight to the value
            self.BeginAnimation(DisplayProperty, null);
            self.SetValue(DisplayProperty, to);
            return;
        }
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Freeze();
        self.BeginAnimation(DisplayProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AnimatedNumber)d;
        var text = Format((long)Math.Round((double)e.NewValue));
        if (self.Text != text) self.Text = text;
    }

    public static string Format(long v) => v switch
    {
        >= 1_000_000_000 => (v / 1_000_000_000d).ToString("0.#", Tr) + " Mr",
        >= 1_000_000 => (v / 1_000_000d).ToString("0.#", Tr) + " Mn",
        >= 10_000 => (v / 1_000d).ToString("0.#", Tr) + " B",
        _ => v.ToString("N0", Tr),
    };
}
