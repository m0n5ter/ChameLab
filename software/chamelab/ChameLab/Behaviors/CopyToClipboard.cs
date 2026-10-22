using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior: <c>Button behaviors:CopyToClipboard.Text="{Binding Value}"</c>
/// copies the text when the button is clicked and briefly confirms via tooltip.
/// </summary>
public static class CopyToClipboard
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Button, string?>("Text", typeof(CopyToClipboard));

    static CopyToClipboard()
    {
        TextProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            button.Click -= OnClick;
            button.Click += OnClick;
            if (ToolTip.GetTip(button) is null) ToolTip.SetTip(button, "Copy");
        });
    }

    public static string? GetText(Button element) => element.GetValue(TextProperty);
    public static void SetText(Button element, string? value) => element.SetValue(TextProperty, value);

    private static async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || string.IsNullOrEmpty(GetText(button))) return;
        var clipboard = TopLevel.GetTopLevel(button)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(GetText(button));
        }
        catch
        {
            return; // clipboard can be unavailable (e.g. browser without permission)
        }

        ToolTip.SetTip(button, "Copied");
        ToolTip.SetIsOpen(button, true);
        DispatcherTimer.RunOnce(() =>
        {
            ToolTip.SetIsOpen(button, false);
            ToolTip.SetTip(button, "Copy");
        }, TimeSpan.FromSeconds(1.2));
    }
}
