using System.Windows;
using System.Windows.Controls;

namespace LiDPI.UI.Controls;

/// <summary>
/// Expander host: reports <c>child height × <see cref="Progress"/></c> to its parent while the child
/// is always measured with unbounded height and arranged at its full size. Animating Progress therefore
/// re-measures nothing inside the child (its measure/arrange stay cached); only this panel's own
/// desired height changes. Clip with ClipToBounds.
/// </summary>
public sealed class RevealPanel : Decorator
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RevealPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } c) return default;
        c.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var p = Math.Clamp(Progress, 0, 1);
        return new Size(c.DesiredSize.Width, c.DesiredSize.Height * p);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is { } c)
            c.Arrange(new Rect(0, 0, arrangeSize.Width, c.DesiredSize.Height));
        return arrangeSize;
    }
}
