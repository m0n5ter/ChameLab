using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ChameLab.Controls;

/// <summary>
/// Plot of a raw LF capture: one 8-bit ADC sample per carrier cycle against time, with the decode threshold,
/// the gap threshold and the samples below it shaded, a cursor, and under it an overview of the whole capture
/// with the part in view framed.
/// <para>
/// <see cref="Zoom"/> magnifies the time axis: the wheel, a two-finger pinch or a touchpad pinch zoom around
/// where they happen, dragging pans, and dragging in the overview moves the view. The mouse places the cursor
/// as it moves, a tap places it on touch screens.
/// </para>
/// <para>
/// Drawing costs follow the width, not the capture: zoomed out, each pixel column shows the range of its
/// samples from a min / max pyramid built once per capture; zoomed in, only the samples in view are drawn.
/// The geometry is kept until the view changes, so moving the cursor redraws only the cursor.
/// </para>
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
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, z) => Math.Clamp(z, 1, MaxZoom));

    /// <summary>Sample under the cursor; -1 for none.</summary>
    public static readonly StyledProperty<int> CursorIndexProperty =
        AvaloniaProperty.Register<LfWaveform, int>(nameof(CursorIndex), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(LineBrush), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> ThresholdBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(ThresholdBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> GapBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(GapBrush), Brushes.IndianRed);

    public static readonly StyledProperty<IBrush?> CursorBrushProperty =
        AvaloniaProperty.Register<LfWaveform, IBrush?>(nameof(CursorBrush), Brushes.Orange);

    public const double MaxZoom = 64;

    /// <summary>Height of the overview strip under the plot, and the gap above it.</summary>
    private const double OverviewHeight = 18, OverviewGap = 6;

    /// <summary>Index of the sample at the left edge.</summary>
    private double _offset;

    /// <summary>Pointers down: on the plot one pans and two pinch; on the overview one moves the view.</summary>
    private readonly Dictionary<IPointer, (Point At, bool Overview, bool Moved)> _pointers = new();

    // Per capture: min / max of buckets of 2, 4, 8… samples, and the stretches below the gap threshold.
    private (byte[] Min, byte[] Max)[] _pyramid = [];
    private List<(int Start, int End)> _gaps = [];

    // The plot as last drawn, kept while the view is the same.
    private (double Offset, double Visible, Size Size, byte[]? Samples) _drawnFor;
    private Geometry? _signal, _overview;

    static LfWaveform()
    {
        AffectsRender<LfWaveform>(SamplesProperty, ThresholdProperty, GapThresholdProperty, ZoomProperty,
            CursorIndexProperty, LineBrushProperty, ThresholdBrushProperty, GapBrushProperty, CursorBrushProperty);
        ClipToBoundsProperty.OverrideDefaultValue<LfWaveform>(true);
    }

    public LfWaveform()
    {
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            ZoomAround(e.GetPosition(this).X, 1 + e.Delta.X);
            e.Handled = true;
        });
    }

    public byte[]? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public int Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public int GapThreshold { get => GetValue(GapThresholdProperty); set => SetValue(GapThresholdProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public int CursorIndex { get => GetValue(CursorIndexProperty); set => SetValue(CursorIndexProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? ThresholdBrush { get => GetValue(ThresholdBrushProperty); set => SetValue(ThresholdBrushProperty, value); }
    public IBrush? GapBrush { get => GetValue(GapBrushProperty); set => SetValue(GapBrushProperty, value); }
    public IBrush? CursorBrush { get => GetValue(CursorBrushProperty); set => SetValue(CursorBrushProperty, value); }

    private int Count => Samples?.Length ?? 0;
    /// <summary>Samples in view.</summary>
    private double Visible => Count / Zoom;
    private double PlotHeight => Math.Max(0, Bounds.Height - OverviewHeight - OverviewGap);
    private double OverviewTop => Bounds.Height - OverviewHeight;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SamplesProperty)
        {
            _offset = 0;
            _pyramid = BuildPyramid(Samples ?? []);
            _gaps = FindGaps(Samples ?? [], GapThreshold);
            _signal = _overview = null;
        }
        else if (change.Property == GapThresholdProperty) _gaps = FindGaps(Samples ?? [], GapThreshold);
        else if (change.Property == ZoomProperty) ClampOffset();
    }

    private void ClampOffset() => _offset = Math.Clamp(_offset, 0, Math.Max(0, Count - Visible));

    private double X(double sample) => (sample - _offset) / Visible * Bounds.Width;
    private double Y(int value) => 4 + (1 - value / 255.0) * (PlotHeight - 8);

    // ---- Precomputed per capture ---------------------------------------

    /// <summary>Level k holds the min and max of each bucket of 2^(k+1) samples.</summary>
    private static (byte[] Min, byte[] Max)[] BuildPyramid(byte[] samples)
    {
        var levels = new List<(byte[], byte[])>();
        byte[] min = samples, max = samples;
        while (min.Length > 1)
        {
            var n = (min.Length + 1) / 2;
            var nextMin = new byte[n];
            var nextMax = new byte[n];
            for (var i = 0; i < n; i++)
            {
                var j = Math.Min(2 * i + 1, min.Length - 1);
                nextMin[i] = Math.Min(min[2 * i], min[j]);
                nextMax[i] = Math.Max(max[2 * i], max[j]);
            }
            levels.Add((nextMin, nextMax));
            (min, max) = (nextMin, nextMax);
        }
        return levels.ToArray();
    }

    private static List<(int, int)> FindGaps(byte[] samples, int threshold)
    {
        var gaps = new List<(int, int)>();
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] >= threshold) continue;
            var start = i;
            while (i < samples.Length && samples[i] < threshold) i++;
            gaps.Add((start, i));
        }
        return gaps;
    }

    /// <summary>Min and max of samples [from, to), read from the coarsest pyramid level that still fits.</summary>
    private (byte Min, byte Max) Range(byte[] samples, int from, int to)
    {
        var span = to - from;
        var level = span >= 4 ? Math.Min(_pyramid.Length, (int)Math.Log2(span) - 1) : 0;
        byte min = 255, max = 0;
        if (level == 0)
        {
            for (var i = from; i < to; i++)
            {
                min = Math.Min(min, samples[i]);
                max = Math.Max(max, samples[i]);
            }
            return (min, max);
        }
        var (lmin, lmax) = _pyramid[level - 1];
        var shift = level;
        // Buckets fully or partly inside: a little wider than asked at the edges, invisible at this scale.
        for (int b = from >> shift, last = Math.Min(lmin.Length - 1, (to - 1) >> shift); b <= last; b++)
        {
            min = Math.Min(min, lmin[b]);
            max = Math.Max(max, lmax[b]);
        }
        return (min, max);
    }

    // ---- Drawing --------------------------------------------------------

    /// <summary>The signal as a filled band of each pixel column's range, or a step line once zoomed in that far.</summary>
    private Geometry BuildSignal(byte[] samples)
    {
        var width = Bounds.Width;
        var perPixel = Visible / width;
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        if (perPixel <= 1)
        {
            var first = (int)Math.Floor(_offset);
            var last = Math.Min(samples.Length - 1, (int)Math.Ceiling(_offset + Visible));
            g.BeginFigure(new Point(X(first), Y(samples[first])), false);
            for (var i = first; i <= last; i++)
            {
                g.LineTo(new Point(X(i), Y(samples[i])));
                g.LineTo(new Point(X(i + 1), Y(samples[i])));
            }
            g.EndFigure(false);
            return geometry;
        }

        var columns = (int)Math.Ceiling(width);
        var lows = new double[columns];
        var started = false;
        for (var px = 0; px < columns; px++)
        {
            var from = (int)(_offset + px * perPixel);
            var to = Math.Min(samples.Length, (int)(_offset + (px + 1) * perPixel) + 1);
            if (from >= to) { lows[px] = double.NaN; continue; }
            var (min, max) = Range(samples, from, to);
            var top = Y(max);
            lows[px] = Math.Max(Y(min), top + 1.2);
            if (!started) { g.BeginFigure(new Point(px, top), true); started = true; }
            g.LineTo(new Point(px, top));
            g.LineTo(new Point(px + 1, top));
        }
        for (var px = columns - 1; px >= 0; px--)
        {
            if (double.IsNaN(lows[px])) continue;
            g.LineTo(new Point(px + 1, lows[px]));
            g.LineTo(new Point(px, lows[px]));
        }
        if (started) g.EndFigure(true);
        return geometry;
    }

    /// <summary>The whole capture squeezed into the overview strip.</summary>
    private Geometry BuildOverview(byte[] samples)
    {
        var width = Bounds.Width;
        var perPixel = samples.Length / width;
        var top = OverviewTop;
        double Oy(int v) => top + 2 + (1 - v / 255.0) * (OverviewHeight - 4);
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        var columns = (int)Math.Ceiling(width);
        var lows = new double[columns];
        g.BeginFigure(new Point(0, Oy(samples[0])), true);
        for (var px = 0; px < columns; px++)
        {
            var from = Math.Min(samples.Length - 1, (int)(px * perPixel));
            var to = Math.Min(samples.Length, Math.Max(from + 1, (int)((px + 1) * perPixel)));
            var (min, max) = Range(samples, from, to);
            g.LineTo(new Point(px, Oy(max)));
            g.LineTo(new Point(px + 1, Oy(max)));
            lows[px] = Math.Max(Oy(min), Oy(max) + 1);
        }
        for (var px = columns - 1; px >= 0; px--)
        {
            g.LineTo(new Point(px + 1, lows[px]));
            g.LineTo(new Point(px, lows[px]));
        }
        g.EndFigure(true);
        return geometry;
    }

    public override void Render(DrawingContext context)
    {
        // Filled all over, so the pointer lands on the plot between the lines too.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Samples is not { Length: > 1 } samples || Bounds.Width <= 0 || PlotHeight <= 0) return;
        var width = Bounds.Width;

        var view = (_offset, Visible, Bounds.Size, samples);
        if (_signal is null || _overview is null || view != _drawnFor)
        {
            _signal = BuildSignal(samples);
            if (_overview is null || view.Size != _drawnFor.Size || !ReferenceEquals(samples, _drawnFor.Samples))
                _overview = BuildOverview(samples);
            _drawnFor = view;
        }

        using (context.PushClip(new Rect(0, 0, width, PlotHeight)))
        {
            // Shaded gaps: only those in view, found by binary search.
            if (GapBrush is ISolidColorBrush gapBrush && _gaps.Count > 0)
            {
                var shade = new ImmutableSolidColorBrush(gapBrush.Color, 0.18);
                var at = _gaps.BinarySearch((Math.Max(0, (int)_offset), int.MinValue));
                for (var i = Math.Max(0, at < 0 ? ~at - 1 : at); i < _gaps.Count; i++)
                {
                    var (start, end) = _gaps[i];
                    if (start > _offset + Visible) break;
                    if (end < _offset) continue;
                    var x0 = Math.Max(0, X(start));
                    context.FillRectangle(shade, new Rect(x0, 0, Math.Max(1, Math.Min(width, X(end)) - x0), PlotHeight));
                }
            }

            if (ThresholdBrush is { } tb)
                context.DrawLine(new Pen(tb, 1, new DashStyle([4, 4], 0)),
                    new Point(0, Y(Threshold)), new Point(width, Y(Threshold)));
            if (GapBrush is { } gb)
                context.DrawLine(new Pen(gb, 1, new DashStyle([2, 4], 0)),
                    new Point(0, Y(GapThreshold)), new Point(width, Y(GapThreshold)));

            if (LineBrush is { } line)
            {
                if (Visible / width <= 1) context.DrawGeometry(null, new Pen(line, 1.4), _signal);
                else context.DrawGeometry(line, null, _signal);
            }

            if (CursorBrush is { } cb && CursorIndex >= 0 && CursorIndex < samples.Length)
            {
                var x = X(CursorIndex + 0.5);
                if (x >= 0 && x <= width)
                {
                    context.DrawLine(new Pen(cb, 1), new Point(x, 0), new Point(x, PlotHeight));
                    context.DrawEllipse(cb, null, new Point(x, Y(samples[CursorIndex])), 3.5, 3.5);
                }
            }
        }

        // Overview: the whole capture, the part in view framed.
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.Gray, 0.12),
            new Rect(0, OverviewTop, width, OverviewHeight), 4);
        if (ThresholdBrush is { } ob) context.DrawGeometry(ob, null, _overview);
        if (LineBrush is ISolidColorBrush accent)
        {
            var left = _offset / samples.Length * width;
            var right = (_offset + Visible) / samples.Length * width;
            var frame = new Rect(left, OverviewTop, Math.Max(4, right - left), OverviewHeight);
            context.DrawRectangle(new ImmutableSolidColorBrush(accent.Color, 0.25),
                new Pen(accent, 1.5), frame, 4, 4);
        }
    }

    // ---- Input ----------------------------------------------------------

    /// <summary>Multiplies the zoom by <paramref name="factor"/>, keeping the sample under <paramref name="x"/> in place.</summary>
    private void ZoomAround(double x, double factor)
    {
        if (Count == 0 || Bounds.Width <= 0 || factor <= 0) return;
        var anchor = _offset + x / Bounds.Width * Visible;
        var zoom = Math.Clamp(Zoom * factor, 1, MaxZoom);
        _offset = anchor - x / Bounds.Width * (Count / zoom);
        Zoom = zoom;
        ClampOffset();
        InvalidateVisual();
    }

    /// <summary>Centers the view on the part of the capture under <paramref name="x"/> in the overview.</summary>
    private void CenterOn(double x)
    {
        _offset = x / Bounds.Width * Count - Visible / 2;
        ClampOffset();
        InvalidateVisual();
    }

    private int SampleAt(double x) => (int)(_offset + x / Bounds.Width * Visible);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAround(e.GetPosition(this).X, Math.Pow(1.25, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_pointers.Count >= 2 || Count == 0) return;
        var p = e.GetPosition(this);
        var overview = p.Y >= OverviewTop - OverviewGap / 2;
        _pointers[e.Pointer] = (p, overview, false);
        if (overview) CenterOn(p.X);
        e.Pointer.Capture(this);
        // A finger drag pans the plot instead of scrolling the page.
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Samples is not { Length: > 0 } samples || Bounds.Width <= 0) return;
        var p = e.GetPosition(this);
        if (_pointers.TryGetValue(e.Pointer, out var down))
        {
            if (down.Overview) CenterOn(p.X);
            else if (_pointers.Count == 1)
            {
                _offset -= (p.X - down.At.X) / Bounds.Width * Visible;
                ClampOffset();
                InvalidateVisual();
            }
            else
            {
                // Pinch: the spread between the fingers sets the zoom, around their midpoint.
                var other = _pointers.First(kv => kv.Key != e.Pointer).Value.At;
                var before = Math.Abs(down.At.X - other.X);
                var after = Math.Abs(p.X - other.X);
                if (before > 8 && after > 8) ZoomAround((p.X + other.X) / 2, after / before);
            }
            _pointers[e.Pointer] = (p, down.Overview, down.Moved || Math.Abs(p.X - down.At.X) > 3);
            e.PreventGestureRecognition();
            e.Handled = true;
        }
        // The mouse carries the cursor; on touch a tap places it.
        if (e.Pointer.Type == PointerType.Mouse && p.Y < PlotHeight)
            CursorIndex = Math.Clamp(SampleAt(p.X), 0, samples.Length - 1);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointers.Remove(e.Pointer, out var down) && down is { Overview: false, Moved: false } && _pointers.Count == 0
            && Samples is { Length: > 0 } samples)
            CursorIndex = Math.Clamp(SampleAt(e.GetPosition(this).X), 0, samples.Length - 1);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pointers.Remove(e.Pointer);
    }
}
