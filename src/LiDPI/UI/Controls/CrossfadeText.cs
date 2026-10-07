using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiDPI.UI.Controls;

/// <summary>Text that crossfades (with a small vertical slide) whenever <see cref="Text"/> or its brush changes.</summary>
public sealed class CrossfadeText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CrossfadeText), new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(CrossfadeText), new PropertyMetadata(Brushes.Gray, OnContentChanged));

    private TextBlock _front;
    private TextBlock _back;

    public CrossfadeText()
    {
        _front = MakeBlock();
        _back = MakeBlock();
        _back.Opacity = 0;
        Children.Add(_back);
        Children.Add(_front);
        IsHitTestVisible = false;
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private TextBlock MakeBlock()
    {
        var tb = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            RenderTransform = new TranslateTransform(),
        };
        // typography is inherited from this element via property value inheritance
        return tb;
    }

    private bool _pending;

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (CrossfadeText)d;
        if (self._pending) return;
        // Text and brush usually change together: coalesce into one crossfade.
        self._pending = true;
        self.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(self.Swap));
    }

    private void Swap()
    {
        _pending = false;
        if (_front.Text == Text && ReferenceEquals(_front.Foreground, TextBrush)) return;

        if (!IsLoaded || !IsVisible)
        {
            _front.Text = Text;
            _front.Foreground = TextBrush;
            _front.Opacity = 1;
            _back.Opacity = 0;
            return;
        }

        (_front, _back) = (_back, _front);
        _front.Text = Text;
        _front.Foreground = TextBrush;

        var dur = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        _back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
        ((TranslateTransform)_front.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, dur) { EasingFunction = ease });
        ((TranslateTransform)_back.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(200)));
        SetZIndex(_front, 1);
        SetZIndex(_back, 0);
    }
}
