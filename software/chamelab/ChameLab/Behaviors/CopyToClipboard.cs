using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using ChameLab.Localization;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior: <c>Button behaviors:CopyToClipboard.Text="{Binding Value}"</c>
/// copies the text when the button is clicked (or another control is tapped) and confirms via tooltip.
/// </summary>
public static class CopyToClipboard
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(CopyToClipboard));

    static CopyToClipboard()
    {
        TextProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control is Button button)
            {
                button.Click -= OnClick;
                button.Click += OnClick;
            }
            else
            {
                control.Tapped -= OnTapped;
                control.Tapped += OnTapped;
            }
            if (ToolTip.GetTip(control) is null) ToolTip.SetTip(control, Strings.Hex_Copy);
        });
    }

    public static string? GetText(Control element) => element.GetValue(TextProperty);
    public static void SetText(Control element, string? value) => element.SetValue(TextProperty, value);

    private static async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control) await CopyAsync(control);
    }

    private static async void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control) await CopyAsync(control);
    }

    private static async System.Threading.Tasks.Task CopyAsync(Control control)
    {
        if (string.IsNullOrEmpty(GetText(control))) return;
        var clipboard = TopLevel.GetTopLevel(control)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(GetText(control));
        }
        catch
        {
            return; // clipboard can be unavailable (e.g. browser without permission)
        }

        ToolTip.SetTip(control, Strings.Common_Copied);
        ToolTip.SetIsOpen(control, true);
        DispatcherTimer.RunOnce(() =>
        {
            ToolTip.SetIsOpen(control, false);
            ToolTip.SetTip(control, Strings.Hex_Copy);
        }, TimeSpan.FromSeconds(1.2));
    }
}
