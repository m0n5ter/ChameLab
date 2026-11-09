using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior: <c>b:Pulse.IsActive="True"</c> makes a small visual breathe (fade and grow,
/// then back) for as long as the flag is on, to draw the eye to something waiting, such as the top
/// bar's app update icon.
/// </summary>
/// <remarks>
/// Stepped by a 10 Hz timer instead of a style animation: an animation ticks at the display's
/// rate, and every tick redraws the whole window. On a 120 Hz phone that kept the render thread
/// busy half of each second and made scrolling judder. Ten steps a second still read as a pulse
/// on an icon this size.
/// </remarks>
public static class Pulse
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("IsActive", typeof(Pulse));

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private const double PeriodSeconds = 1.6;
    private const double MinOpacity = 0.35;
    private const double MaxScale = 1.3;

    private static readonly HashSet<Visual> Active = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;

    static Pulse()
    {
        IsActiveProperty.Changed.AddClassHandler<Visual>((visual, e) =>
        {
            // Off the visual tree it rests, so a removed icon doesn't keep the timer running.
            if (e.NewValue is true)
            {
                visual.AttachedToVisualTree += OnTreeChanged;
                visual.DetachedFromVisualTree += OnTreeChanged;
            }
            else
            {
                visual.AttachedToVisualTree -= OnTreeChanged;
                visual.DetachedFromVisualTree -= OnTreeChanged;
            }
            Update(visual);
        });
    }

    public static bool GetIsActive(Visual element) => element.GetValue(IsActiveProperty);
    public static void SetIsActive(Visual element, bool value) => element.SetValue(IsActiveProperty, value);

    private static void OnTreeChanged(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual visual) Update(visual);
    }

    private static void Update(Visual visual)
    {
        var on = GetIsActive(visual) && Avalonia.Controls.TopLevel.GetTopLevel(visual) is not null;
        if (on && Active.Add(visual))
        {
            visual.RenderTransformOrigin = RelativePoint.Center;
            visual.RenderTransform = new ScaleTransform();
            Apply(visual, Phase());
            _timer ??= StartTimer();
        }
        else if (!on && Active.Remove(visual))
        {
            visual.ClearValue(Visual.OpacityProperty);
            visual.ClearValue(Visual.RenderTransformProperty);
            visual.ClearValue(Visual.RenderTransformOriginProperty);
            if (Active.Count == 0)
            {
                _timer?.Stop();
                _timer = null;
            }
        }
    }

    private static DispatcherTimer StartTimer()
    {
        var timer = new DispatcherTimer { Interval = Step };
        timer.Tick += (_, _) =>
        {
            var phase = Phase();
            foreach (var visual in Active)
                if (visual.IsEffectivelyVisible) Apply(visual, phase);
        };
        timer.Start();
        return timer;
    }

    /// <summary>0 at rest, rising to 1 halfway through the period and easing back.</summary>
    private static double Phase() =>
        0.5 - 0.5 * Math.Cos(Clock.Elapsed.TotalSeconds / PeriodSeconds * 2 * Math.PI);

    private static void Apply(Visual visual, double phase)
    {
        visual.Opacity = 1 - (1 - MinOpacity) * phase;
        if (visual.RenderTransform is ScaleTransform scale)
            scale.ScaleX = scale.ScaleY = 1 + (MaxScale - 1) * phase;
    }
}
