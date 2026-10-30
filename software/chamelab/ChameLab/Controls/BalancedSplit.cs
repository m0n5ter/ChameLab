using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ChameLab.Controls;

/// <summary>
/// Three rows: top and bottom share the height left after the middle (natural height). Half each when both
/// need more; when one needs less, the other takes what it leaves, and spare room is shared evenly.
/// Under an unbounded height (a ScrollViewer) MinHeight stands in for the height to share.
/// </summary>
public class BalancedSplit : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<BalancedSplit, double>(nameof(Spacing), 12);

    static BalancedSplit()
    {
        AffectsMeasure<BalancedSplit>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private double _topH, _middleH, _bottomH;

    private Layoutable? Row(int i) => i < Children.Count && Children[i].IsVisible ? Children[i] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (top, middle, bottom) = (Row(0), Row(1), Row(2));
        var width = availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? MinHeight : availableSize.Height;
        var natural = new Size(width, double.PositiveInfinity);

        middle?.Measure(natural);
        _middleH = middle?.DesiredSize.Height ?? 0;
        var gaps = Spacing * ((top is null ? 0 : 1) + (middle is null ? 0 : 1) + (bottom is null ? 0 : 1) - 1);
        var rest = Math.Max(0, height - _middleH - Math.Max(0, gaps));

        if (top is not null && bottom is not null)
        {
            top.Measure(natural);
            bottom.Measure(natural);
            // Each first gets what it needs, up to half; what's left goes to the one still short of its need,
            // and any space past both needs is shared evenly. Continuous, so the boundary never jumps.
            var (topNeed, bottomNeed, half) = (top.DesiredSize.Height, bottom.DesiredSize.Height, rest / 2);
            _topH = Math.Min(topNeed, half);
            var bottomH = Math.Min(bottomNeed, half);
            var left = rest - _topH - bottomH;
            var topShort = Math.Min(topNeed - _topH, left);
            var bottomShort = Math.Min(bottomNeed - bottomH, left);
            _topH += topShort + (left - topShort - bottomShort) / 2;
            // Never below a row's MinHeight; past them the panel outgrows the height and the scroller scrolls.
            _topH = Math.Max(_topH, top.MinHeight);
            _bottomH = Math.Max(rest - _topH, bottom.MinHeight);
        }
        else (_topH, _bottomH) = top is not null ? (Math.Max(rest, top.MinHeight), 0d)
            : (0d, Math.Max(rest, bottom?.MinHeight ?? 0));

        top?.Measure(new Size(width, _topH));
        bottom?.Measure(new Size(width, _bottomH));

        var desiredW = Math.Max(top?.DesiredSize.Width ?? 0,
            Math.Max(middle?.DesiredSize.Width ?? 0, bottom?.DesiredSize.Width ?? 0));
        return new Size(desiredW, _topH + _middleH + _bottomH + Math.Max(0, gaps));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var (row, h) in new[] { (Row(0), _topH), (Row(1), _middleH), (Row(2), _bottomH) })
        {
            if (row is null) continue;
            row.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h + Spacing;
        }
        return finalSize;
    }
}
