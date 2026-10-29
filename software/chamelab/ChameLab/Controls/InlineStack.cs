using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// Two children side by side when both fit their natural width on one line, the second taking the rest of
/// the line; otherwise the second goes under the first, both the full width.
/// </summary>
public class InlineStack : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<InlineStack, double>(nameof(Spacing), 12);

    static InlineStack()
    {
        AffectsMeasure<InlineStack>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private bool _inline;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
        {
            foreach (var c in Children) c.Measure(availableSize);
            return Children.Count == 0 ? default : Children[0].DesiredSize;
        }
        var (first, second) = (Children[0], Children[1]);
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        first.Measure(unbounded);
        second.Measure(unbounded);
        var natural = first.DesiredSize.Width + Spacing + second.DesiredSize.Width;
        _inline = natural <= availableSize.Width;
        if (_inline)
        {
            second.Measure(new Size(availableSize.Width - first.DesiredSize.Width - Spacing, availableSize.Height));
            return new Size(Math.Min(natural, availableSize.Width),
                Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
        }
        first.Measure(availableSize);
        second.Measure(availableSize);
        return new Size(Math.Max(first.DesiredSize.Width, second.DesiredSize.Width),
            first.DesiredSize.Height + second.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            foreach (var c in Children) c.Arrange(new Rect(finalSize));
            return finalSize;
        }
        var (first, second) = (Children[0], Children[1]);
        if (_inline)
        {
            var w = first.DesiredSize.Width;
            first.Arrange(new Rect(0, 0, w, finalSize.Height));
            second.Arrange(new Rect(w + Spacing, 0, Math.Max(0, finalSize.Width - w - Spacing), finalSize.Height));
        }
        else
        {
            first.Arrange(new Rect(0, 0, finalSize.Width, first.DesiredSize.Height));
            second.Arrange(new Rect(0, first.DesiredSize.Height, finalSize.Width, finalSize.Height - first.DesiredSize.Height));
        }
        return finalSize;
    }
}
