using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using ChameLab.Controls;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

/// <summary>The console's hint line and the row that keeps its command line wide.</summary>
public class ConsoleTests
{
    [Fact]
    public void HintListsTheWordsThatMayComeNext()
    {
        Assert.Contains("hw", ToolsViewModel.HintFor("").Split("   "));
        Assert.Contains("hf", ToolsViewModel.HintFor("").Split("   "));
        Assert.Subset(new HashSet<string> { "slot", "settings", "share" }, ToolsViewModel.HintFor("hw s").Split("   ").ToHashSet());
        Assert.Contains("nested", ToolsViewModel.HintFor("hf mf ").Split("   "));
        // A whole word typed: what follows it.
        Assert.Contains("list", ToolsViewModel.HintFor("hw slot").Split("   "));
    }

    [Fact]
    public void HintListsTheOptionsOfATypedCommandRequiredOnesStarred()
    {
        Assert.Contains("-s, --slot*", ToolsViewModel.HintFor("hw slot change ").Split("   "));
        Assert.Contains("-s, --slot*", ToolsViewModel.HintFor("hw slot change").Split("   "));
        Assert.Equal("-s, --slot*", ToolsViewModel.HintFor("hw slot change --sl"));
        // A value being typed has nothing to suggest but what the command does.
        Assert.Equal("Set emulation tag slot activated", ToolsViewModel.HintFor("hw slot change -s 3"));
        Assert.Equal("", ToolsViewModel.HintFor("nonsense"));
    }

    private sealed class Box(double width) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => new(Math.Min(width, availableSize.Width), 30);
    }

    [Fact]
    public void SideDockKeepsTheFillChildsMinimumWidthFromTheSide()
    {
        var fill = new Box(1000) { MinWidth = 160 };
        var side = new Box(300);
        var dock = new SideDock { Spacing = 8, Children = { fill, side } };

        dock.Measure(new Size(400, 100));
        dock.Arrange(new Rect(0, 0, 400, 100));
        Assert.Equal(400 - 160 - 8, side.Bounds.Width);
        Assert.Equal(400 - side.Bounds.Width, side.Bounds.X);
        Assert.Equal(160, fill.Bounds.Width);

        dock.Measure(new Size(800, 100));
        dock.Arrange(new Rect(0, 0, 800, 100));
        Assert.Equal(300, side.Bounds.Width);
        Assert.Equal(800 - 300 - 8, fill.Bounds.Width);
    }
}
