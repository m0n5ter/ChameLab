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
using ChameLab.Localization;

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
    private static readonly string[] HexBytes = Enumerable.Range(0, 256).Select(b => b.ToString("X2")).ToArray();
    private static readonly string[] AsciiBytes = Enumerable.Range(0, 256)
        .Select(b => b is >= 0x20 and < 0x7F ? ((char)b).ToString() : "·").ToArray();

    // The text is drawn by strips of lines (child visuals) that keep what they drew: scrolling
    // only moves them, and a strip draws again only when its text changes. Strips come and go
    // with the part of the editor on screen, so a long key list never draws all its lines.
    // The editor itself draws only the row and cell highlights under them, and the caret
    // layer above them blinks on its own without redrawing any text.
    // Small strips: the one the view scrolls to is drawn within a frame, so a fling adds a
    // little text every few frames instead of a large batch at once.
    private const int ChunkLines = 16;
    private readonly Dictionary<int, LineChunk> _chunks = new();
    private readonly Stack<LineChunk> _spareChunks = new();
    private readonly CaretLayer _caretLayer;
    // Lines with strips (and highlights drawn): _winFrom.._winTo.
    private int _winFrom, _winTo = -1;
    // The strips were placed for a viewport reported by the layout (not a guess before one came).
    private bool _windowFits;
    // Strips to place on the next arrange (laid out again, added, or the width changed). Scrolling
    // re-arranges the editor every frame at a new position; without changes there is nothing to do.
    private bool _chunksDirty = true;
    private Size _arrangedSize;

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
    // How far the view can move before the lines drawn around it run out (redraw past these).
    private double _drawnTop, _drawnBottom;

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
        _caretLayer = new CaretLayer(this);
        VisualChildren.Add(_caretLayer);
        _blink = new DispatcherTimer { Interval = CaretBlink };
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; _caretLayer.InvalidateVisual(); };
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
        // Scrolling moves the drawn strips without redrawing them; strips are added and dropped
        // only once the visible part nears the edge of those there are. A partly visible editor
        // (the page scrolling it in) gets a new viewport height every frame, and redrawing on
        // each made touch scrolling stutter on phones.
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            var top = Math.Max(0, _viewport.Top);
            var bottom = Math.Min(_contentHeight, _viewport.Bottom);
            if (bottom > top && (!_windowFits || top < _drawnTop || bottom > _drawnBottom) && UpdateWindow())
            {
                InvalidateArrange();
                InvalidateVisual();
            }
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
            InvalidateText();
            _caretLayer.InvalidateVisual();
        }
    }

    /// <summary>The caret or the selection moved: the highlights and the caret draw again, the text does not.</summary>
    private void InvalidateCaret()
    {
        InvalidateVisual();
        _caretLayer.InvalidateVisual();
    }

    /// <summary>The bytes, their colors or their layout changed: every strip of lines draws again.</summary>
    private void InvalidateText()
    {
        foreach (var chunk in _chunks.Values) chunk.InvalidateVisual();
    }

    private void OnBufferChanged(object? sender, EventArgs e)
    {
        InvalidateText();
        if (_buffer is { } buf && _lineY.Length != LineCount(buf))
        {
            // Rows were added or removed: lay the lines out again and keep the caret inside.
            if (_caret >= buf.ViewLength) { _caret = Math.Max(0, buf.ViewLength - 1); _lowNibble = false; }
            if (_anchor >= buf.ViewLength) _anchor = -1;
            InvalidateMeasure();
        }
        UpdateStatus();
        InvalidateCaret();
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

    // A line holds part of a row, a whole row or (RowsPerLine) several whole rows.
    private int LineCount(HexBuffer buf) => (int)((buf.ViewLength + _perLine - 1) / _perLine);
    private int RowOfLine(int line) => (int)((long)line * _perLine / _buffer!.RowLength);
    private bool StartsRow(int line) => (long)line * _perLine % _buffer!.RowLength == 0;

    /// <summary>Bytes of a line that are in the buffer: the last line of packed rows can be shorter.</summary>
    private int LineBytes(HexBuffer buf, int line) => (int)Math.Min(_perLine, buf.ViewLength - (long)line * _perLine);

    /// <summary>Bytes between group gaps: fours where the line splits into fours, else none.</summary>
    private static int GroupSize(int perLine) => perLine % 4 == 0 ? 4 : perLine;

    private double HexWidth(int perLine) =>
        perLine * 2 * _digitW + (perLine - 1) * _byteGap + (perLine - 1) / GroupSize(perLine) * _groupGap;

    protected override Size MeasureOverride(Size availableSize)
    {
        InvalidateText();
        _chunksDirty = true;
        _textCache.Clear();
        _typeface = new Typeface(FontFamily);
        var glyphMap = _typeface.GlyphTypeface.CharacterToGlyphMap;
        _canBatchText = Enumerable.Range(0x20, 0x7F - 0x20).Append(0xB7)
            .All(glyphMap.ContainsGlyph);
        if (_canBatchText)
        {
            for (var c = 0x20; c < 0x7F; c++) _asciiGlyphs[c] = glyphMap[c];
            _dotGlyph = glyphMap[0xB7];
        }
        _digitW = "0123456789ABCDEF".Max(c => Text(c.ToString(), Brushes.Black).WidthIncludingTrailingWhitespace);
        _canBatchText &= "0123456789ABCDEF".All(c =>
            Math.Abs(Text(c.ToString(), Brushes.Black).WidthIncludingTrailingWhitespace - _digitW) < 0.001);
        _lineH = LineHeight;
        _byteGap = _digitW * 0.6;
        _groupGap = _digitW * 0.6;
        _byteStride = 2 * _digitW + _byteGap;
        _textCache.Clear();

        if (_buffer is null) { _lineY = Array.Empty<double>(); _contentHeight = 0; return default; }

        var labelChars = Enumerable.Range(0, _buffer.ViewRowCount).Select(r => _buffer.RowLabel(r).Length).DefaultIfEmpty(2).Max();
        _labelW = (labelChars + 1.5) * _digitW;
        _hexX = _labelW;

        // Widest layout that fits with the ASCII column (wrapping rows to 8 / 4 bytes
        // if needed); only when even that doesn't fit, drop ASCII.
        var avail = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        // Several short rows side by side where allowed (a divisor of RowsPerLine, so 4 pages
        // fall back to 2, then 1), else the whole row on one line (up to 16 bytes, e.g. a
        // 6-byte key), else halves, quarters…
        var rowLength = _buffer.RowLength;
        var rowsPerLine = Math.Max(1, _buffer.RowsPerLine);
        var candidates = Enumerable.Range(1, rowsPerLine).Where(k => rowsPerLine % k == 0).Select(k => k * rowLength)
            .Concat([16, 8, 4, 2, 1])
            .Where(n => n <= 16 && (n <= rowLength ? rowLength % n == 0 : n % rowLength == 0 && rowsPerLine % (n / rowLength) == 0))
            .Distinct().OrderDescending().ToArray();
        (int perLine, bool ascii) pick = (candidates[^1], false);
        var asciiMin = Math.Min(4, rowLength);
        if (candidates.FirstOrDefault(n => n >= asciiMin && _hexX + HexWidth(n) + 2 * _digitW + n * _digitW <= avail) is > 0 and var withAscii)
            pick = (withAscii, true);
        else if (candidates.FirstOrDefault(n => _hexX + HexWidth(n) <= avail) is > 0 and var hexOnly)
            pick = (hexOnly, false);
        (_perLine, _showAscii) = pick;
        _asciiX = _hexX + HexWidth(_perLine) + 2 * _digitW;
        if (!_showAscii && _pane == Pane.Ascii) _pane = Pane.Hex;

        var lines = LineCount(_buffer);
        _lineY = new double[lines];
        double y = 0;
        for (var i = 0; i < lines; i++)
        {
            var row = RowOfLine(i);
            if (StartsRow(i))
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _arrangedSize) _chunksDirty = true;
        if (!_chunksDirty) return finalSize;
        _arrangedSize = finalSize;
        if (UpdateWindow()) InvalidateVisual();
        _chunksDirty = false;
        _caretLayer.Measure(finalSize);
        _caretLayer.Arrange(new Rect(finalSize));
        foreach (var (index, chunk) in _chunks)
        {
            var rect = ChunkRect(index, finalSize.Width);
            // The group separators run the whole width.
            if (Math.Abs(chunk.Bounds.Width - rect.Width) > 1e-7) chunk.InvalidateVisual();
            chunk.Measure(rect.Size);
            chunk.Arrange(rect);
        }
        return finalSize;
    }

    private int ChunkCount => (_lineY.Length + ChunkLines - 1) / ChunkLines;

    /// <summary>Top of a line's band: just under the line before, so a separator and heading above it are inside.</summary>
    private double LineTop(int line) => line <= 0 ? 0 : _lineY[line - 1] + _lineH;

    private Rect ChunkRect(int index, double width)
    {
        var first = index * ChunkLines;
        var last = Math.Min(first + ChunkLines, _lineY.Length) - 1;
        var top = LineTop(first);
        return new Rect(0, top, width, Math.Max(0, _lineY[last] + _lineH - top));
    }

    /// <summary>
    /// Keeps strips of lines for the view plus a screenful around it (the window's height, not
    /// the view's, which is short while the editor is partly scrolled in), and drops the rest.
    /// </summary>
    /// <returns>Whether strips were added or dropped.</returns>
    private bool UpdateWindow()
    {
        var changed = false;
        if (_buffer is null || _lineY.Length == 0)
        {
            foreach (var index in _chunks.Keys.ToArray()) changed |= DropChunk(index);
            (_winFrom, _winTo) = (0, -1);
            (_drawnTop, _drawnBottom) = (double.NegativeInfinity, double.PositiveInfinity);
            return changed;
        }

        var client = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 0;
        _windowFits = _viewport.Height > 0;
        // Before the layout says what is on screen, assume the top of the editor is.
        var view = _windowFits ? _viewport : new Rect(0, 0, Bounds.Width, client > 0 ? client : 1000);
        var margin = Math.Max(view.Height, client);
        var lastChunk = ChunkCount - 1;
        var from = Math.Clamp(LineAt(view.Top - margin) / ChunkLines, 0, lastChunk);
        var to = Math.Clamp(LineAt(view.Bottom + margin) / ChunkLines, from, lastChunk);
        (_winFrom, _winTo) = (from * ChunkLines, Math.Min(_lineY.Length, (to + 1) * ChunkLines) - 1);
        // Move the window on once the view has scrolled about a strip, so each step adds one strip
        // (a screenful ahead of the view, so no blank lines show) rather than several in one frame.
        var lead = Math.Max(margin / 2, margin - ChunkLines * _lineH);
        _drawnTop = from > 0 ? LineTop(_winFrom) + lead : double.NegativeInfinity;
        _drawnBottom = to < lastChunk ? _lineY[_winTo] + _lineH - lead : double.PositiveInfinity;

        foreach (var index in _chunks.Keys.Where(i => i < from || i > to).ToArray())
            changed |= DropChunk(index);
        for (var index = from; index <= to; index++)
        {
            if (_chunks.ContainsKey(index)) continue;
            var chunk = _spareChunks.Count > 0 ? _spareChunks.Pop() : new LineChunk(this);
            chunk.Index = index;
            _chunks[index] = chunk;
            // Under the caret layer, which stays last.
            VisualChildren.Insert(VisualChildren.Count - 1, chunk);
            chunk.InvalidateVisual();
            changed = _chunksDirty = true;
        }
        return changed;
    }

    private bool DropChunk(int index)
    {
        if (!_chunks.Remove(index, out var chunk)) return false;
        VisualChildren.Remove(chunk);
        chunk.ReleaseRuns();
        if (_spareChunks.Count < 8) _spareChunks.Push(chunk);
        return true;
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
            var col = Math.Clamp((int)((p.X - _asciiX) / _digitW), 0, LineBytes(_buffer!, line) - 1);
            return ((long)line * _perLine + col, false, Pane.Ascii);
        }

        var best = 0;
        for (var c = 0; c < LineBytes(_buffer!, line); c++)
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

    // Batch a line by brush instead of recording a separate text draw for every byte (and its
    // row label: a label of its own was laid out as text for every row, thousands in a key list).
    // Explicit glyph advances preserve the cell positions, including fractional group gaps.
    // The caller owns (and disposes) the runs. Returns whether the row label is in them.
    private bool LineText(int line, HexBuffer buf, IBrush fg, IBrush dim, IBrush changed, IBrush label,
        List<(IBrush Brush, GlyphRun Run)> runs)
    {
        // The per-brush lists are kept between lines: a strip of lines is drawn as the view scrolls
        // to it, and allocating them for every line made garbage collections cut into the scrolling.
        // Brushes change only with the theme; drop the lists of those no longer used.
        if (_glyphs.Count >= 16) _glyphs.Clear();
        foreach (var list in _glyphs.Values) list.Clear();
        var face = _typeface.GlyphTypeface;
        var sample = Text("00", fg);
        var baseline = (_lineH - sample.Height) / 2 + sample.Baseline;
        void Add(string text, IBrush brush, double center) => AddGlyphs(text, brush, center);

        var labelDrawn = false;
        if (StartsRow(line))
        {
            var row = RowOfLine(line);
            var rowLabel = buf.RowLabel(row);
            // Printable ASCII is covered (checked during measure); anything else is laid out as text.
            if (rowLabel.All(ch => ch is >= ' ' and < (char)0x7F))
            {
                var labelBrush = buf.IsRead(row) ? label : dim;
                double labelX = 0;
                foreach (var ch in rowLabel)
                {
                    var glyphText = AsciiBytes[ch];
                    var advance = Text(glyphText, labelBrush).WidthIncludingTrailingWhitespace;
                    Add(glyphText, labelBrush, labelX + advance / 2);
                    labelX += advance;
                }
                labelDrawn = true;
            }
        }

        for (var col = 0; col < LineBytes(buf, line); col++)
        {
            var offset = (long)line * _perLine + col;
            var row = (int)(offset / buf.RowLength);
            var read = buf.IsRead(row);
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

        foreach (var (brush, list) in _glyphs)
        {
            if (list.Count == 0) continue;
            list.Sort(static (a, b) => a.X.CompareTo(b.X));
            var info = new GlyphInfo[list.Count];
            for (var i = 0; i < info.Length; i++)
                info[i] = new GlyphInfo(list[i].Glyph, i,
                    i + 1 < info.Length ? list[i + 1].X - list[i].X : _digitW);
            runs.Add((brush, new GlyphRun(face, FontSize, ReadOnlyMemory<char>.Empty, info,
                new Point(list[0].X, baseline))));
        }
        return labelDrawn;
    }

    private readonly Dictionary<IBrush, List<(ushort Glyph, double X)>> _glyphs = new();
    // Glyphs of printable ASCII and '·' (looked up once per layout; the map is a lookup per character).
    private readonly ushort[] _asciiGlyphs = new ushort[0x80];
    private ushort _dotGlyph;

    private void AddGlyphs(string text, IBrush brush, double center)
    {
        var width = Text(text, brush).WidthIncludingTrailingWhitespace;
        var x = center - width / 2;
        if (!_glyphs.TryGetValue(brush, out var list)) _glyphs[brush] = list = new();
        // Coverage and fixed-width hex digits were checked during measure.
        foreach (var c in text)
        {
            var glyph = c < 0x80 ? _asciiGlyphs[c] : c == '·' ? _dotGlyph : _typeface.GlyphTypeface.CharacterToGlyphMap[c];
            list.Add((glyph, x));
            x += width / text.Length;
        }
    }

    // The editor draws what changes with the caret: the active row, the selection and the caret's
    // cell, under the strips of text. Only within the lines that have strips.
    public override void Render(DrawingContext ctx)
    {
        var buf = _buffer;
        if (buf is null || _lineY.Length == 0) return;

        // Paint the whole area so clicks between and around bytes still reach the editor.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_winTo < _winFrom || buf.ViewLength == 0) return;

        var caretLine = LineOf(_caret);
        var caretShown = caretLine >= _winFrom && caretLine <= _winTo;
        if (caretShown && ActiveRowBrush is { } activeRow)
            ctx.FillRectangle(activeRow, new Rect(0, _lineY[caretLine], Bounds.Width, _lineH), 4);

        var panes = _showAscii ? 2 : 1;
        var (selFrom, selTo) = Selection();
        if (selTo >= selFrom && SelectionBrush is { } selBrush)
        {
            var from = Math.Max(selFrom, (long)_winFrom * _perLine);
            var to = Math.Min(Math.Min(selTo, ((long)_winTo + 1) * _perLine - 1), buf.ViewLength - 1);
            for (var offset = from; offset <= to; offset++)
                for (var p = 0; p < panes; p++)
                    ctx.FillRectangle(selBrush, CellRect(offset, p == 0 ? Pane.Hex : Pane.Ascii));
        }

        if (caretShown && (_caret < selFrom || _caret > selTo) && CaretCellBrush is { } caretCell)
            for (var p = 0; p < panes; p++)
                ctx.FillRectangle(caretCell, CellRect(_caret, p == 0 ? Pane.Hex : Pane.Ascii).Deflate(new Thickness(0, 2)), 3);
    }

    /// <summary>Draws the text of lines <paramref name="first"/>..<paramref name="last"/> for a strip whose top is at <paramref name="top"/>.</summary>
    private void RenderLines(DrawingContext ctx, int first, int last, double top, double width,
        List<(IBrush Brush, GlyphRun Run)> runs)
    {
        var buf = _buffer;
        if (buf is null || _lineY.Length == 0) return;
        last = Math.Min(last, _lineY.Length - 1);

        var fg = Foreground ?? Brushes.White;
        var dim = DimBrush ?? fg;
        var label = LabelBrush ?? dim;
        var changed = ChangedBrush ?? fg;
        // Group headings and labels laid out as text are few; do not keep every one seen while scrolling.
        if (_textCache.Count > 4096) _textCache.Clear();

        using var strip = ctx.PushTransform(Matrix.CreateTranslation(0, -top));
        for (var line = first; line <= last; line++)
        {
            var y = _lineY[line];
            var row = RowOfLine(line);
            var firstOfRow = StartsRow(line);

            var header = firstOfRow && ShowGroupHeaders ? buf.GroupHeader(row) : null;
            var headerTop = header is null ? y : y - _lineH;
            if (firstOfRow && row > 0 && buf.StartsGroup(row) && SeparatorBrush is { } sep)
                ctx.FillRectangle(sep, new Rect(0, headerTop - SeparatorGap / 2 - 0.5, width, 1));

            if (header is { } h)
            {
                var title = Text(h.Title, label);
                ctx.DrawText(title, new Point(0, headerTop + (_lineH - title.Height) / 2));
                if (h.Detail is { } detail)
                {
                    var ft = Text(detail, dim);
                    ctx.DrawText(ft, new Point(title.WidthIncludingTrailingWhitespace + 2 * _digitW, headerTop + (_lineH - ft.Height) / 2));
                }
            }

            var labelDrawn = false;
            if (_canBatchText)
            {
                var from = runs.Count;
                labelDrawn = LineText(line, buf, fg, dim, changed, label, runs);
                using (ctx.PushTransform(Matrix.CreateTranslation(0, y)))
                    for (var i = from; i < runs.Count; i++)
                        ctx.DrawGlyphRun(runs[i].Brush, runs[i].Run);
            }

            if (firstOfRow && !labelDrawn)
            {
                var ft = Text(buf.RowLabel(row), buf.IsRead(row) ? label : dim);
                ctx.DrawText(ft, new Point(0, y + (_lineH - ft.Height) / 2));
            }

            if (_canBatchText) continue;
            for (var col = 0; col < LineBytes(buf, line); col++)
            {
                var offset = (long)line * _perLine + col;
                var cellRow = (int)(offset / buf.RowLength);
                var rowCol = (int)(offset % buf.RowLength);
                var rowRead = buf.IsRead(cellRow);
                IBrush brush;
                string hex, ascii;
                if (!rowRead)
                {
                    brush = buf.IsTemplateKnown(offset) ? KindBrush(buf.Classify(cellRow, rowCol), fg) : dim;
                    var template = buf.TemplateAt(offset);
                    hex = template is { } t ? HexBytes[t] : "--";
                    ascii = template is { } a ? AsciiBytes[a] : "·";
                }
                else
                {
                    var b = buf[offset];
                    brush = buf.IsChanged(offset) ? changed : KindBrush(buf.Classify(cellRow, rowCol), fg);
                    hex = HexBytes[b];
                    ascii = AsciiBytes[b];
                }

                DrawCentered(ctx, hex, brush, ByteX(col) + _digitW, y);
                if (_showAscii)
                    DrawCentered(ctx, ascii, rowRead && ascii != "·" ? brush : dim, AsciiX(col) + _digitW / 2, y);
            }
        }
    }

    // Caret: a blinking text-cursor bar before the active nibble (hex) or character (ASCII).
    private void RenderCaret(DrawingContext ctx)
    {
        if (_buffer is not { ViewLength: > 0 } || _lineY.Length == 0 || !IsKeyboardFocusWithin || !_caretOn ||
            CaretBrush is not { } caret)
            return;
        var line = LineOf(_caret);
        if (line >= _lineY.Length) return;
        var col = (int)(_caret % _perLine);
        var cx = _pane == Pane.Hex ? ByteX(col) + (_lowNibble ? _digitW : 0) : AsciiX(col);
        ctx.FillRectangle(caret, new Rect(cx - 1, _lineY[line] + 2, 2, _lineH - 4));
    }

    /// <summary>A strip of <see cref="ChunkLines"/> lines of text; keeps what it drew until the text changes.</summary>
    private sealed class LineChunk : Control
    {
        private readonly HexEditor _owner;
        private readonly List<(IBrush Brush, GlyphRun Run)> _runs = new();

        public LineChunk(HexEditor owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public int Index { get; set; }

        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context)
        {
            ReleaseRuns();
            var first = Index * ChunkLines;
            _owner.RenderLines(context, first, first + ChunkLines - 1, Bounds.Top, Bounds.Width, _runs);
        }

        public void ReleaseRuns()
        {
            foreach (var (_, run) in _runs) run.Dispose();
            _runs.Clear();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            ReleaseRuns();
        }
    }

    /// <summary>The caret bar above the text, so blinking draws only the bar.</summary>
    private sealed class CaretLayer : Control
    {
        private readonly HexEditor _owner;

        public CaretLayer(HexEditor owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context) => _owner.RenderCaret(context);
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
        InvalidateCaret();
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
            value = string.Format(Strings.Hex_Selected, to - from + 1);
        }
        else if (buf.IsRead(row))
        {
            var b = buf[_caret];
            value = $"{b:X2} {b,4} {Convert.ToString(b, 2).PadLeft(8, '0')}";
        }
        else if (buf.IsTemplateKnown(_caret) && buf.TemplateAt(_caret) is { } k)
            value = $"{k:X2} {k,4} {Convert.ToString(k, 2).PadLeft(8, '0')}";
        else value = Strings.Hex_NotRead;

        StatusText = $"{buf.RowLabel(row).PadLeft(labelWidth)}:{col.ToString().PadLeft(colWidth, '0')}  " +
                     $"0x{(buf.ByteOffset + _caret).ToString("X" + offsetWidth)}  {value.PadRight(valueWidth, ' ')}";
        StatusDetail = buf.Describe(row, col) is { Length: > 0 } what ? what : null;
        var title = Strings.Hex_Memory;
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
            var note = Strings.Hex_FromIdentity;
            SelectionStatus = new(title, detail is null ? note : $"{detail} · {note}", position, known.ToString("X2"),
                known.ToString(CultureInfo.InvariantCulture), Convert.ToString(known, 2).PadLeft(8, '0'), null);
            return;
        }
        SelectionStatus = !HasSelection && buf.IsRead(row)
            ? new(title, detail, position, buf[_caret].ToString("X2"), buf[_caret].ToString(CultureInfo.InvariantCulture),
                Convert.ToString(buf[_caret], 2).PadLeft(8, '0'), null)
            : new(title, detail, position, null, null, null,
                HasSelection ? string.Format(Strings.Hex_Selected, Selection().to - Selection().from + 1) : Strings.Hex_NotRead);
    }

    // ================================================================ Input

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _pointerFocus = e.NavigationMethod == NavigationMethod.Pointer;
        RestartBlink();
        _caretLayer.InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _pointerFocus = false;
        _blink.Stop();
        _caretLayer.InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
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
            InvalidateCaret();
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
        if (_buffer is { IsReadOnly: false } && _buffer.Undo()) InvalidateCaret();
    }

    private void Redo()
    {
        if (_buffer is { IsReadOnly: false } && _buffer.Redo()) InvalidateCaret();
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
        catch (Exception)
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
        catch (Exception)
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

        var undo = Item(Strings.Hex_Undo, "Ctrl+Z", Undo);
        var redo = Item(Strings.Hex_Redo, "Ctrl+Y", Redo);
        var insert = Item(Strings.Hex_AddRowBelow, "Enter", InsertRowBelow);
        var remove = Item(Strings.Hex_DeleteRows, "Delete", RemoveRows);
        var rowsSeparator = new Separator();
        var paste = Item(Strings.Hex_Paste, "Ctrl+V", Paste);
        var historySeparator = new Separator();
        var menu = new ContextMenu
        {
            Items =
            {
                Item(Strings.Hex_Copy, "Ctrl+C", Copy),
                paste,
                rowsSeparator,
                insert,
                remove,
                historySeparator,
                undo,
                redo,
                new Separator(),
                Item(Strings.Hex_SelectAll, "Ctrl+A", SelectAll),
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
