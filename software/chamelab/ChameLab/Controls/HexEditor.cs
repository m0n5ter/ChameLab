using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using ChameLab.Models;

namespace ChameLab.Controls;

public sealed record HexSelectionStatus(string Title, string? Detail, string Position,
    string? Hex, string? Decimal, string? Binary, string? Note);

/// <summary>
/// Grid hex editor for card memory (<see cref="HexBuffer"/>): row labels, hex
/// bytes and an ASCII column. Typing overwrites nibble by nibble, so the
/// layout never shifts and a row can't end up the wrong length. Supports
/// keyboard / mouse selection, clipboard, undo and the on-screen keyboard.
/// Rows wrap to 8 or 4 bytes per line when the control is narrow.
/// </summary>
public sealed class HexEditor : Control
{
    public static readonly StyledProperty<HexBuffer?> BufferProperty =
        AvaloniaProperty.Register<HexEditor, HexBuffer?>(nameof(Buffer));
    public static readonly StyledProperty<bool> ShowGroupHeadersProperty =
        AvaloniaProperty.Register<HexEditor, bool>(nameof(ShowGroupHeaders), true);
    public static readonly DirectProperty<HexEditor, HexSelectionStatus?> SelectionStatusProperty =
        AvaloniaProperty.RegisterDirect<HexEditor, HexSelectionStatus?>(nameof(SelectionStatus), o => o.SelectionStatus);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<HexEditor>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<HexEditor>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<HexEditor>();

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(LabelBrush));
    public static readonly StyledProperty<IBrush?> DimBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(DimBrush));
    public static readonly StyledProperty<IBrush?> ChangedBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(ChangedBrush));
    public static readonly StyledProperty<IBrush?> ManufacturerBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(ManufacturerBrush));
    public static readonly StyledProperty<IBrush?> KeyABrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(KeyABrush));
    public static readonly StyledProperty<IBrush?> AccessBitsBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(AccessBitsBrush));
    public static readonly StyledProperty<IBrush?> KeyBBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(KeyBBrush));
    public static readonly StyledProperty<IBrush?> CaretBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(CaretBrush));
    public static readonly StyledProperty<IBrush?> CaretCellBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(CaretCellBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> ActiveRowBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(ActiveRowBrush));
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(SeparatorBrush));

    public static readonly DirectProperty<HexEditor, string?> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<HexEditor, string?>(nameof(StatusText), o => o.StatusText);

    public static readonly DirectProperty<HexEditor, string?> StatusDetailProperty =
        AvaloniaProperty.RegisterDirect<HexEditor, string?>(nameof(StatusDetail), o => o.StatusDetail);

    private enum Pane { Hex, Ascii }

    private const double SeparatorGap = 8;
    private const int PageLines = 8;
    private static readonly TimeSpan CaretBlink = TimeSpan.FromMilliseconds(530);

    private readonly Dictionary<(string, IBrush), FormattedText> _textCache = new();
    private readonly Dictionary<int, List<(IBrush Brush, GlyphRun Run)>> _lineText = new();
    private static readonly string[] HexBytes = Enumerable.Range(0, 256).Select(b => b.ToString("X2")).ToArray();
    private static readonly string[] AsciiBytes = Enumerable.Range(0, 256)
        .Select(b => b is >= 0x20 and < 0x7F ? ((char)b).ToString() : "·").ToArray();

    private void ClearLineText()
    {
        foreach (var runs in _lineText.Values)
            foreach (var (_, run) in runs) run.Dispose();
        _lineText.Clear();
    }

    // Layout (recomputed on measure).
    private Typeface _typeface;
    private bool _canBatchText;
    private double _digitW, _lineH, _labelW, _hexX, _asciiX, _byteStride, _byteGap, _groupGap;
    private int _perLine = 16;
    private bool _showAscii;
    private double[] _lineY = Array.Empty<double>();
    private double _contentHeight;

    // Caret / selection, as byte offsets into the buffer.
    private long _caret;
    private bool _lowNibble;
    private long _anchor = -1;
    private Pane _pane = Pane.Hex;
    private bool _dragging;
    private bool _pointerFocus, _movingCaret;
    private HexBuffer? _buffer;
    private readonly DispatcherTimer _blink;
    private bool _caretOn = true;
    // The part of the control on screen (empty until known): only lines around it are drawn.
    private Rect _viewport;

    static HexEditor()
    {
        FocusableProperty.OverrideDefaultValue<HexEditor>(true);
        AffectsMeasure<HexEditor>(BufferProperty, FontFamilyProperty, FontSizeProperty, ShowGroupHeadersProperty);
        AffectsRender<HexEditor>(ShowGroupHeadersProperty);
        AffectsRender<HexEditor>(ForegroundProperty, LabelBrushProperty, DimBrushProperty, ChangedBrushProperty,
            ManufacturerBrushProperty, KeyABrushProperty, AccessBitsBrushProperty, KeyBBrushProperty, CaretBrushProperty,
            CaretCellBrushProperty, SelectionBrushProperty, ActiveRowBrushProperty, SeparatorBrushProperty);
        TextInputMethodClientRequestedEvent.AddClassHandler<HexEditor>((editor, e) =>
        {
            if (editor.Buffer is { IsReadOnly: false }) e.Client = editor._ime;
        });
    }

    private readonly InputClient _ime;

    public HexEditor()
    {
        _ime = new InputClient(this);
        _blink = new DispatcherTimer { Interval = CaretBlink };
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; InvalidateVisual(); };
        Cursor = new Cursor(StandardCursorType.Ibeam);
        // The caret and the active row show focus; no keyboard focus frame around the whole grid.
        FocusAdorner = null;
        // The control can be thousands of pixels tall: when focus asks the page to
        // show it, show the caret line instead of jumping to the control's top.
        // A click focuses the editor before OnPointerPressed moves the caret, so that
        // request would scroll to the old caret; the clicked byte is already in view.
        // (GotFocus reaches this control before the scroll viewers that ask.)
        AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            if (e.TargetObject != this || _lineY.Length == 0 || _buffer is not { ViewLength: > 0 }) return;
            if (_pointerFocus && !_movingCaret) { e.Handled = true; return; }
            e.TargetRect = CaretViewRect();
        });
        // Scrolling moves the drawn lines without redrawing them; draw again once
        // the view leaves the screenful drawn around it.
        EffectiveViewportChanged += (_, e) =>
        {
            var was = _viewport;
            _viewport = e.EffectiveViewport;
            if (was.Height <= 0 || Math.Abs(_viewport.Top - was.Top) > was.Height / 2 || Math.Abs(_viewport.Height - was.Height) > 1e-7)
                InvalidateVisual();
            else _viewport = was;
        };
        TextInputOptions.SetShowSuggestions(this, false);
        TextInputOptions.SetAutoCapitalization(this, false);
        TextInputOptions.SetMultiline(this, false);
        ContextMenu = BuildContextMenu();
    }

    public HexBuffer? Buffer { get => GetValue(BufferProperty); set => SetValue(BufferProperty, value); }
    public bool ShowGroupHeaders { get => GetValue(ShowGroupHeadersProperty); set => SetValue(ShowGroupHeadersProperty, value); }

    public HexSelectionStatus? SelectionStatus
    {
        get;
        private set => SetAndRaise(SelectionStatusProperty, ref field, value);
    }

    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    /// <summary>Height of one line of bytes at the current font size.</summary>
    public double LineHeight => Math.Ceiling(FontSize * 1.45);
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? DimBrush { get => GetValue(DimBrushProperty); set => SetValue(DimBrushProperty, value); }
    public IBrush? ChangedBrush { get => GetValue(ChangedBrushProperty); set => SetValue(ChangedBrushProperty, value); }
    public IBrush? ManufacturerBrush { get => GetValue(ManufacturerBrushProperty); set => SetValue(ManufacturerBrushProperty, value); }
    public IBrush? KeyABrush { get => GetValue(KeyABrushProperty); set => SetValue(KeyABrushProperty, value); }
    public IBrush? AccessBitsBrush { get => GetValue(AccessBitsBrushProperty); set => SetValue(AccessBitsBrushProperty, value); }
    public IBrush? KeyBBrush { get => GetValue(KeyBBrushProperty); set => SetValue(KeyBBrushProperty, value); }
    public IBrush? CaretBrush { get => GetValue(CaretBrushProperty); set => SetValue(CaretBrushProperty, value); }
    public IBrush? CaretCellBrush { get => GetValue(CaretCellBrushProperty); set => SetValue(CaretCellBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? ActiveRowBrush { get => GetValue(ActiveRowBrushProperty); set => SetValue(ActiveRowBrushProperty, value); }
    public IBrush? SeparatorBrush { get => GetValue(SeparatorBrushProperty); set => SetValue(SeparatorBrushProperty, value); }

    /// <summary>
    /// Caret position and the byte under it, padded to a constant width for a
    /// monospace status line: "row:col  0xOFFS  HX  dec  binary".
    /// </summary>
    public string? StatusText
    {
        get;
        private set => SetAndRaise(StatusTextProperty, ref field, value);
    }

    /// <summary>What the byte under the caret means (e.g. "Sector 0 · UID"), if known.</summary>
    public string? StatusDetail
    {
        get;
        private set => SetAndRaise(StatusDetailProperty, ref field, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BufferProperty)
        {
            ClearLineText();
            if (_buffer is not null) _buffer.Changed -= OnBufferChanged;
            _buffer = Buffer;
            if (_buffer is not null) _buffer.Changed += OnBufferChanged;
            _caret = 0;
            _lowNibble = false;
            _anchor = -1;
            UpdateStatus();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty ||
                 change.Property.PropertyType == typeof(IBrush))
        {
            _textCache.Clear();
            ClearLineText();
        }
    }

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        ClearLineText();
        if (_buffer is { } buf && _lineY.Length != buf.ViewRowCount * LinesPerRow)
        {
            // Rows were added or removed: lay the lines out again and keep the caret inside.
            if (_caret >= buf.ViewLength) { _caret = Math.Max(0, buf.ViewLength - 1); _lowNibble = false; }
            if (_anchor >= buf.ViewLength) _anchor = -1;
            InvalidateMeasure();
        }
        UpdateStatus();
        InvalidateVisual();
    }

    /// <summary>Puts the caret on a byte (and shows it), e.g. at a row just added.</summary>
    public void GoTo(long offset)
    {
        if (_buffer is not { ViewLength: > 0 }) return;
        // Lines of a row just added are laid out first, so the caret can be shown.
        UpdateLayout();
        _pane = Pane.Hex;
        MoveCaret(offset, false, extend: false);
    }

    // ================================================================ Layout

    private int LinesPerRow => _buffer is null ? 1 : Math.Max(1, _buffer.RowLength / _perLine);

    /// <summary>Bytes between group gaps: fours where the line splits into fours, else none.</summary>
    private static int GroupSize(int perLine) => perLine % 4 == 0 ? 4 : perLine;

    private double HexWidth(int perLine) =>
        perLine * 2 * _digitW + (perLine - 1) * _byteGap + (perLine - 1) / GroupSize(perLine) * _groupGap;

    protected override Size MeasureOverride(Size availableSize)
    {
        ClearLineText();
        _textCache.Clear();
        _typeface = new Typeface(FontFamily);
        var glyphMap = _typeface.GlyphTypeface.CharacterToGlyphMap;
        _canBatchText = Enumerable.Range(0x20, 0x7F - 0x20).Append(0xB7)
            .All(glyphMap.ContainsGlyph);
        _digitW = "0123456789ABCDEF".Max(c => Text(c.ToString(), Brushes.Black).WidthIncludingTrailingWhitespace);
        _canBatchText &= "0123456789ABCDEF".All(c =>
            Math.Abs(Text(c.ToString(), Brushes.Black).WidthIncludingTrailingWhitespace - _digitW) < 0.001);
        _lineH = LineHeight;
        _byteGap = _digitW * 0.6;
        _groupGap = _digitW * 0.6;
        _byteStride = 2 * _digitW + _byteGap;
        _textCache.Clear();

        if (_buffer is null) return default;

        var labelChars = Enumerable.Range(0, _buffer.ViewRowCount).Select(r => _buffer.RowLabel(r).Length).DefaultIfEmpty(2).Max();
        _labelW = (labelChars + 1.5) * _digitW;
        _hexX = _labelW;

        // Widest layout that fits with the ASCII column (wrapping rows to 8 / 4 bytes
        // if needed); only when even that doesn't fit, drop ASCII.
        var avail = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        // The whole row on one line (up to 16 bytes, e.g. a 6-byte key), else halves, quarters…
        var candidates = new[] { _buffer.RowLength, 16, 8, 4, 2, 1 }
            .Where(n => n <= 16 && n <= _buffer.RowLength && _buffer.RowLength % n == 0)
            .Distinct().OrderDescending().ToArray();
        (int perLine, bool ascii) pick = (candidates[^1], false);
        var asciiMin = Math.Min(4, _buffer.RowLength);
        if (candidates.FirstOrDefault(n => n >= asciiMin && _hexX + HexWidth(n) + 2 * _digitW + n * _digitW <= avail) is > 0 and var withAscii)
            pick = (withAscii, true);
        else if (candidates.FirstOrDefault(n => _hexX + HexWidth(n) <= avail) is > 0 and var hexOnly)
            pick = (hexOnly, false);
        (_perLine, _showAscii) = pick;
        _asciiX = _hexX + HexWidth(_perLine) + 2 * _digitW;
        if (!_showAscii && _pane == Pane.Ascii) _pane = Pane.Hex;

        var lines = _buffer.ViewRowCount * LinesPerRow;
        _lineY = new double[lines];
        double y = 0;
        for (var i = 0; i < lines; i++)
        {
            var row = i / LinesPerRow;
            if (i % LinesPerRow == 0)
            {
                if (row > 0 && _buffer.StartsGroup(row)) y += SeparatorGap;
                // A group heading takes a line of its own above the group's first row.
                if (ShowGroupHeaders && _buffer.GroupHeader(row) is not null) y += _lineH;
            }
            _lineY[i] = y;
            y += _lineH;
        }
        _contentHeight = y;

        var width = _showAscii ? _asciiX + _perLine * _digitW : _hexX + HexWidth(_perLine);
        return new Size(Math.Min(width, avail), _contentHeight);
    }

    private double ByteX(int col) => _hexX + col * _byteStride + col / GroupSize(_perLine) * _groupGap;
    private double AsciiX(int col) => _asciiX + col * _digitW;

    private int LineOf(long offset) => (int)(offset / _perLine);

    private Rect CellRect(long offset, Pane pane)
    {
        int line = LineOf(offset), col = (int)(offset % _perLine);
        var y = _lineY[line];
        return pane == Pane.Hex
            ? new Rect(ByteX(col) - _byteGap / 2, y, 2 * _digitW + _byteGap, _lineH)
            : new Rect(AsciiX(col), y, _digitW, _lineH);
    }

    /// <summary>The line at a height (clamped to the lines there are); a group heading belongs to the line under it.</summary>
    private int LineAt(double y)
    {
        var line = Array.BinarySearch(_lineY, y);
        if (line < 0) line = Math.Max(0, ~line - 1);
        line = Math.Min(line, _lineY.Length - 1);
        return y >= _lineY[line] + _lineH && line + 1 < _lineY.Length ? line + 1 : line;
    }

    /// <summary>Byte (and nibble / pane) under a point, clamped to the buffer.</summary>
    private (long offset, bool low, Pane pane) HitTest(Point p)
    {
        var line = LineAt(p.Y);

        if (_showAscii && p.X >= _asciiX - _digitW)
        {
            var col = Math.Clamp((int)((p.X - _asciiX) / _digitW), 0, _perLine - 1);
            return ((long)line * _perLine + col, false, Pane.Ascii);
        }

        var best = 0;
        for (var c = 0; c < _perLine; c++)
            if (p.X >= ByteX(c) - _byteGap / 2) best = c;
        var low = p.X >= ByteX(best) + _digitW;
        return ((long)line * _perLine + best, low, Pane.Hex);
    }

    // ================================================================ Rendering

    private FormattedText Text(string s, IBrush brush)
    {
        if (!_textCache.TryGetValue((s, brush), out var ft))
        {
            ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, brush);
            _textCache[(s, brush)] = ft;
        }
        return ft;
    }

    private void DrawCentered(DrawingContext ctx, string s, IBrush brush, double centerX, double y)
    {
        var ft = Text(s, brush);
        ctx.DrawText(ft, new Point(centerX - ft.WidthIncludingTrailingWhitespace / 2, y + (_lineH - ft.Height) / 2));
    }

    private IBrush KindBrush(HexCellKind kind, IBrush fg) => kind switch
    {
        HexCellKind.Manufacturer => ManufacturerBrush ?? fg,
        HexCellKind.KeyA => KeyABrush ?? fg,
        HexCellKind.AccessBits => AccessBitsBrush ?? fg,
        HexCellKind.KeyB => KeyBBrush ?? fg,
        _ => fg,
    };

    // Batch a line by brush instead of recording a separate text draw for every byte.
    // Explicit glyph advances preserve the cell positions, including fractional group gaps.
    // Keep only the drawn window cached, so scrolling a large capture has bounded memory use.
    private List<(IBrush Brush, GlyphRun Run)> LineText(int line, HexBuffer buf,
        IBrush fg, IBrush dim, IBrush changed)
    {
        if (_lineText.TryGetValue(line, out var cached)) return cached;
        var glyphs = new Dictionary<IBrush, List<(ushort Glyph, double X)>>();
        var face = _typeface.GlyphTypeface;
        var sample = Text("00", fg);
        var baseline = (_lineH - sample.Height) / 2 + sample.Baseline;
        void Add(string text, IBrush brush, double center)
        {
            var width = Text(text, brush).WidthIncludingTrailingWhitespace;
            var x = center - width / 2;
            // Coverage and fixed-width hex digits were checked during measure.
            foreach (var c in text)
            {
                var glyph = face.CharacterToGlyphMap[c];
                if (!glyphs.TryGetValue(brush, out var list)) glyphs[brush] = list = new();
                list.Add((glyph, x));
                x += width / text.Length;
            }
        }

        var row = line / LinesPerRow;
        var read = buf.IsRead(row);
        for (var col = 0; col < _perLine; col++)
        {
            var offset = (long)line * _perLine + col;
            // An unread row shows its template (blank-card defaults) dimmed, or dashes without one.
            var template = read ? null : buf.TemplateAt(offset);
            var b = read ? buf[offset] : template ?? 0;
            var known = !read && buf.IsTemplateKnown(offset);
            var brush = !read && !known ? dim
                : buf.IsChanged(offset) ? changed : KindBrush(buf.Classify(row, (int)(offset % buf.RowLength)), fg);
            Add(read || template is not null ? HexBytes[b] : "--", brush, ByteX(col) + _digitW);
            if (_showAscii)
                Add(read || template is not null ? AsciiBytes[b] : "·", read && AsciiBytes[b] != "·" ? brush : dim, AsciiX(col) + _digitW / 2);
        }

        var runs = new List<(IBrush, GlyphRun)>();
        foreach (var (brush, list) in glyphs)
        {
            list.Sort((a, b) => a.X.CompareTo(b.X));
            var info = new GlyphInfo[list.Count];
            for (var i = 0; i < info.Length; i++)
                info[i] = new GlyphInfo(list[i].Glyph, i,
                    i + 1 < info.Length ? list[i + 1].X - list[i].X : _digitW);
            runs.Add((brush, new GlyphRun(face, FontSize, ReadOnlyMemory<char>.Empty, info,
                new Point(list[0].X, baseline))));
        }
        return _lineText[line] = runs;
    }

    public override void Render(DrawingContext ctx)
    {
        var buf = _buffer;
        if (buf is null || _lineY.Length == 0) return;

        // Paint the whole area so clicks between and around bytes still reach the editor.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var fg = Foreground ?? Brushes.White;
        var dim = DimBrush ?? fg;
        var label = LabelBrush ?? dim;
        var changed = ChangedBrush ?? fg;
        var focused = IsKeyboardFocusWithin;
        var (selFrom, selTo) = Selection();

        // Only the lines in view (plus a screenful around them) are drawn: a key list can be thousands long.
        var (drawFrom, drawTo) = (0, _lineY.Length - 1);
        if (_viewport is { Height: > 0 } view)
        {
            drawFrom = Math.Max(0, LineAt(view.Top - view.Height));
            drawTo = Math.Min(_lineY.Length - 1, LineAt(view.Bottom + view.Height));
        }

        foreach (var line in _lineText.Keys.Where(line => line < drawFrom || line > drawTo).ToArray())
        {
            foreach (var (_, run) in _lineText[line]) run.Dispose();
            _lineText.Remove(line);
        }
        // Gutter labels are unique; do not retain every offset visited while scrolling.
        if (_textCache.Count > 4096) _textCache.Clear();

        var caretLine = LineOf(_caret);
        for (var line = drawFrom; line <= drawTo; line++)
        {
            var y = _lineY[line];
            var row = line / LinesPerRow;
            var firstOfRow = line % LinesPerRow == 0;
            var rowRead = buf.IsRead(row);

            var header = firstOfRow && ShowGroupHeaders ? buf.GroupHeader(row) : null;
            var top = header is null ? y : y - _lineH;
            if (firstOfRow && row > 0 && buf.StartsGroup(row) && SeparatorBrush is { } sep)
                ctx.FillRectangle(sep, new Rect(0, top - SeparatorGap / 2 - 0.5, Bounds.Width, 1));

            if (header is { } h)
            {
                var title = Text(h.Title, label);
                ctx.DrawText(title, new Point(0, top + (_lineH - title.Height) / 2));
                if (h.Detail is { } detail)
                {
                    var ft = Text(detail, dim);
                    ctx.DrawText(ft, new Point(title.WidthIncludingTrailingWhitespace + 2 * _digitW, top + (_lineH - ft.Height) / 2));
                }
            }

            if (line == caretLine && ActiveRowBrush is { } activeRow)
                ctx.FillRectangle(activeRow, new Rect(0, y, Bounds.Width, _lineH), 4);

            if (firstOfRow)
            {
                var ft = Text(buf.RowLabel(row), rowRead ? label : dim);
                ctx.DrawText(ft, new Point(0, y + (_lineH - ft.Height) / 2));
            }

            for (var col = 0; col < _perLine; col++)
            {
                var offset = (long)line * _perLine + col;
                var selected = offset >= selFrom && offset <= selTo;
                var isCaret = offset == _caret;

                // Most cells have no background. Avoid per-byte pane arrays and rectangle work.
                if (selected || isCaret)
                {
                    for (var p = 0; p < (_showAscii ? 2 : 1); p++)
                    {
                        var cell = CellRect(offset, p == 0 ? Pane.Hex : Pane.Ascii);
                        if (selected && SelectionBrush is { } selBrush)
                            ctx.FillRectangle(selBrush, cell);
                        else if (isCaret && CaretCellBrush is { } caretCell)
                            ctx.FillRectangle(caretCell, cell.Deflate(new Thickness(0, 2)), 3);
                    }
                }
            }

            if (_canBatchText)
            {
                using (ctx.PushTransform(Matrix.CreateTranslation(0, y)))
                    foreach (var (brush, run) in LineText(line, buf, fg, dim, changed))
                        ctx.DrawGlyphRun(brush, run);
            }

            for (var col = 0; col < _perLine; col++)
            {
                var offset = (long)line * _perLine + col;
                var rowCol = (int)(offset % buf.RowLength);
                var isCaret = offset == _caret;

                if (!_canBatchText)
                {
                    IBrush brush;
                    string hex, ascii;
                    if (!rowRead)
                    {
                        brush = buf.IsTemplateKnown(offset) ? KindBrush(buf.Classify(row, rowCol), fg) : dim;
                        var template = buf.TemplateAt(offset);
                        hex = template is { } t ? HexBytes[t] : "--";
                        ascii = template is { } a ? AsciiBytes[a] : "·";
                    }
                    else
                    {
                        var b = buf[offset];
                        brush = buf.IsChanged(offset) ? changed : KindBrush(buf.Classify(row, rowCol), fg);
                        hex = HexBytes[b];
                        ascii = AsciiBytes[b];
                    }

                    DrawCentered(ctx, hex, brush, ByteX(col) + _digitW, y);
                    if (_showAscii)
                        DrawCentered(ctx, ascii, rowRead && ascii != "·" ? brush : dim, AsciiX(col) + _digitW / 2, y);
                }

                // Caret: a blinking text-cursor bar before the active nibble (hex) or character (ASCII).
                if (isCaret && focused && _caretOn && CaretBrush is { } caret)
                {
                    var cx = _pane == Pane.Hex ? ByteX(col) + (_lowNibble ? _digitW : 0) : AsciiX(col);
                    ctx.FillRectangle(caret, new Rect(cx - 1, y + 2, 2, _lineH - 4));
                }
            }
        }
    }

    // ================================================================ Caret & selection

    private (long from, long to) Selection() =>
        _anchor < 0 ? (-1, -2) : (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));

    private bool HasSelection => _anchor >= 0 && _anchor != _caret;

    private void MoveCaret(long offset, bool low, bool extend)
    {
        if (_buffer is not { ViewLength: > 0 }) return;
        if (extend) { if (_anchor < 0) _anchor = _caret; }
        else _anchor = -1;
        _caret = Math.Clamp(offset, 0, _buffer.ViewLength - 1);
        _lowNibble = low && !extend;
        RestartBlink();
        UpdateStatus();
        InvalidateVisual();
        if (_lineY.Length == 0) return;
        _movingCaret = true;
        try { this.BringIntoView(CaretViewRect()); }
        finally { _movingCaret = false; }
    }

    private Rect CaretViewRect() =>
        CellRect(Math.Min(_caret, _buffer!.ViewLength - 1), _pane).Inflate(new Thickness(0, _lineH / 2));

    private void UpdateStatus()
    {
        var buf = _buffer;
        if (buf is not { ViewLength: > 0 }) { StatusText = StatusDetail = null; SelectionStatus = null; return; }

        // Every field is padded so the line keeps its width as the caret moves.
        const int valueWidth = 16; // "E1  225 11100001"
        int row = (int)(_caret / buf.RowLength), col = (int)(_caret % buf.RowLength);
        var labelWidth = buf.RowLabel(buf.ViewRowCount - 1).Length;
        var colWidth = (buf.RowLength - 1).ToString().Length;
        var offsetWidth = Math.Max(3, (buf.ByteOffset + buf.ViewLength - 1).ToString("X").Length);

        string value;
        if (HasSelection)
        {
            var (from, to) = Selection();
            value = $"{to - from + 1} selected";
        }
        else if (buf.IsRead(row))
        {
            var b = buf[_caret];
            value = $"{b:X2} {b,4} {Convert.ToString(b, 2).PadLeft(8, '0')}";
        }
        else if (buf.IsTemplateKnown(_caret) && buf.TemplateAt(_caret) is { } k)
            value = $"{k:X2} {k,4} {Convert.ToString(k, 2).PadLeft(8, '0')}";
        else value = "not read";

        StatusText = $"{buf.RowLabel(row).PadLeft(labelWidth)}:{col.ToString().PadLeft(colWidth, '0')}  " +
                     $"0x{(buf.ByteOffset + _caret).ToString("X" + offsetWidth)}  {value.PadRight(valueWidth, ' ')}";
        StatusDetail = buf.Describe(row, col) is { Length: > 0 } what ? what : null;
        var title = "Memory";
        for (var r = row; r >= 0; r--)
            if (buf.GroupHeader(r) is { } group) { title = group.Title; break; }
        var detail = StatusDetail;
        if (detail == title) detail = null;
        else if (detail?.StartsWith(title + " · ", StringComparison.Ordinal) == true)
            detail = detail[(title.Length + 3)..];
        else if (detail?.StartsWith(title + " trailer · ", StringComparison.Ordinal) == true)
            detail = detail[(title.Length + 11)..];
        var position = $"{buf.RowLabel(row)}:{col.ToString().PadLeft(colWidth, '0')}";
        // Known without the block being read (the UID from the scan): the value, and where it comes from.
        if (!HasSelection && buf.IsTemplateKnown(_caret) && buf.TemplateAt(_caret) is { } known)
        {
            const string note = "from the card's identity, block not read";
            SelectionStatus = new(title, detail is null ? note : $"{detail} · {note}", position, known.ToString("X2"),
                known.ToString(CultureInfo.InvariantCulture), Convert.ToString(known, 2).PadLeft(8, '0'), null);
            return;
        }
        SelectionStatus = !HasSelection && buf.IsRead(row)
            ? new(title, detail, position, buf[_caret].ToString("X2"), buf[_caret].ToString(CultureInfo.InvariantCulture),
                Convert.ToString(buf[_caret], 2).PadLeft(8, '0'), null)
            : new(title, detail, position, null, null, null,
                HasSelection ? $"{Selection().to - Selection().from + 1} selected" : "not read");
    }

    // ================================================================ Input

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _pointerFocus = e.NavigationMethod == NavigationMethod.Pointer;
        RestartBlink();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _pointerFocus = false;
        _blink.Stop();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
        ClearLineText();
    }

    /// <summary>Shows the caret solid after a move and blinks it while focused.</summary>
    private void RestartBlink()
    {
        _caretOn = true;
        _blink.Stop();
        if (IsKeyboardFocusWithin) _blink.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_buffer is null || _lineY.Length == 0) return;
        var point = e.GetCurrentPoint(this);

        // Touch: place the caret on release so the page can still be scrolled by dragging.
        if (e.Pointer.Type == PointerType.Touch) return;
        if (!point.Properties.IsLeftButtonPressed)
        {
            // Right click inside the selection keeps it for the context menu.
            var (from, to) = Selection();
            var hit = HitTest(point.Position);
            if (hit.offset < from || hit.offset > to) PlaceCaret(hit, extend: false);
            Focus(NavigationMethod.Pointer);
            return;
        }

        PlaceCaret(HitTest(point.Position), extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        Focus(NavigationMethod.Pointer);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pointerFocus = _dragging = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _buffer is null) return;
        var hit = HitTest(e.GetPosition(this));
        if (hit.offset != _caret || _anchor < 0) MoveCaret(hit.offset, false, extend: true);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pointerFocus = false;
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            if (_anchor == _caret) _anchor = -1;
            UpdateStatus();
            InvalidateVisual();
        }
        else if (e.Pointer.Type == PointerType.Touch && _buffer is not null && _lineY.Length > 0 &&
                 e.InitialPressMouseButton == MouseButton.Left)
        {
            PlaceCaret(HitTest(e.GetPosition(this)), extend: false);
            Focus(NavigationMethod.Pointer);
        }
    }

    private void PlaceCaret((long offset, bool low, Pane pane) hit, bool extend)
    {
        _pane = hit.pane;
        MoveCaret(hit.offset, hit.low, extend);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_buffer is not { ViewLength: > 0, IsReadOnly: false } || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;

        if (HasSelection) MoveCaret(Selection().from, false, extend: false);
        foreach (var ch in e.Text)
        {
            if (_pane == Pane.Hex)
            {
                var v = HexValue(ch);
                if (v < 0) continue;
                _buffer.WriteNibble(_caret, !_lowNibble, v);
                if (!_lowNibble) MoveCaret(_caret, true, false);
                else if (_caret < _buffer.ViewLength - 1) MoveCaret(_caret + 1, false, false);
            }
            else if (ch is >= ' ' and < (char)0x7F)
            {
                _buffer.Write(_caret, [(byte)ch]);
                MoveCaret(_caret + 1, false, false);
            }
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_buffer is not { ViewLength: > 0 } || e.Handled) return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        long line = _perLine;
        var last = _buffer.ViewLength - 1;
        e.Handled = true;

        switch (e.Key)
        {
            case Key.Left when _pane == Pane.Hex && _lowNibble && !shift:
                MoveCaret(_caret, false, false);
                break;
            case Key.Left: MoveCaret(_caret - 1, false, shift); break;
            case Key.Right: MoveCaret(_caret + 1, false, shift); break;
            case Key.Up: MoveCaret(_caret >= line ? _caret - line : _caret, _lowNibble, shift); break;
            case Key.Down: MoveCaret(_caret + line <= last ? _caret + line : _caret, _lowNibble, shift); break;
            case Key.PageUp: MoveCaret(Math.Max(_caret % line, _caret - line * PageLines), false, shift); break;
            case Key.PageDown:
                var down = _caret + line * PageLines;
                while (down > last) down -= line;
                MoveCaret(down, false, shift);
                break;
            case Key.Home when cmd: MoveCaret(0, false, shift); break;
            case Key.End when cmd: MoveCaret(last, false, shift); break;
            case Key.Home: MoveCaret(_caret - _caret % line, false, shift); break;
            case Key.End: MoveCaret(_caret - _caret % line + line - 1, false, shift); break;
            case Key.Back: MoveCaret(_lowNibble ? _caret : _caret - 1, false, false); break;
            case Key.Enter when _buffer.CanResize && !cmd: InsertRowBelow(); break;
            case Key.Delete when _buffer.CanResize: RemoveRows(); break;
            case Key.Tab when _showAscii && !cmd && !e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                // Switch between the hex and ASCII columns; leave the control from the side it's going to.
                if ((_pane == Pane.Hex) == shift) { e.Handled = false; return; }
                _pane = shift ? Pane.Hex : Pane.Ascii;
                MoveCaret(_caret, false, false);
                break;
            case Key.Escape when HasSelection: MoveCaret(_caret, false, false); break;
            case Key.A when cmd: _anchor = 0; MoveCaret(last, false, true); break;
            case Key.C when cmd: Copy(); break;
            case Key.X when cmd: Copy(); break; // fixed size: cut copies only
            case Key.V when cmd: Paste(); break;
            case Key.Z when cmd && shift: Redo(); break;
            case Key.Z when cmd: Undo(); break;
            case Key.Y when cmd: Redo(); break;
            default: e.Handled = false; break;
        }
    }

    // ================================================================ Commands

    private void Undo()
    {
        if (_buffer is { IsReadOnly: false } && _buffer.Undo()) InvalidateVisual();
    }

    private void Redo()
    {
        if (_buffer is { IsReadOnly: false } && _buffer.Redo()) InvalidateVisual();
    }

    /// <summary>
    /// Enter in a resizable buffer: on the last row (or the empty one after it) moves to the empty
    /// row at the end; higher up, opens an empty row under the caret's row and moves there.
    /// </summary>
    private void InsertRowBelow()
    {
        if (_buffer is not { CanResize: true } buf) return;
        var row = (int)(_caret / buf.RowLength);
        if (row < buf.RowCount - 1) buf.InsertRows(row + 1, 1);
        GoTo((long)Math.Min(row + 1, buf.ViewRowCount - 1) * buf.RowLength);
    }

    /// <summary>Removes the rows the selection touches, or the caret's row (resizable buffers).</summary>
    private void RemoveRows()
    {
        if (_buffer is not { CanResize: true, RowCount: > 0 } buf) return;
        var (from, to) = HasSelection ? Selection() : (_caret, _caret);
        int first = (int)(from / buf.RowLength), last = Math.Min((int)(to / buf.RowLength), buf.RowCount - 1);
        if (first > last) return;
        _anchor = -1;
        buf.RemoveRows(first, last - first + 1);
        GoTo((long)Math.Min(first, buf.ViewRowCount - 1) * buf.RowLength);
    }

    /// <summary>Copies the selection, or the whole row under the caret.</summary>
    private async void Copy()
    {
        try
        {
            var buf = _buffer;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (buf is not { ViewLength: > 0 } || clipboard is null) return;

            long from, to;
            if (HasSelection) (from, to) = Selection();
            else
            {
                from = _caret - _caret % buf.RowLength;
                to = from + buf.RowLength - 1;
            }

            string text;
            if (_pane == Pane.Ascii)
            {
                var sb = new StringBuilder();
                for (var o = from; o <= to; o++)
                    sb.Append(buf.IsRead(o) && buf[o] is >= 0x20 and < 0x7F ? (char)buf[o] : '.');
                text = sb.ToString();
            }
            else
            {
                text = string.Join(' ', Enumerable.Range(0, (int)(to - from + 1))
                    .Select(i => buf.IsRead(from + i) ? buf[from + i].ToString("X2") : "??"));
            }

            try { await clipboard.SetTextAsync(text); }
            catch { /* clipboard unavailable (browser without permission) */ }
        }
        catch (Exception e)
        {
            // ignore
        }
    }

    /// <summary>Overwrites from the caret (or selection start) with hex / text from the clipboard.</summary>
    private async void Paste()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (_buffer is null or { IsReadOnly: true } || clipboard is null) return;

            string? text;
            try { text = await clipboard.TryGetTextAsync(); }
            catch { return; }
            if (string.IsNullOrEmpty(text) || _buffer is not { } buf) return;

            byte[] bytes;
            if (_pane == Pane.Ascii) bytes = Encoding.ASCII.GetBytes(text.Replace("\r", "").Replace("\n", ""));
            else if (!Hex.TryParse(text, out bytes)) return;

            var start = HasSelection ? Selection().from : _caret;
            if (HasSelection) bytes = bytes.Take((int)(Selection().to - start + 1)).ToArray();
            var written = buf.Write(start, bytes);
            if (written > 0) GoTo(Math.Min(start + written, buf.ViewLength - 1));
        }
        catch (Exception e)
        {
            // ignore
        }
    }

    private void SelectAll()
    {
        if (_buffer is not { ViewLength: > 0 }) return;
        _anchor = 0;
        MoveCaret(_buffer.ViewLength - 1, false, true);
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGesture = KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }

        var undo = Item("Undo", "Ctrl+Z", Undo);
        var redo = Item("Redo", "Ctrl+Y", Redo);
        var insert = Item("Add row below", "Enter", InsertRowBelow);
        var remove = Item("Delete rows", "Delete", RemoveRows);
        var rowsSeparator = new Separator();
        var paste = Item("Paste", "Ctrl+V", Paste);
        var historySeparator = new Separator();
        var menu = new ContextMenu
        {
            Items =
            {
                Item("Copy", "Ctrl+C", Copy),
                paste,
                rowsSeparator,
                insert,
                remove,
                historySeparator,
                undo,
                redo,
                new Separator(),
                Item("Select all", "Ctrl+A", SelectAll),
            },
        };
        menu.Opening += (_, _) =>
        {
            undo.IsEnabled = _buffer?.CanUndo == true;
            redo.IsEnabled = _buffer?.CanRedo == true;
            // Rows come and go only in resizable buffers (key lists), never in card memory.
            var resizable = _buffer?.CanResize == true;
            rowsSeparator.IsVisible = insert.IsVisible = remove.IsVisible = resizable;
            // A read-only view copies and selects only.
            paste.IsVisible = historySeparator.IsVisible = undo.IsVisible = redo.IsVisible = _buffer?.IsReadOnly != true;
        };
        return menu;
    }

    /// <summary>
    /// Minimal input-method client: lets the on-screen keyboard open on phones and
    /// delivers typed characters as <see cref="TextInputEventArgs"/>.
    /// </summary>
    private sealed class InputClient(HexEditor owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle =>
            owner._lineY.Length > 0 ? owner.CellRect(owner._caret, owner._pane) : default;
        public override TextSelection Selection { get => default; set { } }
    }
}
