using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// A horizontal row: each child at its natural width, and any width beyond that shared evenly among
/// them, so a segmented switch stretched wider keeps its segments sized to their labels.
/// </summary>
public class SpreadStack : Panel
{
    private Control[] Visible() => Children.Where(c => c.IsVisible).ToArray();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (var child in Visible())
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Visible();
        if (children.Length == 0) return finalSize;
        var natural = children.Sum(c => c.DesiredSize.Width);
        var extra = Math.Max(0, finalSize.Width - natural) / children.Length;
        var x = 0.0;
        foreach (var child in children)
        {
            var w = child.DesiredSize.Width + extra;
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
