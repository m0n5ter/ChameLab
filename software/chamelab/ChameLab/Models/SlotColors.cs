using System;
using System.Collections.Generic;
using System.Linq;

namespace ChameLab.Models;

/// <summary>Card accent colours and how the device's slot LED shows them.</summary>
public static class SlotColors
{
    /// <summary>Preset accents: Material colours around the hue wheel, then darker shades and neutrals.</summary>
    public static IReadOnlyList<string> Palette { get; } = new[]
    {
        "#FF5722", "#FF9800", "#FFC107", "#FFEB3B", "#CDDC39", "#8BC34A", "#4CAF50", "#009688",
        "#00BCD4", "#03A9F4", "#2196F3", "#3F51B5", "#673AB7", "#9C27B0", "#E91E63", "#F44336",
        "#B71C1C", "#795548", "#827717", "#1B5E20", "#006064", "#0D47A1", "#4A148C", "#607D8B",
    };

    // What the firmware shows for a card without a colour of its own.
    public const string DefaultHf = "#00FF00";
    public const string DefaultLf = "#0000FF";
    public const string DefaultDual = "#FF0000";

    /// <summary>
    /// The firmware keeps 5 bits per channel, so a colour read back from the
    /// device is slightly off; map it onto the palette colour it was stored from.
    /// </summary>
    public static string SnapToPalette(string color)
    {
        var key = Quantize(color);
        return Palette.FirstOrDefault(p => Quantize(p) == key) ?? CardSave.NormalizeColor(color);
    }

    /// <summary>The two colours end up the same on the LED (the firmware keeps 5 bits per channel).</summary>
    public static bool SameLed(string a, string b) => Quantize(a) == Quantize(b);

    /// <summary>The LED colours a slot shows for its HF and LF cards (null for an absent frequency).</summary>
    public static (string? Hf, string? Lf) Effective(SlotInfo slot, bool hasHf, bool hasLf)
    {
        if (hasHf && hasLf && slot.HfColor is null && slot.LfColor is null)
            return (DefaultDual, DefaultDual);
        return (hasHf ? slot.HfColor ?? DefaultHf : null, hasLf ? slot.LfColor ?? DefaultLf : null);
    }

    private static int Quantize(string color)
    {
        var rgb = Convert.FromHexString(CardSave.NormalizeColor(color).AsSpan(1));
        int q(byte c) => (c * 31 + 127) / 255;
        return (q(rgb[0]) << 10) | (q(rgb[1]) << 5) | q(rgb[2]);
    }
}
