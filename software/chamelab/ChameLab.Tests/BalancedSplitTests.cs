using System;
using Avalonia;
using Avalonia.Controls;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class BalancedSplitTests
{
    /// <summary>Like a list in a scroller: wants its content height, takes less when that's all there is.</summary>
    private sealed class Scroller(double need) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(10, Math.Min(need, availableSize.Height));
    }

    [Theory]
    [InlineData(500, 500, 200)] // both long: half each
    [InlineData(50, 500, 50)]   // few cards: keys take the rest
    [InlineData(500, 50, 350)]  // few keys: cards take the rest
    [InlineData(50, 50, 200)]   // both short: half each
    [InlineData(50, 300, 75)]   // room past both needs is shared evenly…
    [InlineData(50, 340, 55)]   // …shrinking smoothly to what the cards need
    [InlineData(50, 150, 150)]
    public void SharesTheHeightLeftAfterTheMiddle(double cards, double keys, double cardsHeight)
    {
        var split = new BalancedSplit
        {
            Spacing = 10,
            Children = { new Scroller(cards), new Scroller(30), new Scroller(keys) },
        };
        // 450 = 400 to share + a 30 middle + two 10 gaps.
        split.Measure(new Size(100, 450));
        split.Arrange(new Rect(0, 0, 100, 450));
        Assert.Equal(cardsHeight, split.Children[0].Bounds.Height);
        Assert.Equal(400 - cardsHeight, split.Children[2].Bounds.Height);
        Assert.Equal(450, split.Children[2].Bounds.Bottom);
    }

    [Fact]
    public void UnderAScrollerSharesItsMinHeightAndGrowsPastItForRowMinimums()
    {
        var cards = new Scroller(500) { MinHeight = 100 };
        var keys = new Scroller(500) { MinHeight = 120 };
        var middle = new Scroller(30) { IsVisible = false };
        var split = new BalancedSplit { Spacing = 0, MinHeight = 300, Children = { cards, middle, keys } };
        split.Measure(new Size(100, double.PositiveInfinity));
        Assert.Equal(300, split.DesiredSize.Height);

        split.MinHeight = 150;
        split.Measure(new Size(100, double.PositiveInfinity));
        Assert.Equal(220, split.DesiredSize.Height);
    }
}
