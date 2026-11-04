using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;

namespace ChameLab.Cli.Plot;

public sealed class WaveformApplication : Application
{
    private static byte[] _samples = [];
    private static int _offset;
    public static void Run(string file, int offset)
    {
        _samples = File.ReadAllBytes(file); _offset = offset;
        AppBuilder.Configure<WaveformApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window
            {
                Title = $"ChameLab LF waveform · {_samples.Length} samples · wheel to zoom, drag to pan, Home to reset",
                Width = 1100, Height = 550, MinWidth = 450, MinHeight = 300,
                Background = new SolidColorBrush(Color.Parse("#111827")),
                Content = new Waveform(_samples, _offset),
            };
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class Waveform(byte[] samples, int offset) : Control
{
    private double _left;
    private double _visible = Math.Max(1, samples.Length);
    private Point? _drag;
    private double _dragLeft;
    private readonly Pen _signal = new(Brushes.LimeGreen, 1);
    private readonly Pen _grid = new(new SolidColorBrush(Color.Parse("#374151")), 1);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = new Rect(55, 20, Math.Max(1, Bounds.Width - 75), Math.Max(1, Bounds.Height - 60));
        for (var value = 0; value <= 255; value += 51)
        {
            var y = rect.Bottom - value / 255d * rect.Height;
            context.DrawLine(_grid, new Point(rect.Left, y), new Point(rect.Right, y));
            Label(context, value.ToString(), new Point(12, y - 8));
        }
        for (var tick = 0; tick <= 8; tick++)
        {
            var x = rect.Left + rect.Width * tick / 8;
            context.DrawLine(_grid, new Point(x, rect.Top), new Point(x, rect.Bottom));
            Label(context, $"{(offset + _left + _visible * tick / 8) * .008:F2}", new Point(x - 14, rect.Bottom + 10));
        }
        Label(context, "ms", new Point(rect.Right - 20, rect.Bottom + 27));
        if (samples.Length == 0) return;
        using var clip = context.PushClip(rect);
        var pixels = (int)Math.Ceiling(rect.Width);
        Point? previous = null;
        for (var px = 0; px < pixels; px++)
        {
            var start = Math.Clamp((int)(_left + px / rect.Width * _visible), 0, samples.Length - 1);
            var end = Math.Clamp((int)Math.Ceiling(_left + (px + 1) / rect.Width * _visible), start + 1, samples.Length);
            var min = 255; var max = 0;
            for (var s = start; s < end; s++) { min = Math.Min(min, samples[s]); max = Math.Max(max, samples[s]); }
            var top = new Point(rect.Left + px, rect.Bottom - max / 255d * rect.Height);
            var bottom = new Point(rect.Left + px, rect.Bottom - min / 255d * rect.Height);
            context.DrawLine(_signal, top, bottom);
            if (previous is { } p) context.DrawLine(_signal, p, top);
            previous = bottom;
        }
    }
    private static void Label(DrawingContext context, string value, Point point) => context.DrawText(
        new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, Brushes.LightGray), point);
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var fraction = Math.Clamp((e.GetPosition(this).X - 55) / Math.Max(1, Bounds.Width - 75), 0, 1);
        var anchor = _left + fraction * _visible;
        _visible = Math.Clamp(_visible * Math.Pow(.8, e.Delta.Y), Math.Min(8, samples.Length), Math.Max(1, samples.Length));
        _left = Math.Clamp(anchor - fraction * _visible, 0, Math.Max(0, samples.Length - _visible));
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    { Focusable = true; Focus(); _drag = e.GetPosition(this); _dragLeft = _left; e.Pointer.Capture(this); }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } start) return;
        _left = Math.Clamp(_dragLeft - (e.GetPosition(this).X - start.X) * _visible / Math.Max(1, Bounds.Width - 75), 0, Math.Max(0, samples.Length - _visible));
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = null; e.Pointer.Capture(null); }
    protected override void OnKeyDown(KeyEventArgs e)
    { if (e.Key == Key.Home) { _left = 0; _visible = Math.Max(1, samples.Length); InvalidateVisual(); } }
}
