using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ChameLab.Behaviors;

/// <summary>What a list page offers <see cref="ListReorder"/> (implemented by its view model).</summary>
public interface IReorderableList
{
    /// <summary>Items can be dragged into place (e.g. only in the manual order).</summary>
    bool CanReorder { get; }

    /// <summary>The items shown, in the list's order.</summary>
    IList ReorderItems { get; }

    /// <summary>Identity of an item that survives the list being rebuilt.</summary>
    string ReorderKey(object item);

    /// <summary>Opens the item that was clicked.</summary>
    void OpenItem(object item);

    /// <summary>Moves an item while it is dragged; <see cref="CommitOrder"/> saves the result.</summary>
    void MoveItem(int from, int to);

    void CommitOrder();
}

/// <summary>
/// A list whose items are dropped onto each other instead of moved (e.g. swapping slots); a
/// view model implementing it next to <see cref="IReorderableList"/> switches <see cref="ListReorder"/>
/// to that mode. The item under the pointer gets the <c>droptarget</c> class while the drag is over it.
/// </summary>
public interface IDropOntoList
{
    /// <summary><paramref name="item"/> may be dropped onto <paramref name="target"/>.</summary>
    bool CanDropOnto(object item, object target);

    /// <summary><paramref name="item"/> was dropped onto <paramref name="target"/>; it slides back to its place.</summary>
    void DropOnto(object item, object target);
}

/// <summary>
/// Opening and dragging items into place in a <see cref="ListBox"/> that sits in its own
/// <see cref="ScrollViewer"/>. A mouse press opens its item on release, so the press can still
/// become a drag; a finger picks an item up after holding still (moving sooner scrolls the list).
/// Items get the <c>dragging</c> class while carried. With an <see cref="IDropOntoList"/> the other
/// items stay put and the dragged one is dropped onto the item under the pointer.
/// </summary>
public sealed class ListReorder
{
    // How far a mouse moves before a press becomes a drag, how long a finger holds still before
    // it picks an item up, and the band at the top and bottom of the list that scrolls it while
    // an item is dragged there.
    private const double DragThreshold = 6;
    private static readonly TimeSpan TouchHold = TimeSpan.FromMilliseconds(400);
    private const double EdgeBand = 36;
    private const double EdgeStep = 12;
    // Items slide to their new places, and a dropped item into its own, this fast.
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(180);

    private readonly ListBox _list;
    private readonly ScrollViewer _scroll;

    private object? _pressedItem;
    private IPointer? _pressPointer;

    private object? _dragItem;
    private IPointer? _dragPointer;
    private Point _dragStart;
    private Point _dragLast;
    // Where in the dragged item it was picked up; the item follows the pointer by it.
    private Vector _grabOffset;
    private bool _dragging;
    // Drop-onto mode: the item the dragged one would be dropped onto.
    private object? _dropTarget;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _edgeTimer;

