using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;

namespace ChameLab.Controls;

/// <summary>
/// Shared touch-selection pieces (drop-shaped end handles and floating action bar) used by
/// custom touch-selectable surfaces such as <see cref="HexEditor"/> and <see cref="TouchSelectableText"/>.
/// </summary>
internal static class TouchSelection
{
    public const double TapSlop = 10;

    /// <summary>Non-focusable button for the floating bar so tapping it never takes focus (or opens a keyboard).</summary>
    public static Button CreateCommand(string text, Action action)
    {
        var button = new Button { Content = text, Focusable = false };
        button.Classes.Add("theme-subtle");
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Floating bar holding Copy / Paste / Select all over a touch selection.</summary>
    public static Border CreateBar(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) row.Children.Add(button);
        var bar = new Border { IsVisible = false, Child = row };
        bar.Classes.Add("touchbar");
        return bar;
    }

    /// <summary>
    /// Places the floating bar above the selection if it fits inside <paramref name="viewport"/>,
    /// otherwise below the end handle, otherwise pinned near the top of the visible area.
    /// </summary>
    public static void ArrangeBar(Border bar, Point startTip, Point endTip, double lineHeight, double handleHeight, bool sameLine, Rect viewport)
    {
        const double gap = 8;
        var barSize = bar.DesiredSize;
        var top = startTip.Y - lineHeight - gap - barSize.Height;
        var below = endTip.Y + handleHeight + gap;
        var y = top >= viewport.Top ? top
            : below + barSize.Height <= viewport.Bottom ? below
            : viewport.Top + gap;
        var center = sameLine ? (startTip.X + endTip.X) / 2 : viewport.Center.X;
        var x = Math.Max(viewport.Left, Math.Min(center - barSize.Width / 2, viewport.Right - barSize.Width));
        bar.Arrange(new Rect(new Point(x, y), barSize));
    }
}

/// <summary>
/// A drop-shaped handle under one end of a touch selection, pointing at it with its square
/// corner; dragging it moves that end. Its touch area is larger than the drop.
/// </summary>
internal sealed class TouchSelectionHandle : Control, ICustomHitTest
{
    private const double Radius = 11, BoxWidth = 44, BoxHeight = 40;
    private readonly Control _owner;
    private readonly bool _start;
    private readonly Func<IBrush?> _getBrush;
    private readonly Action<bool, Point> _beginDrag;
    private readonly Action<Point> _drag;
    private readonly Action _endDrag;

    public TouchSelectionHandle(
        Control owner,
        bool start,
        Func<IBrush?> getBrush,
        Action<bool, Point> beginDrag,
        Action<Point> drag,
        Action endDrag)
    {
        _owner = owner;
        _start = start;
        _getBrush = getBrush;
        _beginDrag = beginDrag;
        _drag = drag;
        _endDrag = endDrag;
        // The tip at the drop's square corner: right of a start handle's drop, left of an end one's.
        Tip = new Vector(start ? BoxWidth - 14 : 14, 0);
    }

    /// <summary>Where on the handle its tip is.</summary>
    public Vector Tip { get; }

    public bool HitTest(Point point) => IsVisible && new Rect(Bounds.Size).Contains(point);

    protected override Size MeasureOverride(Size availableSize) => new(BoxWidth, BoxHeight);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_getBrush() is not { } brush) return;
        var center = new Point(Tip.X + (_start ? -Radius : Radius), Radius);
        context.DrawEllipse(brush, null, center, Radius, Radius);
        context.FillRectangle(brush, new Rect(Math.Min(Tip.X, center.X), 0, Radius, Radius));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        e.PreventGestureRecognition();
        e.Pointer.Capture(this);
        _beginDrag(_start, e.GetPosition(_owner));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured != this) return;
        e.Handled = true;
        e.PreventGestureRecognition();
        _drag(e.GetPosition(_owner));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured != this) return;
        e.Handled = true;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _endDrag();
    }
}
