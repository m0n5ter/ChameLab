using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// Equal columns, like a UniformGrid with <see cref="Columns"/> columns, while every child fits
/// its column at its natural width; otherwise a single column, one child per row. The choice
/// depends only on the children's natural widths and the width offered, so it cannot oscillate.
/// Each row is as tall as its tallest child.
/// </summary>
public class FitGrid : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<FitGrid, int>(nameof(Columns), 2);
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FitGrid, double>(nameof(ColumnSpacing));
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FitGrid, double>(nameof(RowSpacing));
    /// <summary>
    /// When set, the narrowest a column may get, instead of the children's natural widths
    /// (which wrapping text makes as wide as its whole line).
    /// </summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<FitGrid, double>(nameof(MinColumnWidth));

    static FitGrid()
    {
        AffectsMeasure<FitGrid>(ColumnsProperty, ColumnSpacingProperty, RowSpacingProperty, MinColumnWidthProperty);
    }

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    // Decided by the last measure, reused by arrange.
    private int _columns = 1;

    private Control[] Visible() => Children.Where(c => c.IsVisible).ToArray();

    private double ItemWidth(double width, int columns) =>
        Math.Max(0, (width - (columns - 1) * ColumnSpacing) / columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Visible();
        var widest = 0.0;
        foreach (var child in children)
        {
            child.Measure(Size.Infinity);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        var width = availableSize.Width;
        var columns = Math.Max(1, Math.Min(Columns, children.Length));
        var needed = MinColumnWidth > 0 ? MinColumnWidth : widest;
        if (!double.IsInfinity(width) && ItemWidth(width, columns) < needed) columns = 1;
        _columns = columns;

        var itemWidth = double.IsInfinity(width) ? double.PositiveInfinity : ItemWidth(width, columns);
        double height = 0, rowHeight = 0;
        for (var i = 0; i < children.Length; i++)
        {
            children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            if ((i + 1) % columns == 0 || i == children.Length - 1)
            {
                height += rowHeight + (i == children.Length - 1 ? 0 : RowSpacing);
                rowHeight = 0;
            }
        }
        return new Size(double.IsInfinity(width) ? widest * columns + (columns - 1) * ColumnSpacing : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Visible();
        var columns = _columns;
        var itemWidth = ItemWidth(finalSize.Width, columns);
        var y = 0.0;
        for (var start = 0; start < children.Length; start += columns)
        {
            var end = Math.Min(start + columns, children.Length);
            var rowHeight = 0.0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
                children[i].Arrange(new Rect((i - start) * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
            y += rowHeight + RowSpacing;
        }
        return finalSize;
    }
}
