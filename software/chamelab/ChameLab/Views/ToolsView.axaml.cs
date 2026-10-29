using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChameLab.Models;
using ChameLab.ViewModels;

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
            ? "Tap a frame, then another to select the range between."
            : "Shift+click selects a range, Ctrl+click adds a frame.";
        UpdateCopyLabel();
    }

    private void UpdateCopyLabel()
    {
        var count = FramesList.SelectedItems?.Count ?? 0;
        CopyFramesLabel.Text = count == 0 ? "Copy all" : $"Copy {count}";
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

    private async void OnCopyFrames(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolsViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var picked = FramesList.SelectedItems?.OfType<HfAnnotatedFrame>().ToList();
        var text = ToolsViewModel.FramesText(picked is { Count: > 0 } ? picked : vm.HfAnnotated);
        try { await clipboard.SetTextAsync(text); }
        catch { return; /* clipboard unavailable (browser without permission) */ }
        ToolTip.SetTip(CopyFramesButton, "Copied");
        ToolTip.SetIsOpen(CopyFramesButton, true);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            ToolTip.SetIsOpen(CopyFramesButton, false);
            ToolTip.SetTip(CopyFramesButton, "Copy the selected frames, or all of them");
        }, TimeSpan.FromSeconds(1.2));
    }
}
