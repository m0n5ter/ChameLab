using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ChameLab.Controls;

/// <summary>
/// Shows its child on one line; when the child is wider than the space, slides it to the end and back,
/// pausing at each end. A child that fits stays still.
/// </summary>
/// <remarks>
/// Frames are requested only while the child slides: the pauses wait on a timer, and a marquee
/// scrolled out of view finishes its slide and waits at that end until it comes back. Every frame
/// redraws the whole window, so a marquee asking for frames while still kept the app rendering at
/// the display's rate (120 Hz on some phones) for nothing.
/// </remarks>
public class Marquee : Decorator
{
    private const double Speed = 30; // px per second
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.5);

    private readonly TranslateTransform _shift = new();
    private double _overflow;
    private bool _running;
    private bool _forward;
    private bool _inView = true;
    private bool _waitingForView;
    private TimeSpan? _slideStart;
    private IDisposable? _pauseTimer;

    public Marquee()
    {
        ClipToBounds = true;
        EffectiveViewportChanged += (_, e) =>
        {
            _inView = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
            if (_inView && _waitingForView) BeginSlide();
        };
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
        if (_overflow > 0.5)
        {
            // Resting past a new, shorter end would cut the text off.
            if (-_shift.X > _overflow) _shift.X = -_overflow;
            Start();
        }
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
        _forward = true;
        _shift.X = 0;
        Rest();
    }

    private void Stop()
    {
        _running = false;
        _waitingForView = false;
        _slideStart = null;
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        _shift.X = 0;
    }

    /// <summary>Holds still at an end, without asking for frames, then slides on.</summary>
    private void Rest()
    {
        _pauseTimer?.Dispose();
        _pauseTimer = DispatcherTimer.RunOnce(BeginSlide, Pause);
    }

    private void BeginSlide()
    {
        _pauseTimer = null;
        _waitingForView = false;
        if (!_running) return;
        // Hidden: rest until the next arrange brings it back.
        if (!IsEffectivelyVisible) { Stop(); return; }
        if (!_inView) { _waitingForView = true; return; }
        _slideStart = null;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Tick);
    }

    private void Tick(TimeSpan now)
    {
        if (!_running) return;
        if (!IsEffectivelyVisible) { Stop(); return; }
        _slideStart ??= now;
        var travelled = (now - _slideStart.Value).TotalSeconds * Speed;
        if (travelled >= _overflow)
        {
            _shift.X = _forward ? -_overflow : 0;
            _forward = !_forward;
            Rest();
            return;
        }
        _shift.X = _forward ? -travelled : travelled - _overflow;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Tick);
    }
}
