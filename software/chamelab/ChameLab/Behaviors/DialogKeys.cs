using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behaviour: <c>behaviors:DialogKeys.IsDefault="True"</c> presses the button on Enter,
/// <c>behaviors:DialogKeys.IsCancel="True"</c> on Esc. Unlike Button.IsDefault / IsCancel it only
/// fires while the button is effectively visible and enabled, so the buttons of hidden dialogs
/// (whose IsVisible is turned off on a parent) stay quiet. Keys a focused control handles itself
/// (Enter on a focused button, Esc in an open drop-down) win.
/// <c>behaviors:DialogKeys.IsBack="True"</c> marks a page's own back button (closing an editor back
/// to its list) for the system Back button, see <see cref="TryGoBack"/>.
/// </summary>
public static class DialogKeys
{
    public static readonly AttachedProperty<bool> IsDefaultProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsDefault", typeof(DialogKeys));

    public static readonly AttachedProperty<bool> IsCancelProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsCancel", typeof(DialogKeys));

    public static readonly AttachedProperty<bool> IsBackProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsBack", typeof(DialogKeys));

    private static readonly List<Button> Buttons = new();

    static DialogKeys()
    {
        IsDefaultProperty.Changed.AddClassHandler<Button>((button, _) => Track(button));
        IsCancelProperty.Changed.AddClassHandler<Button>((button, _) => Track(button));
        IsBackProperty.Changed.AddClassHandler<Button>((button, _) => Track(button));
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>(OnKeyDown);
    }

    public static bool GetIsDefault(Button element) => element.GetValue(IsDefaultProperty);
    public static void SetIsDefault(Button element, bool value) => element.SetValue(IsDefaultProperty, value);

    public static bool GetIsCancel(Button element) => element.GetValue(IsCancelProperty);
    public static void SetIsCancel(Button element, bool value) => element.SetValue(IsCancelProperty, value);

    public static bool GetIsBack(Button element) => element.GetValue(IsBackProperty);
    public static void SetIsBack(Button element, bool value) => element.SetValue(IsBackProperty, value);

    /// <summary>Presses the open dialog's cancel button, as Esc would.</summary>
    public static bool TryCancel(TopLevel top) => TryPress(top, IsCancelProperty);

    /// <summary>
    /// Presses the shown page back button. A disabled one (the page is busy) still takes the
    /// request, so system Back doesn't leave the page from under it.
    /// </summary>
    public static bool TryGoBack(TopLevel top)
    {
        foreach (var button in Buttons)
        {
            if (!button.GetValue(IsBackProperty) || TopLevel.GetTopLevel(button) != top) continue;
            if (!button.IsEffectivelyVisible) continue;
            if (button.IsEffectivelyEnabled) Press(button);
            return true;
        }
        return false;
    }

    private static void Track(Button button)
    {
        // Registered while attached, so detached pages don't keep their buttons alive.
        button.AttachedToVisualTree -= OnAttached;
        button.DetachedFromVisualTree -= OnDetached;
        button.AttachedToVisualTree += OnAttached;
        button.DetachedFromVisualTree += OnDetached;
        if (TopLevel.GetTopLevel(button) is not null && !Buttons.Contains(button)) Buttons.Add(button);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button && !Buttons.Contains(button)) Buttons.Add(button);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button) Buttons.Remove(button);
    }

    private static void OnKeyDown(TopLevel top, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        var property = e.Key switch
        {
            Key.Enter => IsDefaultProperty,
            Key.Escape => IsCancelProperty,
            _ => null,
        };
        if (property is not null && TryPress(top, property)) e.Handled = true;
    }

    private static bool TryPress(TopLevel top, AttachedProperty<bool> property)
    {
        foreach (var button in Buttons)
        {
            if (!button.GetValue(property) || TopLevel.GetTopLevel(button) != top) continue;
            if (!button.IsEffectivelyVisible || !button.IsEffectivelyEnabled) continue;
            Press(button);
            return true;
        }
        return false;
    }

    private static void Press(Button button)
    {
        if (button.Command is { } command)
        {
            if (command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        }
        else
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
