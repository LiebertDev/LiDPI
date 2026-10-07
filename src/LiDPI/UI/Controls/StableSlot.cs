using System.Windows;
using System.Windows.Controls;

namespace LiDPI.UI.Controls;

/// <summary>
/// Layout firewall for frequently changing content (counting numbers): reports zero width (it is
/// stretched by its parent) and the tallest height seen so far, so the child's per-frame size changes never invalidate the parent's
/// layout. The child is still arranged in the full slot, so rendering is unchanged.
/// </summary>
public sealed class StableSlot : Decorator
{
    private double _height;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } c) return default;
        c.Measure(constraint);
        _height = Math.Max(_height, c.DesiredSize.Height);
        // width 0: the slot asks for nothing horizontally (it is stretched to the column anyway),
        // so neither text width nor layout rounding can ever push the card wider than its column
        return new Size(0, _height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
