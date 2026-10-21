using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// Lays children out in as many equal columns as fit while each stays at least
/// <see cref="MinItemWidth"/> wide; a wide list of cards turns into a grid of tiles.
/// Each row is as tall as its tallest child.
/// </summary>
public class TileGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<TileGrid, double>(nameof(MinItemWidth), 300);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<TileGrid, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<TileGrid, double>(nameof(RowSpacing));

    static TileGrid()
    {
        AffectsMeasure<TileGrid>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
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

    private int Columns(double width) =>
        double.IsInfinity(width) ? 1 : Math.Max(1, (int)((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)));

    private double ItemWidth(double width, int columns) =>
        Math.Max(0, (width - (columns - 1) * ColumnSpacing) / columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var itemW = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : ItemWidth(availableSize.Width, columns);
        double height = 0, rowH = 0, widest = 0;
        var visible = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(itemW, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            if (++visible % columns == 0)
            {
                height += rowH + RowSpacing;
                rowH = 0;
            }
        }
        height = visible % columns == 0 ? Math.Max(0, height - RowSpacing) : height + rowH;
        var width = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var itemW = ItemWidth(finalSize.Width, columns);
        double y = 0, rowH = 0;
        var i = 0;
        var row = new Control[columns];
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            row[i % columns] = child;
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            if (++i % columns == 0) Flush(columns);
        }
        Flush(i % columns);
        return finalSize;

        void Flush(int count)
        {
            if (count == 0) return;
            for (var c = 0; c < count; c++)
                row[c].Arrange(new Rect(c * (itemW + ColumnSpacing), y, itemW, rowH));
            y += rowH + RowSpacing;
            rowH = 0;
        }
    }
}
