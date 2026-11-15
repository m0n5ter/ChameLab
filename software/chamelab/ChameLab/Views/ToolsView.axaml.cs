using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChameLab.Models;
using ChameLab.ViewModels;
using ChameLab.Localization;

namespace ChameLab.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        FramesList.SelectionChanged += (_, _) => UpdateCopyLabel();
        CopyFramesButton.Click += OnCopyFrames;
        FramesList.AddHandler(TappedEvent, OnFrameTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        FramesHint.Text = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
            ? Strings.Tools_FramesHintTouch
            : Strings.Tools_FramesHintMouse;
        UpdateCopyLabel();
        // The typed lines' color comes from the theme.
        ActualThemeVariantChanged += (_, _) => { if (_vm is not null) ShowConsoleOutput(_vm.ConsoleText); };
        // Tunnel: Tab would move focus and Up/Down move the caret before the box sees them.
        ConsoleInput.AddHandler(KeyDownEvent, OnConsoleKey, RoutingStrategies.Tunnel);
        // A hint badge clicked: its word goes into the line, and typing carries on there.
        ConsoleHints.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (_vm is null || (e.Source as Button)?.Content is not string word) return;
            _vm.UseConsoleHint(word);
            ConsoleInput.Focus();
            ConsoleInput.CaretIndex = _vm.ConsoleInput.Length;
        });
    }

    private ToolsViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as ToolsViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;
        ShowConsoleOutput(_vm.ConsoleText);
        ApplyConsoleWrap(_vm.ConsoleWrap);
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm is not { } vm) return;
        switch (e.PropertyName)
        {
            case nameof(ToolsViewModel.IsConsole) when vm.IsConsole:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ConsoleInput.Focus());
                break;
            case nameof(ToolsViewModel.ConsoleText):
                ShowConsoleOutput(vm.ConsoleText);
                break;
            case nameof(ToolsViewModel.ConsoleWrap):
                ApplyConsoleWrap(vm.ConsoleWrap);
                break;
            case nameof(ToolsViewModel.ConsoleAutoscroll) when vm.ConsoleAutoscroll:
                ConsoleScroller.ScrollToEnd();
                break;
        }
    }

    /// <summary>The console's text, the typed lines marked; follows the newest line when autoscroll is on.</summary>
    private void ShowConsoleOutput(ConsoleOutput output)
    {
        var inlines = new InlineCollection();
        var at = 0;
        foreach (var span in output.Typed)
        {
            if (span.Start > at) inlines.Add(new Run(output.Text[at..span.Start]));
            var end = Math.Min(span.Start + span.Length, output.Text.Length);
            if (end > span.Start) inlines.Add(Typed(output.Text[span.Start..end]));
            at = Math.Max(at, end);
        }
        if (at < output.Text.Length) inlines.Add(new Run(output.Text[at..]));
        ConsoleOutput.Inlines = inlines;
        if (_vm?.ConsoleAutoscroll == true)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ConsoleScroller.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>A typed line: stands out from what the commands print, in the theme's blue.</summary>
    private Run Typed(string text)
    {
        var run = new Run(text) { FontWeight = FontWeight.SemiBold };
        if (this.TryFindResource("BrandBlueBrush", ActualThemeVariant, out var brush) && brush is IBrush blue)
            run.Foreground = blue;
        return run;
    }

    /// <summary>Wrapped, the output fits the width; otherwise long lines scroll sideways.</summary>
    private void ApplyConsoleWrap(bool wrap)
    {
        ConsoleOutput.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ConsoleScroller.HorizontalScrollBarVisibility = wrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    private void OnConsoleKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ToolsViewModel vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.RunConsoleCommand.Execute(null);
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                vm.ClearConsoleCommand.Execute(null);
                break;
            case Key.Up or Key.Down:
                vm.ConsoleHistory(e.Key == Key.Up ? -1 : 1);
                ConsoleInput.CaretIndex = vm.ConsoleInput.Length;
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                ConsoleInput.CaretIndex = vm.CompleteConsole(ConsoleInput.CaretIndex);
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.IsConsoleRunning
                                                                    && ConsoleInput.SelectionStart == ConsoleInput.SelectionEnd:
                vm.CancelConsoleCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void UpdateCopyLabel()
    {
        var count = FramesList.SelectedItems?.Count ?? 0;
        CopyFramesLabel.Text = count == 0 ? Strings.Tools_CopyAll : string.Format(Strings.Tools_CopyN, count);
    }

    /// <summary>First frame of a range picked by touch; null when none is.</summary>
    private HfAnnotatedFrame? _touchAnchor;
    private bool _touchRange;

    /// <summary>
    /// Touch: the first tap picks a frame, a second one on another frame selects the range between, and a tap
    /// after a range starts over. Tapping the picked frame again clears it. Runs after the list's own handling,
    /// so a finger drag still scrolls the list.
    /// </summary>
    private void OnFrameTapped(object? sender, TappedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse || DataContext is not ToolsViewModel vm) return;
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: HfAnnotatedFrame tapped }
            || FramesList.SelectedItems is not { } selected)
            return;
        if (_touchAnchor is { } anchor && !vm.HfAnnotated.Contains(anchor)) _touchAnchor = null;
        selected.Clear();
        if (_touchAnchor is null || _touchRange)
        {
            selected.Add(tapped);
            (_touchAnchor, _touchRange) = (tapped, false);
        }
        else if (ReferenceEquals(_touchAnchor, tapped)) _touchAnchor = null;
        else
        {
            int from = Math.Min(_touchAnchor.Index, tapped.Index), to = Math.Max(_touchAnchor.Index, tapped.Index);
            foreach (var f in vm.HfAnnotated.Where(f => f.Index >= from && f.Index <= to)) selected.Add(f);
            _touchRange = true;
        }
    }

    private async void OnCopyT55xxKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolsViewModel vm || vm.T55xxFoundKeyText() is not { } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { return; /* clipboard unavailable (browser without permission) */ }
        vm.NotifyKeysCopied(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private async void OnCopyFrames(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolsViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var picked = FramesList.SelectedItems?.OfType<HfAnnotatedFrame>().ToList();
        var text = ToolsViewModel.FramesText(picked is { Count: > 0 } ? picked : vm.HfAnnotated);
        try { await clipboard.SetTextAsync(text); }
        catch { return; /* clipboard unavailable (browser without permission) */ }
        ToolTip.SetTip(CopyFramesButton, Strings.Common_Copied);
        ToolTip.SetIsOpen(CopyFramesButton, true);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            ToolTip.SetIsOpen(CopyFramesButton, false);
            ToolTip.SetTip(CopyFramesButton, Strings.Tools_CopyTheSelectedFramesOrAll);
        }, TimeSpan.FromSeconds(1.2));
    }
}
