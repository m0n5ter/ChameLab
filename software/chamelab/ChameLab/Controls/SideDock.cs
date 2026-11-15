using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// Two children on one line: the first fills, the second (usually an <see cref="ActionRow"/>) sits on the right at
/// its natural width. The second is measured with the width left after the first's MinWidth, so its buttons drop
/// their labels before the first gets narrower than that.
/// </summary>
public class SideDock : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SideDock, double>(nameof(Spacing), 8);

    static SideDock() => AffectsMeasure<SideDock>(SpacingProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private (Control? Fill, Control? Side) Parts() =>
        (Children.Count > 0 && Children[0].IsVisible ? Children[0] : null,
         Children.Count > 1 && Children[1].IsVisible ? Children[1] : null);

    protected override Size MeasureOverride(Size availableSize)
    {
        var (fill, side) = Parts();
        var width = availableSize.Width;
        var sideWidth = 0.0;
        var height = 0.0;
        if (side is not null)
        {
            var reserve = fill is null ? 0 : fill.MinWidth + Spacing;
            side.Measure(new Size(Math.Max(0, width - reserve), availableSize.Height));
            sideWidth = side.DesiredSize.Width;
            height = side.DesiredSize.Height;
        }
        if (fill is not null)
        {
            var gap = side is null ? 0 : Spacing;
            fill.Measure(new Size(Math.Max(0, width - sideWidth - gap), availableSize.Height));
            height = Math.Max(height, fill.DesiredSize.Height);
            sideWidth += fill.DesiredSize.Width + gap;
        }
        return new Size(double.IsInfinity(width) ? sideWidth : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (fill, side) = Parts();
        var sideWidth = side is null ? 0 : Math.Min(side.DesiredSize.Width, finalSize.Width);
        side?.Arrange(new Rect(finalSize.Width - sideWidth, 0, sideWidth, finalSize.Height));
        var gap = side is null ? 0 : Spacing;
        fill?.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - sideWidth - gap), finalSize.Height));
        return finalSize;
    }
}
