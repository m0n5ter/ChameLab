using System.Linq;
using Avalonia;
using Avalonia.Controls;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class TileGridTests
{
    private sealed class TileBox : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(120, 40);
    }

    private static TileGrid CreateGrid(int count, bool fitItemCount = true, int maxColumns = 3)
    {
        var grid = new TileGrid
        {
            MinItemWidth = 240,
            MaxColumns = maxColumns,
            FitItemCount = fitItemCount,
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        for (var i = 0; i < count; i++) grid.Children.Add(new TileBox());
        return grid;
    }

    [Theory]
    [InlineData(1, 778, new[] { 778.0 })]
    [InlineData(2, 778, new[] { 385.0, 385.0 })]
    [InlineData(3, 778, new[] { 254.0, 254.0, 254.0 })]
    [InlineData(3, 540, new[] { 266.0, 266.0, 266.0 })]
    [InlineData(3, 360, new[] { 360.0, 360.0, 360.0 })]
    public void FitItemCountAndMaxColumnsStretchAndWrapAsExpected(int count, double width, double[] expectedWidths)
    {
        var grid = CreateGrid(count);
        grid.Measure(new Size(width, 500));
        grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
        Assert.Equal(expectedWidths, grid.Children.Select(c => c.Bounds.Width).ToArray());
    }

    [Fact]
    public void MaxColumnsCapsColumnsEvenOnWideViewports()
    {
        var grid = CreateGrid(4);
        grid.Measure(new Size(1216, 500));
        grid.Arrange(new Rect(0, 0, 1216, grid.DesiredSize.Height));

        Assert.Equal(400.0, grid.Children[0].Bounds.Width, 6);
        Assert.Equal(0, grid.Children[2].Bounds.Y);
        Assert.Equal(48, grid.Children[3].Bounds.Y);
        Assert.Equal(0, grid.Children[3].Bounds.X);
    }

    [Fact]
    public void DefaultsKeepOriginalBehavior()
    {
        var grid = CreateGrid(1, fitItemCount: false, maxColumns: 0);
        grid.Measure(new Size(778, 500));
        grid.Arrange(new Rect(0, 0, 778, grid.DesiredSize.Height));
        Assert.Equal(254.0, grid.Children[0].Bounds.Width, 6);
    }
}
