using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ChameLab.Controls;

/// <summary>
/// Plot of a raw LF capture: one 8-bit ADC sample per carrier cycle against time, with the decode threshold,
/// the gap threshold and the samples below it shaded. <see cref="Zoom"/> magnifies the time axis; the wheel
/// zooms around the pointer and dragging pans, so it works the same with a mouse and a finger.
/// </summary>
public class LfWaveform : Control
{
    public static readonly StyledProperty<byte[]?> SamplesProperty =
        AvaloniaProperty.Register<LfWaveform, byte[]?>(nameof(Samples));

    /// <summary>Level dividing high from low (the dashed line).</summary>
    public static readonly StyledProperty<int> ThresholdProperty =
        AvaloniaProperty.Register<LfWaveform, int>(nameof(Threshold));

    /// <summary>Samples below this level are shaded as gaps in the field.</summary>
    public static readonly StyledProperty<int> GapThresholdProperty =
        AvaloniaProperty.Register<LfWaveform, int>(nameof(GapThreshold));

    /// <summary>How many times the time axis is stretched; 1 fits the whole capture.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<LfWaveform, double>(nameof(Zoom), 1,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: (_, z) => Math.Clamp(z, 1, MaxZoom));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(LineBrush), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> ThresholdBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(ThresholdBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> GapBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(GapBrush), Brushes.IndianRed);

    public const double MaxZoom = 64;

    /// <summary>Index of the sample at the left edge.</summary>
    private double _offset;
    private Point? _dragFrom;
    private double _dragOffset;

    static LfWaveform()
    {
        AffectsRender<LfWaveform>(SamplesProperty, ThresholdProperty, GapThresholdProperty, ZoomProperty,
            LineBrushProperty, ThresholdBrushProperty, GapBrushProperty);
        ClipToBoundsProperty.OverrideDefaultValue<LfWaveform>(true);
    }

    public byte[]? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public int Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public int GapThreshold { get => GetValue(GapThresholdProperty); set => SetValue(GapThresholdProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? ThresholdBrush { get => GetValue(ThresholdBrushProperty); set => SetValue(ThresholdBrushProperty, value); }
    public IBrush? GapBrush { get => GetValue(GapBrushProperty); set => SetValue(GapBrushProperty, value); }

    private int Count => Samples?.Length ?? 0;
    /// <summary>Samples in view.</summary>
    private double Visible => Count / Zoom;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SamplesProperty) _offset = 0;
        else if (change.Property == ZoomProperty) ClampOffset();
    }

    private void ClampOffset() => _offset = Math.Clamp(_offset, 0, Math.Max(0, Count - Visible));

    private double X(double sample) => (sample - _offset) / Visible * Bounds.Width;
    private double Y(int value) => 4 + (1 - value / 255.0) * (Bounds.Height - 8);

    public override void Render(DrawingContext context)
    {
        if (Samples is not { Length: > 1 } samples || Bounds.Width <= 0) return;
        var width = Bounds.Width;
        var perPixel = Visible / width;
        var first = (int)Math.Floor(_offset);
        var last = Math.Min(samples.Length - 1, (int)Math.Ceiling(_offset + Visible));

        // Shaded gaps: stretches of samples below the gap threshold.
        if (GapBrush is { } gapBrush)
        {
            var shade = new ImmutableSolidColorBrush(gapBrush is ISolidColorBrush s ? s.Color : Colors.IndianRed, 0.18);
            for (var i = first; i <= last; i++)
            {
                if (samples[i] >= GapThreshold) continue;
                var start = i;
                while (i <= last && samples[i] < GapThreshold) i++;
                context.FillRectangle(shade, new Rect(X(start), 0, Math.Max(1, X(i) - X(start)), Bounds.Height));
            }
        }

        if (ThresholdBrush is { } tb)
        {
            var dashed = new Pen(tb, 1, new DashStyle([4, 4], 0));
            context.DrawLine(dashed, new Point(0, Y(Threshold)), new Point(width, Y(Threshold)));
            if (GapBrush is { } gb)
                context.DrawLine(new Pen(gb, 1, new DashStyle([2, 4], 0)),
                    new Point(0, Y(GapThreshold)), new Point(width, Y(GapThreshold)));
        }

        if (LineBrush is not { } lineBrush) return;
        var pen = new Pen(lineBrush, 1.2);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            if (perPixel <= 1)
            {
                // Zoomed in: a step per sample, each held for its 8 µs.
                g.BeginFigure(new Point(X(first), Y(samples[first])), false);
                for (var i = first; i <= last; i++)
                {
                    g.LineTo(new Point(X(i), Y(samples[i])));
                    g.LineTo(new Point(X(i + 1), Y(samples[i])));
                }
                g.EndFigure(false);
            }
            else
            {
                // Zoomed out: the range of the samples under each pixel column.
                var columns = (int)Math.Ceiling(width);
                var started = false;
                for (var px = 0; px < columns; px++)
                {
                    var from = (int)(_offset + px * perPixel);
                    var to = Math.Min(samples.Length, (int)(_offset + (px + 1) * perPixel) + 1);
                    if (from >= to) continue;
                    byte min = 255, max = 0;
                    for (var i = from; i < to; i++)
                    {
                        min = Math.Min(min, samples[i]);
                        max = Math.Max(max, samples[i]);
                    }
                    if (!started)
                    {
                        g.BeginFigure(new Point(px + 0.5, Y(max)), false);
                        started = true;
                    }
                    g.LineTo(new Point(px + 0.5, Y(max)));
                    g.LineTo(new Point(px + 0.5, Y(min)));
                }
                if (started) g.EndFigure(false);
            }
        }
        context.DrawGeometry(null, pen, geometry);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Count == 0 || Bounds.Width <= 0) return;
        var x = e.GetPosition(this).X;
        var anchor = _offset + x / Bounds.Width * Visible;
        var zoom = Math.Clamp(Zoom * Math.Pow(1.25, e.Delta.Y), 1, MaxZoom);
        _offset = anchor - x / Bounds.Width * (Count / zoom);
        Zoom = zoom;
        ClampOffset();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        _dragOffset = _offset;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Samples is not { Length: > 0 } samples || Bounds.Width <= 0) return;
        var p = e.GetPosition(this);
        if (_dragFrom is { } from)
        {
            _offset = _dragOffset - (p.X - from.X) / Bounds.Width * Visible;
            ClampOffset();
            InvalidateVisual();
        }
        var index = (int)(_offset + p.X / Bounds.Width * Visible);
        ToolTip.SetTip(this, index >= 0 && index < samples.Length
            ? $"Sample {index} · {index * 8 / 1000.0:0.000} ms · 0x{samples[index]:X2}"
            : null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
    }
}
