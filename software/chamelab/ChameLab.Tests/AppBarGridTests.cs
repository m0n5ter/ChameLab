using Avalonia;
using Avalonia.Controls;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class AppBarGridTests
{
    private sealed class Detail(double fullWidth, double compactWidth, string step) : Control
    {
        protected override Size MeasureOverride(Size availableSize) =>
            new(Parent is AppBarGrid bar && bar.Classes.Contains(step) ? compactWidth : fullWidth, 40);
    }

    [Fact]
    public void CollapsesOnlyAsNeededAndRestoresDetailsWhenSpaceReturns()
    {
        var bar = new AppBarGrid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto") };
        Control[] children =
        [
            new Detail(160, 160, ""),
            new Detail(120, 0, "hide-summary"),
            new Detail(100, 36, "hide-disconnect-label"),
            new Detail(60, 0, "hide-logo"),
            new Detail(70, 28, "hide-connection-label")
        ];
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            bar.Children.Add(children[i]);
        }

        bar.Measure(new Size(750, 100));
        Assert.Empty(bar.Classes);

        bar.Measure(new Size(390, 100));
        Assert.Equal(new[] { "hide-summary" }, bar.Classes);

        bar.Measure(new Size(330, 100));
        Assert.Contains("hide-disconnect-label", bar.Classes);
        Assert.DoesNotContain("hide-logo", bar.Classes);

        bar.Measure(new Size(250, 100));
        Assert.Contains("hide-connection-label", bar.Classes);
        Assert.True(bar.DesiredSize.Width <= 250);

        bar.Measure(new Size(750, 100));
        Assert.Empty(bar.Classes);
        Assert.Equal(510, bar.DesiredSize.Width);
    }
}
