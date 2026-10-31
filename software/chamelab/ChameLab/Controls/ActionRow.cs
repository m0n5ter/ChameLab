using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// Buttons kept on one line, left-aligned at their natural size. When they don't fit, buttons
/// get the <c>iconic</c> class one at a time (styles hide their label, leaving the icon) until
/// the row fits: lowest <see cref="PriorityProperty"/> first, and among equals the last button
/// first. Labels come back as soon as there is room again.
/// </summary>
public class ActionRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ActionRow, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<bool> WrapProperty =
        AvaloniaProperty.Register<ActionRow, bool>(nameof(Wrap));

    /// <summary>Higher keeps its label longer when the row runs out of room.</summary>
    public static readonly AttachedProperty<int> PriorityProperty =
        AvaloniaProperty.RegisterAttached<ActionRow, Control, int>("Priority");

    public const string IconicClass = "iconic";

    static ActionRow()
    {
        AffectsMeasure<ActionRow>(SpacingProperty, WrapProperty);
        PriorityProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as ActionRow)?.InvalidateMeasure());
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
    /// <summary>Wrap only when even all icon buttons cannot fit on one line.</summary>
    public bool Wrap { get => GetValue(WrapProperty); set => SetValue(WrapProperty, value); }

    public static int GetPriority(Control control) => control.GetValue(PriorityProperty);
    public static void SetPriority(Control control, int value) => control.SetValue(PriorityProperty, value);

    /// <summary>The row's width with every label hidden, as of the last measure.</summary>
    public double IconicWidth { get; private set; }

    private List<Control> Visible() => Children.Where(c => c.IsVisible).ToList();

    /// <summary>Visible buttons in the order they lose their label.</summary>
    private static List<Control> CollapseOrder(List<Control> visible) => visible
        .Select((c, i) => (Control: c, Index: i))
        .OrderBy(x => GetPriority(x.Control))
        .ThenByDescending(x => x.Index)
        .Select(x => x.Control)
        .ToList();

    /// <remarks>
    /// Every pass measures each button both with and without its label, so the choice depends
    /// only on the width offered: no state carried between passes, nothing to oscillate.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Hidden buttons keep their label, so they come back whole.
        foreach (var child in Children.Where(c => !c.IsVisible)) SetIconic(child, false);

        var order = CollapseOrder(Visible());
        var full = order.Select(c => WidthOf(c, iconic: false)).ToArray();
        var icon = order.Select(c => WidthOf(c, iconic: true)).ToArray();

        var limit = availableSize.Width;
        var gaps = Math.Max(0, order.Count - 1) * Spacing;
        IconicWidth = icon.Sum() + gaps;
        var width = full.Sum() + gaps;
        var collapsed = 0;
        while (width > limit && collapsed < order.Count)
        {
            width += icon[collapsed] - full[collapsed];
            collapsed++;
        }

        var height = 0.0;
        for (var i = 0; i < order.Count; i++)
        {
            SetIconic(order[i], i < collapsed);
            order[i].Measure(Size.Infinity);
            height = Math.Max(height, order[i].DesiredSize.Height);
        }
        return new Size(Math.Min(width, limit), Wrap ? LayoutRows(limit, arrange: false) : height);
    }

    private static double WidthOf(Control child, bool iconic)
    {
        SetIconic(child, iconic);
        child.Measure(Size.Infinity);
        return child.DesiredSize.Width;
    }

    /// <summary>
    /// Switches the label off or on. The style change hides a nested TextBlock, which does not
    /// invalidate the button's cached size by the time it is measured again, so the whole
    /// subtree is invalidated here.
    /// </summary>
    private static void SetIconic(Control child, bool iconic)
    {
        if (child.Classes.Contains(IconicClass) == iconic) return;
        child.Classes.Set(IconicClass, iconic);
        foreach (var layoutable in child.GetSelfAndVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Wrap)
        {
            LayoutRows(finalSize.Width, arrange: true);
            return finalSize;
        }
        // Stretched buttons take the row's height, so icon-only ones match their labeled neighbors.
        var x = 0.0;
        foreach (var child in Visible())
        {
            var size = child.DesiredSize;
            var height = child.VerticalAlignment == VerticalAlignment.Stretch ? finalSize.Height : size.Height;
            child.Arrange(new Rect(x, (finalSize.Height - height) / 2, size.Width, height));
            x += size.Width + Spacing;
        }
        return finalSize;
    }

    private double LayoutRows(double limit, bool arrange)
    {
        var children = Visible();
        double y = 0;
        for (var start = 0; start < children.Count;)
        {
            var end = start + 1;
            var width = children[start].DesiredSize.Width;
            var height = children[start].DesiredSize.Height;
            while (end < children.Count && width + Spacing + children[end].DesiredSize.Width <= limit)
            {
                width += Spacing + children[end].DesiredSize.Width;
                height = Math.Max(height, children[end].DesiredSize.Height);
                end++;
            }
            double x = 0;
            for (var i = start; i < end; i++)
            {
                var size = children[i].DesiredSize;
                if (arrange) children[i].Arrange(new Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
            }
            y += height + Spacing;
            start = end;
        }
        return children.Count == 0 ? 0 : y - Spacing;
    }
}
