using System;
using System.Collections.Generic;
using System.Linq;

namespace ChameLab.Models;

/// <summary>Card accent colours and how the device's slot LED shows them.</summary>
public static class SlotColors
{
    /// <summary>
    /// Preset colours: 24 fully saturated hues, 15° apart from red, so the slot LED shows
    /// them at full brightness (red, green and blue are the firmware's own defaults).
    /// The app draws them toned down, see <see cref="Muted"/>.
    /// </summary>
    public static IReadOnlyList<string> Palette { get; } = new[]
    {
        "#FF0000", "#FF4000", "#FF8000", "#FFBF00", "#FFFF00", "#BFFF00",
        "#80FF00", "#40FF00", "#00FF00", "#00FF40", "#00FF80", "#00FFBF",
        "#00FFFF", "#00BFFF", "#0080FF", "#0040FF", "#0000FF", "#4000FF",
        "#8000FF", "#BF00FF", "#FF00FF", "#FF00BF", "#FF0080", "#FF0040",
    };

    /// <summary>
    /// How the app draws a card colour: less saturated and nearer mid lightness than the
    /// LED colour it stands for, so a list of cards doesn't look garish.
    /// </summary>
    public static string Muted(string color)
    {
        var rgb = Convert.FromHexString(CardSave.NormalizeColor(color).AsSpan(1));
        var (h, s, l) = ToHsl(rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0);
        var (r, g, b) = FromHsl(h, s * 0.6, 0.5 + (l - 0.5) * 0.5);
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return "#" + Convert.ToHexString(new[] { B(r), B(g), B(b) });
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 < 0 ? h * 60 + 360 : h * 60, s, l);
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }

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
