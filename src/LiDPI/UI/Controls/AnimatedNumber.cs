using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using LiDPI.Localization;
using LiDPI.UI;

namespace LiDPI.UI.Controls;

/// <summary>TextBlock that counts smoothly to its <see cref="Value"/> and abbreviates large numbers in the UI language (1,2 B / 3.4K).</summary>
public sealed class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(long), typeof(AnimatedNumber), new PropertyMetadata(0L, OnValueChanged));

    private static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        "Display", typeof(double), typeof(AnimatedNumber), new PropertyMetadata(0d, OnDisplayChanged));

    public AnimatedNumber()
    {
        Text = "0";
        // re-format after a language switch (separator and suffixes); only while in the visual tree, so no leak
        Loaded += (_, _) => Loc.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Text = Format((long)Math.Round((double)GetValue(DisplayProperty)));

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

    public static string Format(long v)
    {
        var c = Loc.Instance.Culture;
        return v switch
        {
            >= 1_000_000_000 => Strings.NumBillion((v / 1_000_000_000d).ToString("0.#", c)),
            >= 1_000_000 => Strings.NumMillion((v / 1_000_000d).ToString("0.#", c)),
            >= 10_000 => Strings.NumThousand((v / 1_000d).ToString("0.#", c)),
            _ => v.ToString("N0", c),
        };
    }
}