    public ListReorder(ListBox list, ScrollViewer scroll)
    {
        _list = list;
        _scroll = scroll;

        _holdTimer = new DispatcherTimer { Interval = TouchHold };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_dragItem is not null && _dragPointer is not null) BeginDrag(_dragPointer);
        };
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeTimer.Tick += (_, _) => ScrollAtEdge();

        // Tunnelling, so the drag sees the pointer before the list items and the scroll gestures do.
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Only the list's own capture ending matters (taking it from a list item raises this for the item).
        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) => { if (e.Source == list) EndDrag(); },
            handledEventsToo: true);
    }

    private IReorderableList? Model => _list.DataContext as IReorderableList;
    private IDropOntoList? DropModel => _list.DataContext as IDropOntoList;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        EndDrag();
        _pressedItem = null;
        _pressPointer = null;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: { } item })
            return;
        // Touch already selects on release (and handling the press would stop the list scrolling).
        if (e.Pointer.Type != PointerType.Touch)
        {
            _pressedItem = item;
            _pressPointer = e.Pointer;
            e.Handled = true;
        }
        if (Model is not { CanReorder: true }) return;
        _dragItem = item;
        _dragPointer = e.Pointer;
        _dragStart = _dragLast = e.GetPosition(_list);
        if (e.Pointer.Type == PointerType.Touch) _holdTimer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || e.Pointer != _dragPointer) return;
        _dragLast = e.GetPosition(_list);
        if (!_dragging)
        {
            var moved = _dragLast - _dragStart;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold) return;
            // A finger that moves before the hold is scrolling the list, not picking an item up.
            if (e.Pointer.Type == PointerType.Touch) { CancelPress(); return; }
            BeginDrag(e.Pointer);
            if (!_dragging) return;
        }
        e.PreventGestureRecognition();
        e.Handled = true;
        MoveDragged();
        UpdateEdgeScroll();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A press that was not dragged opens the item, if released over it.
        var open = !_dragging && e.Pointer == _pressPointer ? _pressedItem : null;
        _pressedItem = null;
        _pressPointer = null;
        if (open is not null && Model is { } model && ItemAt(e.GetPosition(_list)) == open)
        {
            e.Handled = true;
            model.OpenItem(open);
        }

        if (e.Pointer != _dragPointer) return;
        if (_dragging)
        {
            e.PreventGestureRecognition();
            e.Handled = true;
        }
        EndDrag();
    }

    private object? ItemAt(Point point)
    {
        if (Model is not { } model) return null;
        for (var i = 0; i < model.ReorderItems.Count; i++)
            if (ItemBounds(i) is { } bounds && bounds.Contains(point)) return model.ReorderItems[i];
        return null;
    }

    /// <summary>
    /// The place the list gives the item at <paramref name="index"/>, in list coordinates
    /// (where it is going, not where a slide or the drag shows it right now).
    /// </summary>
    private Rect? ItemBounds(int index) =>
        _list.ContainerFromIndex(index) is { } item && SlotOf(item) is { } topLeft
            ? new Rect(topLeft, item.Bounds.Size)
            : null;

    private Point? SlotOf(Control item) =>
        item.GetVisualParent() is { } panel ? panel.TranslatePoint(item.Bounds.Position, _list) : null;

    private static Vector ShiftOf(Control item) =>
        item.RenderTransform is TranslateTransform t ? new Vector(t.X, t.Y) : default;

    private void BeginDrag(IPointer pointer)
    {
        if (Model is not { CanReorder: true } || _dragItem is null) { CancelPress(); return; }
        if (_list.ContainerFromItem(_dragItem) is not { } item || SlotOf(item) is not { } slot) { CancelPress(); return; }
        _dragging = true;
        _grabOffset = _dragStart - slot;
        pointer.Capture(_list);
        MarkDragged(true);
        FollowPointer();
    }

    /// <summary>Keeps the dragged item under the pointer, wherever its place in the list is.</summary>
    private void FollowPointer()
    {
        if (_dragItem is null || _list.ContainerFromItem(_dragItem) is not { } item || SlotOf(item) is not { } slot)
            return;
        // A plain transform: one still sliding from before would lag behind the pointer.
        if (item.RenderTransform is not TranslateTransform { Transitions: null } t)
            item.RenderTransform = t = new TranslateTransform();
        var shift = _dragLast - _grabOffset - slot;
        t.X = shift.X;
        t.Y = shift.Y;
    }

    /// <summary>Where each shown item is on screen now, by key.</summary>
    private Dictionary<string, Point> Snapshot()
    {
        var shown = new Dictionary<string, Point>();
        if (Model is not { } model) return shown;
        foreach (var entry in model.ReorderItems)
            if (entry is not null && _list.ContainerFromItem(entry) is { } item && SlotOf(item) is { } slot)
                shown[model.ReorderKey(entry)] = slot + ShiftOf(item);
        return shown;
    }

    /// <summary>
    /// After the list changed: slides every item from where it was on screen to its new place
    /// (the dragged item stays with the pointer).
    /// </summary>
    private void SlideFrom(Dictionary<string, Point> shown)
    {
        if (Model is not { } model) return;
        _list.UpdateLayout();
        foreach (var entry in model.ReorderItems)
        {
            if (entry is null || _dragging && ReferenceEquals(entry, _dragItem)) continue;
            if (_list.ContainerFromItem(entry) is not { } item || SlotOf(item) is not { } slot) continue;
            if (!shown.TryGetValue(model.ReorderKey(entry), out var was)) continue;
            var from = was - slot;
            if (Math.Abs(from.X) < 0.5 && Math.Abs(from.Y) < 0.5) { item.RenderTransform = null; continue; }
            var t = new TranslateTransform(from.X, from.Y);
            item.RenderTransform = t;
            // Transitions come after the start offset, so only the way back to the slot animates.
            t.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.XProperty, Duration = SlideTime, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = SlideTime, Easing = new CubicEaseOut() },
            };
            t.X = 0;
            t.Y = 0;
        }
    }

    /// <summary>Moves the dragged item to the place of the item under the pointer.</summary>
    private void MoveDragged()
    {
        if (Model is not { } model || _dragItem is null) return;
        if (DropModel is { } drop)
        {
            var under = ItemAt(_dragLast);
            SetDropTarget(under is not null && !ReferenceEquals(under, _dragItem) && drop.CanDropOnto(_dragItem, under)
                ? under
                : null);
            FollowPointer();
            return;
        }
        var from = model.ReorderItems.IndexOf(_dragItem);
        if (from < 0) { EndDrag(); return; }
        for (var i = 0; i < model.ReorderItems.Count; i++)
        {
            if (i == from || ItemBounds(i) is not { } bounds || !bounds.Contains(_dragLast)) continue;
            var shown = Snapshot();
            MarkDragged(false);
            model.MoveItem(from, i);
            MarkDragged(true);
            SlideFrom(shown);
            break;
        }
        FollowPointer();
    }

    private void UpdateEdgeScroll()
    {
        if (EdgeDirection() != 0) { if (!_edgeTimer.IsEnabled) _edgeTimer.Start(); }
        else _edgeTimer.Stop();
    }

    /// <summary>-1 / 1 while the dragged item is held at the top / bottom edge of the list, else 0.</summary>
    private int EdgeDirection()
    {
        if (_list.TranslatePoint(_dragLast, _scroll) is not { } p) return 0;
        if (p.Y < EdgeBand && _scroll.Offset.Y > 0) return -1;
        if (p.Y > _scroll.Viewport.Height - EdgeBand
            && _scroll.Offset.Y < _scroll.Extent.Height - _scroll.Viewport.Height) return 1;
        return 0;
    }

    private void ScrollAtEdge()
    {
        var direction = EdgeDirection();
        if (!_dragging || direction == 0) { _edgeTimer.Stop(); return; }
        // The pointer stays put on screen, so it moves over the list by as much as the list scrolls.
        var before = _scroll.Offset.Y;
        _scroll.Offset = _scroll.Offset.WithY(Math.Max(0, before + direction * EdgeStep));
        _dragLast = _dragLast.WithY(_dragLast.Y + _scroll.Offset.Y - before);
        MoveDragged();
    }

    private void SetDropTarget(object? target)
    {
        if (ReferenceEquals(target, _dropTarget)) return;
        if (_dropTarget is not null && _list.ContainerFromItem(_dropTarget) is { } old) old.Classes.Set("droptarget", false);
        _dropTarget = target;
        if (target is not null && _list.ContainerFromItem(target) is { } item) item.Classes.Set("droptarget", true);
    }

    private void MarkDragged(bool on)
    {
        if (_dragItem is not null && _list.ContainerFromItem(_dragItem) is { } item)
            item.Classes.Set("dragging", on);
    }

    /// <summary>Forgets a press that never became a drag.</summary>
    private void CancelPress()
    {
        _holdTimer.Stop();
        _dragItem = null;
        _dragPointer = null;
    }

    /// <summary>Drops the dragged item where it is and saves the new order.</summary>
    private void EndDrag()
    {
        _holdTimer.Stop();
        _edgeTimer.Stop();
        if (!_dragging) { CancelPress(); return; }
        var shown = Snapshot();
        var dropped = _dragItem;
        var target = _dropTarget;
        SetDropTarget(null);
        _dragging = false;
        MarkDragged(false);
        var pointer = _dragPointer;
        CancelPress();
        if (pointer?.Captured == _list) pointer.Capture(null);
        if (DropModel is { } drop)
        {
            // The dragged item glides back to its place; the drop acts on the two items.
            SlideFrom(shown);
            KeepOnTop(dropped);
            if (dropped is not null && target is not null) drop.DropOnto(dropped, target);
            return;
        }
        // Saving may rebuild the list; either way the dropped item glides into its place.
        Model?.CommitOrder();
        SlideFrom(shown);
        KeepOnTop(dropped);
    }

    /// <summary>Keeps the dropped item above the others while it slides into place.</summary>
    private void KeepOnTop(object? dropped)
    {
        if (Model is not { } model || dropped is null) return;
        // After saving, the list holds a new item for the same entry.
        var key = model.ReorderKey(dropped);
        var entry = model.ReorderItems.Cast<object?>().FirstOrDefault(i => i is not null && model.ReorderKey(i) == key);
        if (entry is null || _list.ContainerFromItem(entry) is not { } item) return;
        item.ZIndex = 1;
        // Cleared rather than set to 0: a local value would outrank the dragging style's ZIndex next time.
        DispatcherTimer.RunOnce(() => item.ClearValue(Visual.ZIndexProperty), SlideTime);
    }
}
