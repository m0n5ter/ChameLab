using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ChameLab.Models;

namespace ChameLab.Controls;

/// <summary>
/// A QR code for <see cref="Text"/>, drawn as a square of whole device pixels per module with a white
/// quiet zone. Always dark on white, also in the dark theme: inverted codes trip up many scanners.
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    private const int QuietZone = 4;
    private bool[,]? _modules;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(TextProperty);
        AffectsMeasure<QrCodeView>(TextProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _modules = string.IsNullOrEmpty(Text) ? null : QrCodes.Encode(Text);
    }

    /// <summary>Square, as wide as allowed (the height follows the width).</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side)) side = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.White, new Rect(0, 0, side, side));
        if (_modules is not { } modules) return;

        var count = modules.GetLength(0) + 2 * QuietZone;
        // Whole device pixels per module keep the edges crisp; the rest is spread around as margin.
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var module = Math.Max(1, Math.Floor(side * scale / count)) / scale;
        var offset = Math.Round((side - module * count) / 2 * scale) / scale + module * QuietZone;
        for (var y = 0; y < modules.GetLength(0); y++)
            for (var x = 0; x < modules.GetLength(1); x++)
                if (modules[y, x])
                    context.FillRectangle(Brushes.Black, new Rect(offset + x * module, offset + y * module, module, module));
    }
}
