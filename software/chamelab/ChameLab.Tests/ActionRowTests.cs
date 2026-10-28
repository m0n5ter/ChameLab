using Avalonia;
using Avalonia.Controls;
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
}
