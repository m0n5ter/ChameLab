using Avalonia;
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class ActionRowTests
{
    private sealed class ActionButton : Control
    {
        protected override Size MeasureOverride(Size availableSize) =>
            new(Classes.Contains(ActionRow.IconicClass) ? 40 : 100, 40);
    }

    [Fact]
    public void LabelsStayVisibleWhenTwoButtonsFitAndCollapseByPriorityWhenNeeded()
    {
        var primary = new ActionButton();
        var secondary = new ActionButton();
        ActionRow.SetPriority(primary, 2);
        var row = new ActionRow { Spacing = 6, Wrap = true, Children = { primary, secondary } };
        row.Measure(new Size(210, 500));
        Assert.DoesNotContain(ActionRow.IconicClass, primary.Classes);
        Assert.DoesNotContain(ActionRow.IconicClass, secondary.Classes);

        row.Measure(new Size(150, 500));
        Assert.DoesNotContain(ActionRow.IconicClass, primary.Classes);
        Assert.Contains(ActionRow.IconicClass, secondary.Classes);

        row.Measure(new Size(210, 500));
        Assert.DoesNotContain(ActionRow.IconicClass, secondary.Classes);
    }

    [Fact]
    public void IconButtonsWrapOnlyWhenTheyCannotFitAndStayInsideThePanel()
    {
        var row = new ActionRow { Wrap = true, Spacing = 6 };
        for (var i = 0; i < 4; i++) row.Children.Add(new ActionButton());
        row.Measure(new Size(100, 500));
        Assert.Equal(86, row.DesiredSize.Height);
        row.Arrange(new Rect(0, 0, 100, row.DesiredSize.Height));
        foreach (var child in row.Children)
        {
            Assert.Contains(ActionRow.IconicClass, child.Classes);
            Assert.InRange(child.Bounds.Right, 0, 100);
            Assert.InRange(child.Bounds.Bottom, 0, 86);
        }
        Assert.Equal(46, row.Children[2].Bounds.Y);
    }

    [Theory]
    [InlineData(0, 32, 36, 0, 1)]
    [InlineData(16, 32, 36, 0.5, 1)]
    [InlineData(38, 32, 36, 1, 5 / 6.0)]
    [InlineData(1000, 32, 36, 1, ActionRow.MinIconScale)]
    [InlineData(10, 0, 36, 0, 26 / 36.0)]
    public void PlanTakesGapsAndPaddingFirstThenNarrowsTheIcons(
        double excess, double reducible, double content, double factor, double scale)
    {
        var plan = ActionRow.Plan(excess, reducible, content);
        Assert.Equal(factor, plan.Factor, 6);
        Assert.Equal(scale, plan.Scale, 6);
    }

    [Fact]
    public void IconsTooWideForTheRowCloseTheirGapsDownToTheMinimum()
    {
        var row = new ActionRow { Spacing = 8 };
        for (var i = 0; i < 4; i++) row.Children.Add(new ActionButton());
        row.Measure(new Size(178, 100));
        row.Arrange(new Rect(0, 0, 178, 40));
        // 4 × 40 + 3 × 8 = 184, 6 too wide: each gap loses 2.
        Assert.Equal(new[] { 0.0, 46, 92, 138 }, row.Children.Select(c => c.Bounds.X));

        row.Measure(new Size(100, 100));
        row.Arrange(new Rect(0, 0, 100, 40));
        Assert.Equal(120 + 3 * ActionRow.MinGap, row.Children[3].Bounds.X);
    }

    /// <summary>
    /// Stands in for a button template's content presenter: a ContentPresenter only builds its
    /// child under a logical root, which these tests have none of.
    /// </summary>
    private sealed class CenteringHost : Control
    {
        private readonly Control _child;

        public CenteringHost(Control child)
        {
            _child = child;
            VisualChildren.Add(child);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _child.Measure(availableSize);
            return _child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var width = Math.Min(finalSize.Width, _child.DesiredSize.Width);
            _child.Arrange(new Rect((finalSize.Width - width) / 2, 0, width, finalSize.Height));
            return finalSize;
        }
    }

    private static Button IconButton() => new()
    {
        Width = 40,
        Content = new Border { Width = 18, Height = 18 },
        Template = new FuncControlTemplate<Button>((button, _) => new CenteringHost((Control)button.Content!)),
    };

    [Fact]
    public void ButtonsShrinkTheirSidesWithTheGapsThenNarrowTheirIconsAndRecoverWithRoom()
    {
        var first = IconButton();
        var second = IconButton();
        var row = new ActionRow { Spacing = 8, Children = { first, second } };

        // 40 + 8 + 40 = 88; reducible: the gap's 4 and 7 on each side of either icon, 32 in all.
        row.Measure(new Size(72, 100));
        row.Arrange(new Rect(0, 0, 72, 40));
        Assert.Equal(33, first.Bounds.Width, 6);
        Assert.Equal(39, second.Bounds.X, 6);
        Assert.Null(first.Content is Control c ? c.RenderTransform : null);

        // 38 too wide: 32 from the sides and the gap, the last 6 from the icons' 36.
        row.Measure(new Size(50, 100));
        row.Arrange(new Rect(0, 0, 50, 40));
        Assert.Equal(23, first.Bounds.Width, 6);
        Assert.Equal(27, second.Bounds.X, 6);
        var icon = (Control)first.Content!;
        Assert.Equal(5 / 6.0, ((ScaleTransform)icon.RenderTransform!).ScaleX, 6);
        Assert.Equal(15, icon.Bounds.Width + icon.Margin.Right, 6);

        row.Measure(new Size(200, 100));
        row.Arrange(new Rect(0, 0, 200, 40));
        Assert.Equal(40, first.Width);
        Assert.Equal(48, second.Bounds.X, 6);
        Assert.Null(icon.RenderTransform);
        Assert.Equal(default, icon.Margin);
    }
}
