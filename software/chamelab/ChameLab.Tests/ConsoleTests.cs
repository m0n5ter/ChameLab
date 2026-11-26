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
[Collection(AppSettingsCollection.Name)]
public class ConsoleTests
{
    [Fact]
    public void HintListsTheWordsThatMayComeNext()
    {
        Assert.Contains("hw", ToolsViewModel.HintFor("").Words);
        Assert.Contains("hf", ToolsViewModel.HintFor("").Words);
        Assert.Subset(new HashSet<string> { "slot", "settings", "share" }, ToolsViewModel.HintFor("hw s").Words.ToHashSet());
        Assert.Contains("nested", ToolsViewModel.HintFor("hf mf ").Words);
        // A whole word typed: what follows it.
        Assert.Contains("list", ToolsViewModel.HintFor("hw slot").Words);
    }

    [Fact]
    public void HintListsTheOptionsOfATypedCommandRequiredOnesStarred()
    {
        Assert.Contains("-s, --slot*", ToolsViewModel.HintFor("hw slot change ").Words);
        Assert.Contains("-s, --slot*", ToolsViewModel.HintFor("hw slot change").Words);
        Assert.Equal(["-s, --slot*"], ToolsViewModel.HintFor("hw slot change --sl").Words);
        // A value being typed has nothing to suggest but what the command does.
        Assert.Equal(([], "Set emulation tag slot activated"), ToolsViewModel.HintFor("hw slot change -s 3"));
        Assert.Equal(([], ""), ToolsViewModel.HintFor("nonsense"));
    }

    [Theory]
    [InlineData("hw s", "slot", "hw slot ")]
    [InlineData("hw slot", "list", "hw slot list ")]
    [InlineData("", "hw", "hw ")]
    [InlineData("hw slot change ", "-s, --slot*", "hw slot change --slot ")]
    [InlineData("hw slot change --sl", "-s, --slot*", "hw slot change --slot ")]
    [InlineData("hw slot change", "-s, --slot*", "hw slot change --slot ")]
    public void ClickedHintCompletesOrFollowsTheLine(string line, string hint, string expected)
    {
        var vm = new ToolsViewModel(new ChameLab.Services.DeviceService([])) { ConsoleInput = line };
        vm.UseConsoleHint(hint);
        Assert.Equal(expected, vm.ConsoleInput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLineIsNotRun(string line)
    {
        var vm = new ToolsViewModel(new ChameLab.Services.DeviceService([])) { ConsoleInput = line };
        Assert.False(vm.RunConsoleCommand.CanExecute(null));
        vm.SubmitConsoleLine();
        Assert.False(vm.IsConsoleRunning);
        Assert.Equal("", vm.ConsoleText.Text);
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

    [Theory]
    [InlineData("UID=5FC1AD1E NT=A9385308", 6, "5FC1AD1E")]
    [InlineData("UID=5FC1AD1E NT=A9385308", 4, "5FC1AD1E")]
    [InlineData("UID=5FC1AD1E NT=A9385308", 12, "5FC1AD1E")]
    [InlineData("UID=5FC1AD1E NT=A9385308", 0, "UID")]
    [InlineData("> hf mf elog\n", 12, "elog")]
    [InlineData("   \n", 1, "")]
    public void TouchWordSpanSelectsTokenUnderFinger(string text, int index, string expected)
    {
        var (start, end) = TouchSelectableText.WordSpanAt(text, index);
        Assert.Equal(expected, text[start..end]);
    }

    [Fact]
    public void TouchBarPlacesAboveSelectionOrBelowHandlesOrPinnedInViewport()
    {
        var bar = new Border { Width = 140, Height = 40 };
        bar.Measure(Size.Infinity);
        var viewport = new Rect(0, 0, 360, 400);

        // Near the bottom: fits above the selection.
        TouchSelection.ArrangeBar(bar, new Point(100, 320), new Point(200, 320), 16, 40, sameLine: true, viewport);
        Assert.Equal(320 - 16 - 8 - 40, bar.Bounds.Y);
        Assert.Equal(150 - 70, bar.Bounds.X);

        // At the top of the viewport: flips below the end handle.
        TouchSelection.ArrangeBar(bar, new Point(10, 20), new Point(110, 20), 16, 40, sameLine: true, viewport);
        Assert.Equal(20 + 40 + 8, bar.Bounds.Y);

        // Multi-line selection filling the viewport: pins near the top edge.
        TouchSelection.ArrangeBar(bar, new Point(10, 20), new Point(200, 380), 16, 40, sameLine: false, viewport);
        Assert.Equal(8, bar.Bounds.Y);
        Assert.Equal(180 - 70, bar.Bounds.X);
    }

    [Fact]
    public void TouchSelectionHandleHitTestsItsFullBoxOnlyWhileVisible()
    {
        var handle = new TouchSelectionHandle(new Border(), start: true, () => null, (_, _) => { }, _ => { }, () => { });
        handle.Measure(Size.Infinity);
        Assert.Equal(new Size(44, 40), handle.DesiredSize);
        handle.Arrange(new Rect(10, 20, 44, 40));

        Assert.True(handle.HitTest(new Point(1, 1)));
        Assert.True(handle.HitTest(new Point(43, 39)));
        Assert.False(handle.HitTest(new Point(45, 20)));

        handle.IsVisible = false;
        Assert.False(handle.HitTest(new Point(10, 10)));
    }
}
