using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ChameLab.Controls;

/// <summary>
/// Shows its child on one line; when the child is wider than the space, slides it to the end and back,
/// pausing at each end. A child that fits stays still.
/// </summary>
public class Marquee : Decorator
{
    private const double Speed = 30; // px per second
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);

    private readonly TranslateTransform _shift = new();
    private double _overflow;
    private TimeSpan? _start;
    private bool _running;

    public Marquee()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        return new Size(Math.Min(child.DesiredSize.Width, availableSize.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child) return finalSize;
        if (!Equals(child.RenderTransform, _shift)) child.RenderTransform = _shift;
        child.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, child.DesiredSize.Width), finalSize.Height));
        _overflow = child.DesiredSize.Width - finalSize.Width;
        if (_overflow > 0.5) Start();
        else Stop();
        return finalSize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _start = null;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Tick);
    }

    private void Stop()
    {
        _running = false;
        _shift.X = 0;
    }

    private void Tick(TimeSpan now)
    {
        // Hidden: rest until the next arrange brings it back.
        if (!_running || !IsEffectivelyVisible) { Stop(); return; }
        _start ??= now;
        var travel = _overflow / Speed;
        var pause = Pause.TotalSeconds;
        var t = (now - _start.Value).TotalSeconds % (2 * (pause + travel));
        var x = t < pause ? 0
            : t < pause + travel ? (t - pause) * Speed
            : t < 2 * pause + travel ? _overflow
            : _overflow - (t - 2 * pause - travel) * Speed;
        _shift.X = -Math.Clamp(x, 0, _overflow);
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Tick);
    }
}
