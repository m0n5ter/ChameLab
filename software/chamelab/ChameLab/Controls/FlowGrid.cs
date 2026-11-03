using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// Lays children out left to right at their natural width, wrapping to a new row when the
/// next one does not fit. The spare width of each row is shared between its children in
/// proportion to their natural widths, so every row spans the panel. A child with an
/// <see cref="ItemWidthProperty">ItemWidth</see> keeps that width while the row has other
/// children to take the spare. Each row is as tall as its tallest child.
/// </summary>
public class FlowGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<FlowGrid, double>(nameof(MinItemWidth), 80);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FlowGrid, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FlowGrid, double>(nameof(RowSpacing));

    /// <summary>Fixed width of a child, e.g. a picker that must not move as its neighbor grows; NaN for its natural width.</summary>
    public static readonly AttachedProperty<double> ItemWidthProperty =
        AvaloniaProperty.RegisterAttached<FlowGrid, Control, double>("ItemWidth", double.NaN);

    public static double GetItemWidth(Control element) => element.GetValue(ItemWidthProperty);
    public static void SetItemWidth(Control element, double value) => element.SetValue(ItemWidthProperty, value);

    static FlowGrid()
    {
        AffectsMeasure<FlowGrid>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
        AffectsParentMeasure<FlowGrid>(ItemWidthProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    // Rects computed by the last measure, reused by arrange.
    private readonly List<(Control Child, Rect Rect)> _layout = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout.Clear();
        var limit = availableSize.Width;
        var unbounded = double.IsInfinity(limit);

        // Natural width of each visible child, never below the minimum nor above the panel.
        var items = new List<(Control Child, double Width, bool Fixed)>();
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(limit, double.PositiveInfinity));
            var itemWidth = GetItemWidth(child);
            var isFixed = !double.IsNaN(itemWidth);
            var w = isFixed ? itemWidth : Math.Max(MinItemWidth, child.DesiredSize.Width);
            if (!unbounded) w = Math.Min(w, limit);
            items.Add((child, w, isFixed));
        }

        double y = 0, widest = 0;
        var start = 0;
        while (start < items.Count)
        {
            // Greedily fill the row.
            var end = start;
            var used = items[start].Width;
            while (end + 1 < items.Count && (unbounded || used + ColumnSpacing + items[end + 1].Width <= limit))
            {
                end++;
                used += ColumnSpacing + items[end].Width;
            }

            // The spare goes to the flexible children; only a row of fixed ones stretches them all.
            var natural = used - (end - start) * ColumnSpacing;
            double flexible = 0;
            for (var i = start; i <= end; i++)
                if (!items[i].Fixed) flexible += items[i].Width;
            var spare = unbounded ? 0 : limit - used;
            var scale = flexible > 0 ? (flexible + spare) / flexible : natural > 0 ? (natural + spare) / natural : 1;

            // Re-measure at the final width: wrapped text may now fit on fewer lines.
            double x = 0, rowH = 0;
            var rowStart = _layout.Count;
            for (var i = start; i <= end; i++)
            {
                var w = items[i].Fixed && flexible > 0 ? items[i].Width : items[i].Width * scale;
                var child = items[i].Child;
                child.Measure(new Size(w, double.PositiveInfinity));
                rowH = Math.Max(rowH, child.DesiredSize.Height);
                _layout.Add((child, new Rect(x, y, w, 0)));
                x += w + ColumnSpacing;
            }
            for (var i = rowStart; i < _layout.Count; i++)
                _layout[i] = (_layout[i].Child, _layout[i].Rect.WithHeight(rowH));

            widest = Math.Max(widest, x - ColumnSpacing);
            y += rowH + RowSpacing;
            start = end + 1;
        }

        var height = items.Count == 0 ? 0 : y - RowSpacing;
        return new Size(unbounded ? widest : limit, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, rect) in _layout)
            child.Arrange(rect);
        return finalSize;
    }
}
