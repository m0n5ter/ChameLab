using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using ChameLab.Localization;

namespace ChameLab.Controls;

/// <summary>
/// A <see cref="SelectableTextBlock"/> with mobile touch selection: a long press selects the word
/// under the finger (dragging on extends it), drop handles adjust either end, and a floating bar
/// offers Copy and Select all without stealing focus or fighting the surrounding scroll viewer.
/// Mouse input passes straight through to <see cref="SelectableTextBlock"/>.
/// </summary>
public sealed class TouchSelectableText : Panel
{
    public static readonly StyledProperty<IBrush?> HandleBrushProperty =
        AvaloniaProperty.Register<TouchSelectableText, IBrush?>(nameof(HandleBrush));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<TouchSelectableText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<TouchSelectableText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<TouchSelectableText>();

    public static readonly StyledProperty<string?> TextProperty =
        TextBlock.TextProperty.AddOwner<TouchSelectableText>();

    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        TextBlock.TextWrappingProperty.AddOwner<TouchSelectableText>();

    public static readonly StyledProperty<Thickness> PaddingProperty =
        TextBlock.PaddingProperty.AddOwner<TouchSelectableText>();

    private readonly InnerSelectableTextBlock _textBlock;
    private readonly TouchSelectionLayer _touchLayer;
    private Rect _viewport;

    private bool _touchSelect, _touchBarShown;
    private bool _touchDown, _touchHeld, _touchExtending, _touchExtendMoved;
    private IPointer? _touchPointer;
    private Point _touchPressPoint;
    private int _touchWordStart, _touchWordEnd;

    private bool _handleIsStart;
    private int _handleAnchor;
    private Vector _handleGrab;

