using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// A title followed by an <see cref="ActionRow"/>, kept right-aligned. The title keeps its natural
/// width while the buttons drop their labels to make room; only when they are all icons does the
/// title get narrower (trimming its text).
/// </summary>
public class TitleRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TitleRow, double>(nameof(Spacing), 12);

    static TitleRow()
    {
        AffectsMeasure<TitleRow>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2 || Children[1] is not ActionRow actions)
        {
            foreach (var c in Children) c.Measure(availableSize);
            return Children.Count == 0 ? default : Children[0].DesiredSize;
        }
        var title = Children[0];
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        title.Measure(unbounded);
        actions.Measure(unbounded);

        var spacing = actions.IsVisible && actions.DesiredSize.Width > 0 ? Spacing : 0;
        var room = availableSize.Width - title.DesiredSize.Width - spacing;
        actions.Measure(new Size(Math.Max(room, actions.IconicWidth), availableSize.Height));
        title.Measure(new Size(Math.Max(0, availableSize.Width - actions.DesiredSize.Width - spacing),
            availableSize.Height));

        return new Size(
            Math.Min(availableSize.Width, title.DesiredSize.Width + spacing + actions.DesiredSize.Width),
            Math.Max(title.DesiredSize.Height, actions.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2 || Children[1] is not ActionRow actions)
        {
            foreach (var c in Children) c.Arrange(new Rect(finalSize));
            return finalSize;
        }
        // The buttons at their own height, centered beside the title.
        var (width, height) = (actions.DesiredSize.Width, actions.DesiredSize.Height);
        var spacing = width > 0 ? Spacing : 0;
        Children[0].Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - width - spacing), finalSize.Height));
        actions.Arrange(new Rect(finalSize.Width - width, (finalSize.Height - height) / 2, width, height));
        return finalSize;
    }
}
