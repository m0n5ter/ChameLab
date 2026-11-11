using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// Byte count shown at the right of a <see cref="HexBox"/>. Its fill reaches the box's
/// top, right and bottom edges (inside its border), with a divider on the left; the
/// layout slot stays the template's, only the painting extends to the edges.
/// </summary>
public class HexCountChip : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<HexCountChip>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<HexCountChip>();

    static HexCountChip() => AffectsRender<HexCountChip>(BackgroundProperty, BorderBrushProperty);

    // After every layout pass: the text may be re-arranged alone, without the box.
    public HexCountChip() => LayoutUpdated += (_, _) => FitToBox();

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }

    /// <summary>
    /// Centers the text in the painted area (which is wider than the layout slot) and
    /// repaints. A render offset, so layout doesn't change.
    /// </summary>
    private void FitToBox()
    {
        if (Child is { } child && PaintedRect() is { } rect)
        {
            var dx = Math.Round((rect.Width - child.Bounds.Width) / 2 - child.Bounds.X);
            if (child.RenderTransform is not TranslateTransform t || t.X != dx)
                child.RenderTransform = new TranslateTransform(dx, 0);
        }
        InvalidateVisual();
    }

    /// <summary>The area to fill, in this control's coordinates: from its left edge to the box's inner edges.</summary>
    private Rect? PaintedRect()
    {
        if (this.FindAncestorOfType<TextBox>() is not { } box || this.TranslatePoint(default, box) is not { } p) return null;
        var b = box.BorderThickness;
        return new Rect(0, b.Top - p.Y,
            Math.Max(0, box.Bounds.Width - b.Right - p.X), Math.Max(0, box.Bounds.Height - b.Top - b.Bottom));
    }

    public override void Render(DrawingContext context)
    {
        if (this.FindAncestorOfType<TextBox>() is not { } box || PaintedRect() is not { } rect) return;
        var b = box.BorderThickness;
        var r = box.CornerRadius;
        if (Background is { } fill)
            context.DrawRectangle(fill, null, new RoundedRect(rect, 0,
                Math.Max(0, r.TopRight - b.Right), Math.Max(0, r.BottomRight - b.Right), 0));
        if (BorderBrush is { } line)
            context.DrawLine(new Pen(line), new Point(0.5, rect.Top), new Point(0.5, rect.Bottom));
    }
}