    public TouchSelectableText()
    {
        Background = Brushes.Transparent;
        SetValue(IsHoldingEnabledProperty, true);

        _textBlock = new InnerSelectableTextBlock(this);
        _touchLayer = new TouchSelectionLayer(this) { ZIndex = 1 };
        Children.Add(_textBlock);
        Children.Add(_touchLayer);

        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            if (_touchLayer.Bar.IsVisible) _touchLayer.InvalidateArrange();
        };

        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (!_touchDown) return;
            e.Handled = true;
            BeginTouchSelection();
        });
    }

    public IBrush? HandleBrush
    {
        get => GetValue(HandleBrushProperty);
        set => SetValue(HandleBrushProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public InlineCollection? Inlines
    {
        get => _textBlock.Inlines;
        set
        {
            _textBlock.Inlines = value;
            SyncAfterTextChange();
        }
    }

    public int SelectionStart
    {
        get => _textBlock.SelectionStart;
        set => _textBlock.SelectionStart = value;
    }

    public int SelectionEnd
    {
        get => _textBlock.SelectionEnd;
        set => _textBlock.SelectionEnd = value;
    }

    public string SelectedText => _textBlock.SelectedText;

    public void Copy() => _textBlock.Copy();

    public void SelectAll()
    {
        _textBlock.SelectAll();
        if (HasSelection)
        {
            _touchSelect = true;
            _touchBarShown = true;
            UpdateTouchUi();
        }
    }

    public void ClearSelection()
    {
        _textBlock.ClearSelection();
        EndTouchSelection();
    }

    private bool HasSelection => _textBlock.SelectionStart != _textBlock.SelectionEnd && CurrentTextLength() > 0;

    private (int From, int To) OrderedSelection()
    {
        var a = _textBlock.SelectionStart;
        var b = _textBlock.SelectionEnd;
        var len = CurrentTextLength();
        var from = Math.Clamp(Math.Min(a, b), 0, len);
        var to = Math.Clamp(Math.Max(a, b), 0, len);
        return (from, to);
    }

    private string CurrentText() => (_textBlock.Inlines is { Count: > 0 } inlines ? inlines.Text : _textBlock.Text) ?? "";

    private int CurrentTextLength() => CurrentText().Length;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty) _textBlock.FontFamily = FontFamily;
        else if (change.Property == FontSizeProperty) _textBlock.FontSize = FontSize;
        else if (change.Property == ForegroundProperty) _textBlock.Foreground = Foreground;
        else if (change.Property == TextProperty)
        {
            _textBlock.Text = Text;
            SyncAfterTextChange();
        }
        else if (change.Property == TextWrappingProperty)
        {
            _textBlock.TextWrapping = TextWrapping;
            if (_touchSelect) _touchLayer.InvalidateArrange();
        }
        else if (change.Property == PaddingProperty)
        {
            _textBlock.Padding = Padding;
            if (_touchSelect) _touchLayer.InvalidateArrange();
        }
        else if (change.Property == HandleBrushProperty)
        {
            _touchLayer.Start.InvalidateVisual();
            _touchLayer.End.InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _textBlock.Measure(availableSize);
        _touchLayer.Measure(availableSize);
        return _textBlock.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _textBlock.Arrange(new Rect(finalSize));
        _touchLayer.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void SyncAfterTextChange()
    {
        if (!_touchSelect) return;
        if (!HasSelection) EndTouchSelection();
        else UpdateTouchUi();
    }

    private void OnTouchPointerPressed(PointerPressedEventArgs e)
    {
        // Leave the pointer uncaptured and unhandled so a swipe scrolls the parent ScrollViewer.
        (_touchDown, _touchHeld, _touchExtending, _touchExtendMoved, _touchPointer, _touchPressPoint) =
            (true, false, false, false, e.Pointer, e.GetPosition(this));
    }

    private void OnTouchPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_touchExtending)
        {
            e.PreventGestureRecognition();
            e.Handled = true;
            // Ignore zero/jitter moves while the finger is still resting on the long-press point.
            if (!_touchExtendMoved)
            {
                if (Vector.Distance(_touchPressPoint, pos) <= TouchSelection.TapSlop) return;
                _touchExtendMoved = true;
            }
            var hit = HitTestPosition(pos);
            if (hit < _touchWordStart) SetSelection(hit, _touchWordEnd);
            else if (hit > _touchWordEnd) SetSelection(_touchWordStart, hit);
            else SetSelection(_touchWordStart, _touchWordEnd);
            UpdateTouchUi();
            return;
        }
        if (_touchDown && !_touchHeld && Vector.Distance(_touchPressPoint, pos) > TouchSelection.TapSlop)
            _touchDown = false;
    }

    private void OnTouchHolding(HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && _touchDown)
        {
            e.Handled = true;
            BeginTouchSelection();
        }
        else if (e.HoldingState == HoldingState.Canceled && !_touchHeld)
        {
            _touchDown = false;
        }
    }

    private void OnTouchPointerReleased(PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var held = _touchHeld;
        var tap = !_touchHeld && Vector.Distance(_touchPressPoint, pos) <= TouchSelection.TapSlop;
        _touchDown = _touchHeld = _touchExtending = _touchExtendMoved = false;
        _touchPointer = null;

        if (held)
        {
            e.Handled = true;
            e.Pointer.Capture(null);
            _touchBarShown = true;
            UpdateTouchUi();
            return;
        }
        if (_touchSelect && tap)
        {
            if (IsPointInSelection(pos))
            {
                _touchBarShown = !_touchBarShown;
                UpdateTouchUi();
            }
            else
            {
                EndTouchSelection();
            }
            e.Handled = true;
        }
    }

    private void OnTouchPointerCaptureLost()
    {
        _touchDown = _touchHeld = _touchExtending = _touchExtendMoved = false;
        _touchPointer = null;
    }

    private void BeginTouchSelection()
    {
        if (_touchHeld) return;
        var text = CurrentText();
        if (text.Length == 0) return;
        var layout = _textBlock.TextLayout;
        var pad = _textBlock.Padding;
        // Long-pressing blank space well below all output should not select a distant line.
        if (_touchPressPoint.Y > pad.Top + layout.Height + CurrentLineHeight())
        {
            _touchDown = false;
            return;
        }

        var hit = HitTestPosition(_touchPressPoint);
        var (start, end) = WordSpanAt(text, hit);
        if (start >= end)
        {
            _touchDown = false;
            return;
        }

        _touchHeld = _touchExtending = true;
        _touchWordStart = start;
        _touchWordEnd = end;
        _touchSelect = true;
        _touchBarShown = false;
        SetSelection(start, end);
        _touchPointer?.Capture(_textBlock);
        UpdateTouchUi();
    }

    private void EndTouchSelection()
    {
        if (!_touchSelect) return;
        _touchSelect = false;
        _touchBarShown = false;
        _textBlock.ClearSelection();
        UpdateTouchUi();
    }

    private void SetSelection(int start, int end)
    {
        _textBlock.SelectionStart = start;
        _textBlock.SelectionEnd = end;
    }

    private void UpdateTouchUi()
    {
        var show = _touchSelect && HasSelection;
        _touchLayer.Start.IsVisible = _touchLayer.End.IsVisible = show;
        _touchLayer.Bar.IsVisible = show && _touchBarShown;
        if (!show) return;
        _touchLayer.Start.InvalidateVisual();
        _touchLayer.End.InvalidateVisual();
        _touchLayer.InvalidateArrange();
    }

    private Point TextOrigin() => new(_textBlock.Padding.Left, _textBlock.Padding.Top);

    private double CurrentLineHeight()
    {
        var lines = _textBlock.TextLayout.TextLines;
        return lines.Count > 0 && lines[0].Height > 0 ? lines[0].Height : Math.Max(14, _textBlock.FontSize * 1.4);
    }

    private int HitTestPosition(Point p)
    {
        var local = p - TextOrigin();
        var pos = _textBlock.TextLayout.HitTestPoint(local).TextPosition;
        return Math.Clamp(pos, 0, CurrentTextLength());
    }

    private bool IsPointInSelection(Point p)
    {
        if (!HasSelection) return false;
        var (from, to) = OrderedSelection();
        var local = p - TextOrigin();
        return _textBlock.TextLayout.HitTestTextRange(from, to - from)
            .Any(r => r.Inflate(6).Contains(local));
    }

    private Point HandleTip(bool start)
    {
        var (from, to) = OrderedSelection();
        var origin = TextOrigin();
        var layout = _textBlock.TextLayout;
        if (start) return layout.HitTestTextPosition(from).BottomLeft + origin;
        return layout.HitTestTextPosition(Math.Max(from, to - 1)).BottomRight + origin;
    }

    private void ArrangeTouchUi(Size size)
    {
        if (!_touchLayer.Start.IsVisible || !HasSelection) return;
        var (from, to) = OrderedSelection();
        var origin = TextOrigin();
        var layout = _textBlock.TextLayout;
        var startRect = layout.HitTestTextPosition(from);
        var endRect = layout.HitTestTextPosition(Math.Max(from, to - 1));
        var startTip = startRect.BottomLeft + origin;
        var endTip = endRect.BottomRight + origin;

        _touchLayer.Start.Arrange(new Rect(startTip - _touchLayer.Start.Tip, _touchLayer.Start.DesiredSize));
        _touchLayer.End.Arrange(new Rect(endTip - _touchLayer.End.Tip, _touchLayer.End.DesiredSize));
        if (!_touchLayer.Bar.IsVisible) return;

        var view = _viewport.Width > 0 ? _viewport.Intersect(new Rect(size)) : new Rect(size);
        var lineH = startRect.Height > 0 ? startRect.Height : CurrentLineHeight();
        var sameLine = Math.Abs(startRect.Top - endRect.Top) < 1;
        TouchSelection.ArrangeBar(_touchLayer.Bar, startTip, endTip, lineH, _touchLayer.End.DesiredSize.Height, sameLine, view);
    }

    internal TouchSelectionHandle StartHandle => _touchLayer.Start;
    internal TouchSelectionHandle EndHandle => _touchLayer.End;
    internal Border TouchBar => _touchLayer.Bar;

    internal void BeginHandleDrag(bool start, Point p)
    {
        var (from, to) = OrderedSelection();
        _handleIsStart = start;
        _handleAnchor = start ? to : from;
        _handleGrab = p - HandleTip(start);
        _touchBarShown = false;
        UpdateTouchUi();
    }

    internal void DragHandle(Point p)
    {
        if (!HasSelection) return;
        var tip = p - _handleGrab;
        var inside = new Point(tip.X, tip.Y - CurrentLineHeight() / 2);
        var hit = HitTestPosition(inside);
        if (_handleIsStart) SetSelection(Math.Clamp(hit, 0, Math.Max(0, _handleAnchor - 1)), _handleAnchor);
        else SetSelection(_handleAnchor, Math.Clamp(hit, Math.Min(CurrentTextLength(), _handleAnchor + 1), CurrentTextLength()));
        UpdateTouchUi();
    }

    internal void EndHandleDrag()
    {
        if (!_touchSelect || _touchBarShown) return;
        _touchBarShown = true;
        UpdateTouchUi();
    }

    /// <summary>
    /// Token under <paramref name="index"/> for initial long-press selection; splits on whitespace and
    /// common delimiters so keys/UIDs in <c>UID=5FC1AD1E</c> select cleanly without the label.
    /// </summary>
    internal static (int Start, int End) WordSpanAt(string text, int index)
    {
        if (text.Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, text.Length);

        var probe = index < text.Length && IsWordChar(text[index]) ? index
            : index > 0 && text[index - 1] is not ('\n' or '\r') && IsWordChar(text[index - 1]) ? index - 1
            : FindWordOnLine(text, index);

        if (probe < 0) return (0, 0);

        var start = probe;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = probe + 1;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return (start, end);
    }

    private static int FindWordOnLine(string text, int index)
    {
        for (var i = index - 1; i >= 0 && text[i] is not ('\n' or '\r'); i--)
            if (IsWordChar(text[i])) return i;
        for (var i = index; i < text.Length && text[i] is not ('\n' or '\r'); i++)
            if (IsWordChar(text[i])) return i;
        return -1;
    }

    private static bool IsWordChar(char c) =>
        !char.IsWhiteSpace(c) && c is not ('=' or ',' or ';' or ':' or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '"' or '\'' or '`');

    private sealed class InnerSelectableTextBlock : SelectableTextBlock
    {
        private readonly TouchSelectableText _owner;

        public InnerSelectableTextBlock(TouchSelectableText owner)
        {
            _owner = owner;
            Background = Brushes.Transparent;
            SetValue(IsHoldingEnabledProperty, true);
            AddHandler(ContextRequestedEvent, (_, e) =>
            {
                if (!_owner._touchDown) return;
                e.Handled = true;
                _owner.BeginTouchSelection();
            });
        }

        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch)
            {
                _owner.OnTouchPointerPressed(e);
                return;
            }
            _owner.EndTouchSelection();
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch)
            {
                _owner.OnTouchPointerMoved(e);
                return;
            }
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch)
            {
                _owner.OnTouchPointerReleased(e);
                return;
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _owner.OnTouchPointerCaptureLost();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnHolding(HoldingRoutedEventArgs e)
        {
            if (e.PointerType == PointerType.Touch)
            {
                _owner.OnTouchHolding(e);
                if (e.Handled) return;
            }
            base.OnHolding(e);
        }
    }

    private sealed class TouchSelectionLayer : Panel
    {
        private readonly TouchSelectableText _owner;

        public TouchSelectionLayer(TouchSelectableText owner)
        {
            _owner = owner;
            Start = new TouchSelectionHandle(owner, start: true, () => owner.HandleBrush,
                owner.BeginHandleDrag, owner.DragHandle, owner.EndHandleDrag) { IsVisible = false };
            End = new TouchSelectionHandle(owner, start: false, () => owner.HandleBrush,
                owner.BeginHandleDrag, owner.DragHandle, owner.EndHandleDrag) { IsVisible = false };

            Bar = TouchSelection.CreateBar(
                TouchSelection.CreateCommand(Strings.Hex_Copy, () =>
                {
                    owner.Copy();
                    owner.EndTouchSelection();
                }),
                TouchSelection.CreateCommand(Strings.Hex_SelectAll, () =>
                {
                    owner.SelectAll();
                    owner._touchBarShown = true;
                    owner.UpdateTouchUi();
                }));

            Children.Add(Start);
            Children.Add(End);
            Children.Add(Bar);
        }

        public TouchSelectionHandle Start { get; }
        public TouchSelectionHandle End { get; }
        public Border Bar { get; }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children) child.Measure(Size.Infinity);
            return default;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _owner.ArrangeTouchUi(finalSize);
            return finalSize;
        }
    }
}
