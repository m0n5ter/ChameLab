using Avalonia;
using Avalonia.Controls;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class InlineStackTests
{
    private sealed class Box(double width, double height) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(width, height);
    }

    [Fact]
    public void SideBySideWhenBothFitElseStacked()
    {
        var first = new Box(100, 20);
        var second = new Box(80, 16);
        var stack = new InlineStack { Spacing = 10, Children = { first, second } };

        stack.Measure(new Size(200, 500));
        Assert.Equal(new Size(190, 20), stack.DesiredSize);
        stack.Arrange(new Rect(0, 0, 200, 20));
        Assert.Equal(new Rect(110, 0, 90, 20), second.Bounds);

        stack.Measure(new Size(150, 500));
        Assert.Equal(new Size(100, 36), stack.DesiredSize);
        stack.Arrange(new Rect(0, 0, 150, 36));
        Assert.Equal(new Rect(0, 20, 150, 16), second.Bounds);
    }
}
