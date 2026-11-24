using System;
using Avalonia;
using Avalonia.Controls;
using ChameLab.Sync;

namespace ChameLab.Behaviors;

/// <summary>Uppercases typed or pasted relay codes while preserving case-sensitive QR payloads.</summary>
public static class ConnectionCodeInput
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(ConnectionCodeInput));

    static ConnectionCodeInput()
    {
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>((box, _) => Normalize(box));
        IsEnabledProperty.Changed.AddClassHandler<TextBox>((box, _) => Normalize(box));
    }

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);

    private static void Normalize(TextBox box)
    {
        if (!GetIsEnabled(box) || box.Text is not { } text
            || text.TrimStart().StartsWith(PairingCode.QrPrefix, StringComparison.OrdinalIgnoreCase)) return;
        var upper = text.ToUpperInvariant();
        if (upper == text) return;
        var start = box.SelectionStart;
        var end = box.SelectionEnd;
        box.SetCurrentValue(TextBox.TextProperty, upper);
        box.SelectionStart = Math.Min(start, upper.Length);
        box.SelectionEnd = Math.Min(end, upper.Length);
    }
}
