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
    public static readonly StyledProperty<IBrush?> StripeBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush?>(nameof(StripeBrush));

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
    // Offset of each line's first cell (one more entry for the end), the row it shows and
    // whether it starts that row: a line holds part of a row, a whole row or several whole rows.
    private long[] _lineStart = [0];
    private int[] _lineRow = Array.Empty<int>();
    // Lines under the faint band of every other record (row, or rows packed on a line).
    private bool[] _lineStriped = Array.Empty<bool>();
    private bool[] _lineFirst = Array.Empty<bool>();
    private double _contentHeight;

    // Caret / selection, as byte offsets into the buffer.
    private long _caret;
    private bool _lowNibble;
    private long _anchor = -1;
    private Pane _pane = Pane.Hex;
    private bool _dragging;
    private bool _pointerFocus, _movingCaret;
    // Touch selection, as on phones: a long press selects the byte under the finger (dragging on
    // extends it); handles at both ends adjust it and a bar above it offers the commands.
    private readonly TouchSelectionLayer _touchLayer;
    private bool _touchSelect, _touchBarShown;
    // A finger is down (not yet a scroll), the press became a long press, a drag after it extends.
    private bool _touchDown, _touchHeld, _touchExtending;
    private IPointer? _touchPointer;
    private Point _touchPressPoint;
    // A handle being dragged: which one, and where on it the finger took it.
    private bool _handleIsStart;
    private Vector _handleGrab;
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
            CaretCellBrushProperty, SelectionBrushProperty, ActiveRowBrushProperty, SeparatorBrushProperty, StripeBrushProperty);
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
        // Above the strips and the caret (z-index), and a logical child so its buttons get styled.
        _touchLayer = new TouchSelectionLayer(this) { ZIndex = 1 };
        VisualChildren.Insert(0, _touchLayer);
        LogicalChildren.Add(_touchLayer);
        SetValue(IsHoldingEnabledProperty, true);
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
            // Keep the selection bar on screen as the page scrolls.
            if (_touchLayer.Bar.IsVisible) _touchLayer.InvalidateArrange();
        };
        TextInputOptions.SetShowSuggestions(this, false);
        TextInputOptions.SetAutoCapitalization(this, false);
        TextInputOptions.SetMultiline(this, false);
        // A long press selects (with handles and a bar) instead of opening the menu. Added before
        // the menu, which opens on this event unless it is handled.
        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (!_touchDown) return;
            e.Handled = true;
            BeginTouchSelection();
        });
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
    /// <summary>Faint band behind every other line (translucent, over the active-row highlight).</summary>
    public IBrush? StripeBrush { get => GetValue(StripeBrushProperty); set => SetValue(StripeBrushProperty, value); }

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
            _touchSelect = false;
            UpdateTouchUi();
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
        if (_buffer is { } buf && _lineY.Length > 0)
        {
            if (_caret >= buf.ViewLength) { _caret = Math.Max(0, buf.ViewLength - 1); _lowNibble = false; }
            if (_anchor >= buf.ViewLength) _anchor = -1;
            // Rows were added or removed, or a row got longer or shorter: lay the lines out again
            // now (the caret is placed by them) and the strips on the next layout if they moved.
            RelayOutLines(buf);
        }
        UpdateStatus();
        InvalidateCaret();
        UpdateTouchUi();
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

    private int RowOfLine(int line) => _lineRow[line];
    private bool StartsRow(int line) => _lineFirst[line];

    /// <summary>
    /// Cells of a line: the last line of a row (or of packed rows) can be shorter. A hidden tail
    /// cell (see <see cref="IsHiddenTail"/>) is counted to the line before but never shown there.
    /// </summary>
    private int LineBytes(HexBuffer buf, int line) => (int)Math.Min(_lineStart[line + 1] - _lineStart[line], _perLine);

    /// <summary>
    /// A line that would hold only the cell for adding a byte after a row's bytes (a row filling
    /// whole lines, e.g. 8 bytes at 8 a line): left out unless the caret is on that cell, so full
    /// rows don't each trail an empty line. The cell then belongs to the line before, past its end.
    /// </summary>
    private bool IsHiddenTail(HexBuffer buf, int row, int col, long offset) =>
        col > 0 && buf.RowViewLength(row) - col == 1 && buf.RowDataLength(row) == col && offset != _caret;

    /// <summary>Whether a cell is a row's tail cell that takes a line only while the caret is on it.</summary>
    private bool TailOnOwnLine(long offset)
    {
        if (_buffer is not { VariableRowLength: true } buf || offset < 0 || offset >= buf.ViewLength) return false;
        int row = buf.RowAt(offset), col = buf.ColumnAt(offset);
        return col % _perLine == 0 && IsHiddenTail(buf, row, col, -1);
    }

    /// <summary>Whether a cell sits on a line of its own (not a hidden tail past a line's end).</summary>
    private bool IsCellShown(long offset) => offset - _lineStart[LineOf(offset)] < _perLine;

    /// <summary>
    /// Lays the lines out again after the bytes or the caret changed them (rows added, removed,
    /// longer or shorter, or a tail cell shown or hidden), and the strips if they moved.
    /// </summary>
    private void RelayOutLines(HexBuffer buf)
    {
        var lines = _lineY.Length;
        var height = _contentHeight;
        LayOutLines(buf);
        if (_lineY.Length != lines || _contentHeight != height)
        {
            _chunksDirty = true;
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Splits the rows into lines of up to <see cref="_perLine"/> cells: equally long rows run on
    /// from line to line (several to a line where allowed), a variable-length row starts a line
    /// of its own. Then places the lines, leaving room for group separators and headings.
    /// </summary>
    private void LayOutLines(HexBuffer buf)
    {
        int lines;
        if (!buf.VariableRowLength)
        {
            lines = (int)((buf.ViewLength + _perLine - 1) / _perLine);
            _lineStart = new long[lines + 1];
            _lineRow = new int[lines];
            _lineFirst = new bool[lines];
            for (var i = 0; i < lines; i++)
            {
                var start = (long)i * _perLine;
                _lineStart[i] = start;
                _lineRow[i] = (int)(start / buf.RowLength);
                _lineFirst[i] = start % buf.RowLength == 0;
            }
            _lineStart[lines] = buf.ViewLength;
        }
        else
        {
            var starts = new List<long>();
            var rows = new List<int>();
            var firsts = new List<bool>();
            for (var row = 0; row < buf.ViewRowCount; row++)
            {
                var start = buf.RowStart(row);
                var length = buf.RowViewLength(row);
                for (var col = 0; col < length; col += _perLine)
                {
                    if (IsHiddenTail(buf, row, col, start + col)) continue;
                    starts.Add(start + col);
                    rows.Add(row);
                    firsts.Add(col == 0);
                }
            }
            lines = starts.Count;
            starts.Add(buf.ViewLength);
            _lineStart = starts.ToArray();
            _lineRow = rows.ToArray();
            _lineFirst = firsts.ToArray();
        }

        _lineY = new double[lines];
        _lineStriped = new bool[lines];
        double y = 0;
        var records = 0;
        for (var i = 0; i < lines; i++)
        {
            var row = RowOfLine(i);
            // A row wrapped over several lines is one record and one band; packed rows share a line's.
            if (StartsRow(i)) records++;
            _lineStriped[i] = records % 2 == 1;
            if (StartsRow(i))
            {
                if (row > 0 && buf.StartsGroup(row)) y += SeparatorGap;
                // A group heading takes a line of its own above the group's first row.
                if (ShowGroupHeaders && buf.GroupHeader(row) is not null) y += _lineH;
            }
            _lineY[i] = y;
            y += _lineH;
        }
        _contentHeight = y;
    }

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
        // The bytes are drawn as glyphs placed by their own advances, so any font with printable
        // ASCII and '·' will do: the font list falls back to a proportional one (Roboto on Android),
        // and requiring same-width hex digits sent every byte through a text layout of its own there.
        var face = _typeface.GlyphTypeface;
        var glyphMap = face.CharacterToGlyphMap;
        _canBatchText = Enumerable.Range(0x20, 0x7F - 0x20).Append(0xB7)
            .All(glyphMap.ContainsGlyph);
        if (_canBatchText)
        {
            var scale = FontSize / face.Metrics.DesignEmHeight;
            double Advance(ushort glyph)
            {
                if (face.TryGetHorizontalGlyphAdvance(glyph, out var advance)) return advance * scale;
                _canBatchText = false;
                return 0;
            }
            for (var c = 0x20; c < 0x7F; c++)
            {
                _asciiGlyphs[c] = glyphMap[c];
                _asciiAdvances[c] = Advance(_asciiGlyphs[c]);
            }
            _dotGlyph = glyphMap[0xB7];
            _dotAdvance = Advance(_dotGlyph);
        }
        _digitW = "0123456789ABCDEF".Max(c => Text(c.ToString(), Brushes.Black).WidthIncludingTrailingWhitespace);
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
        // Variable-length rows each start a line: room for the longest (RowLength).
        var rowsPerLine = _buffer.VariableRowLength ? 1 : Math.Max(1, _buffer.RowsPerLine);
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

        LayOutLines(_buffer);

        var width = _showAscii ? _asciiX + _perLine * _digitW : _hexX + HexWidth(_perLine);
        return new Size(Math.Min(width, avail), _contentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _touchLayer.Measure(finalSize);
        _touchLayer.Arrange(new Rect(finalSize));
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

    private int LineOf(long offset)
    {
        var lines = _lineY.Length;
        if (lines == 0) return 0;
        var line = Array.BinarySearch(_lineStart, 0, lines, offset);
        if (line < 0) line = ~line - 1;
        return Math.Clamp(line, 0, lines - 1);
    }

    /// <summary>Position of a cell in its line.</summary>
    private int ColumnInLine(long offset) => (int)(offset - _lineStart[LineOf(offset)]);

    /// <summary>The cell of a line at a column, or the line's last cell when it is shorter.</summary>
    private long CellOfLine(int line, int col) =>
        _lineStart[line] + Math.Min(col, LineBytes(_buffer!, line) - 1);

    private Rect CellRect(long offset, Pane pane)
    {
        int line = LineOf(offset), col = (int)(offset - _lineStart[line]);
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
    private (long offset, bool low, Pane pane) HitTest(Point p, Pane? pane = null)
    {
        var line = LineAt(p.Y);

        if (pane == Pane.Ascii || (pane is null && _showAscii && p.X >= _asciiX - _digitW))
        {
            var col = Math.Clamp((int)((p.X - _asciiX) / _digitW), 0, LineBytes(_buffer!, line) - 1);
            return (_lineStart[line] + col, false, Pane.Ascii);
        }

        var best = 0;
        for (var c = 0; c < LineBytes(_buffer!, line); c++)
            if (p.X >= ByteX(c) - _byteGap / 2) best = c;
        var low = p.X >= ByteX(best) + _digitW;
        // The cell after a short row's last byte has no nibbles yet: typing starts the byte.
        var offset = _lineStart[line] + best;
        return (offset, low && _buffer!.IsRead(offset), Pane.Hex);
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

    // Batch a strip of lines by brush instead of recording a separate text draw for every byte (and
    // its row label: a label of its own was laid out as text for every row, thousands in a key list).
    // Every glyph is placed by its own offset, so a strip is one glyph run per color rather than
    // a run and a transform for every line, which the renderer replays every frame of a scroll.
    // Adds the glyphs of a line whose top is at y; returns whether the row label is among them.
    private bool LineGlyphs(int line, HexBuffer buf, IBrush fg, IBrush dim, IBrush changed, IBrush label, double y)
    {
        var sample = Text("00", fg);
        var baseline = y + (_lineH - sample.Height) / 2 + sample.Baseline;
        void Add(string text, IBrush brush, double center) => AddGlyphs(text, brush, center, baseline);

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
                    var advance = _asciiAdvances[ch];
                    Add(AsciiBytes[ch], labelBrush, labelX + advance / 2);
                    labelX += advance;
                }
                labelDrawn = true;
            }
        }

        for (var col = 0; col < LineBytes(buf, line); col++)
        {
            var offset = _lineStart[line] + col;
            var row = RowOfLine(line);
            var read = buf.IsRead(offset);
            // The cell after a short row's last byte stays blank (an empty row shows dashes there).
            if (!read && buf.VariableRowLength && buf.IsRead(row)) continue;
            // An unread row shows its template (blank-card defaults) dimmed, or dashes without one.
            var template = read ? null : buf.TemplateAt(offset);
            var b = read ? buf[offset] : template ?? 0;
            var known = !read && buf.IsTemplateKnown(offset);
            var brush = !read && !known ? dim
                : buf.IsChanged(offset) ? changed : KindBrush(buf.Classify(buf.RowAt(offset), buf.ColumnAt(offset)), fg);
            Add(read || template is not null ? HexBytes[b] : "--", brush, ByteX(col) + _digitW);
            if (_showAscii)
                Add(read || template is not null ? AsciiBytes[b] : "·", read && AsciiBytes[b] != "·" ? brush : dim, AsciiX(col) + _digitW / 2);
        }
        return labelDrawn;
    }

    /// <summary>Starts collecting the glyphs of a strip.</summary>
    private void BeginGlyphs()
    {
        // The per-brush lists are kept between strips, so drawing one allocates little.
        // Brushes change only with the theme; drop the lists of those no longer used.
        if (_glyphs.Count >= 16) _glyphs.Clear();
        foreach (var list in _glyphs.Values) list.Clear();
    }

    /// <summary>Turns the collected glyphs into one run per brush (the caller owns and disposes them).</summary>
    private void EndGlyphs(List<(IBrush Brush, GlyphRun Run)> runs)
    {
        var face = _typeface.GlyphTypeface;
        foreach (var (brush, list) in _glyphs)
        {
            if (list.Count == 0) continue;
            var info = new GlyphInfo[list.Count];
            for (var i = 0; i < info.Length; i++)
                info[i] = new GlyphInfo(list[i].Glyph, i, 0, new Vector(list[i].X, list[i].Y));
            // An explicit origin: left out (null), the run would start at the font's ascent instead.
            runs.Add((brush, new GlyphRun(face, FontSize, ReadOnlyMemory<char>.Empty, info, new Point(0, 0))));
        }
    }

    private readonly Dictionary<IBrush, List<(ushort Glyph, double X, double Y)>> _glyphs = new();
    // Glyphs of printable ASCII and '·' and their advances (looked up once per layout).
    private readonly ushort[] _asciiGlyphs = new ushort[0x80];
    private readonly double[] _asciiAdvances = new double[0x80];
    private ushort _dotGlyph;
    private double _dotAdvance;

    /// <summary>Places a short text of printable ASCII and '·' (covered, as measure checked) centered on x.</summary>
    private void AddGlyphs(string text, IBrush brush, double center, double baseline)
    {
        double width = 0;
        foreach (var c in text) width += c == '·' ? _dotAdvance : _asciiAdvances[c];
        var x = center - width / 2;
        if (!_glyphs.TryGetValue(brush, out var list)) _glyphs[brush] = list = new();
        foreach (var c in text)
        {
            list.Add((c == '·' ? _dotGlyph : _asciiGlyphs[c], x, baseline));
            x += c == '·' ? _dotAdvance : _asciiAdvances[c];
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
        // Only while focused: an idle caret on the first row reads as a stripe of its own.
        if (caretShown && IsKeyboardFocusWithin && ActiveRowBrush is { } activeRow)
            ctx.FillRectangle(activeRow, new Rect(0, _lineY[caretLine], Bounds.Width, _lineH), 4);

        var panes = _showAscii ? 2 : 1;
        var (selFrom, selTo) = Selection();
        // Lines laid out again after an edit can be fewer than the strips placed before it.
        if (selTo >= selFrom && SelectionBrush is { } selBrush && _winTo < _lineY.Length)
        {
            var from = Math.Max(selFrom, _lineStart[_winFrom]);
            var to = Math.Min(Math.Min(selTo, _lineStart[_winTo + 1] - 1), buf.ViewLength - 1);
            for (var offset = from; offset <= to; offset++)
                if (IsCellShown(offset))
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
        // The runs' own bounds leave out the glyph offsets; the strip's area is its content.
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, top, width, _lineY[last] + _lineH - top));
        if (_canBatchText) BeginGlyphs();
        var stripe = StripeBrush;
        for (var line = first; line <= last; line++)
        {
            var y = _lineY[line];
            var row = RowOfLine(line);
            // Every other record gets a faint band, so the eye keeps to it across the grid; a
            // wrapped row's band runs on over its lines, rounded only at its top and bottom.
            if (stripe is not null && _lineStriped[line])
            {
                var upper = StartsRow(line) ? 4 : 0;
                var lower = line + 1 >= _lineY.Length || StartsRow(line + 1) ? 4 : 0;
                ctx.DrawRectangle(stripe, null, new RoundedRect(new Rect(0, y, width, _lineH), upper, upper, lower, lower));
            }
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

            var labelDrawn = _canBatchText && LineGlyphs(line, buf, fg, dim, changed, label, y);

            if (firstOfRow && !labelDrawn)
            {
                var ft = Text(buf.RowLabel(row), buf.IsRead(row) ? label : dim);
                ctx.DrawText(ft, new Point(0, y + (_lineH - ft.Height) / 2));
            }

            if (_canBatchText) continue;
            for (var col = 0; col < LineBytes(buf, line); col++)
            {
                var offset = _lineStart[line] + col;
                var cellRow = buf.RowAt(offset);
                var rowCol = buf.ColumnAt(offset);
                var rowRead = buf.IsRead(offset);
                if (!rowRead && buf.VariableRowLength && buf.IsRead(cellRow)) continue;
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

        if (!_canBatchText) return;
        EndGlyphs(runs);
        foreach (var (brush, run) in runs) ctx.DrawGlyphRun(brush, run);
    }

    // Caret: a blinking text-cursor bar before the active nibble (hex) or character (ASCII).
    private void RenderCaret(DrawingContext ctx)
    {
        if (_buffer is not { ViewLength: > 0 } || _lineY.Length == 0 || !IsKeyboardFocusWithin || !_caretOn ||
            CaretBrush is not { } caret)
            return;
        var line = LineOf(_caret);
        if (line >= _lineY.Length) return;
        var col = (int)(_caret - _lineStart[line]);
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

    // A touch selection can be a single byte; a mouse one is a range.
    private bool HasSelection => _anchor >= 0 && (_anchor != _caret || _touchSelect);

    private void MoveCaret(long offset, bool low, bool extend)
    {
        if (_buffer is not { ViewLength: > 0 }) return;
        if (extend) { if (_anchor < 0) _anchor = _caret; }
        else
        {
            _anchor = -1;
            _touchSelect = false;
        }
        var old = _caret;
        _caret = Math.Clamp(offset, 0, _buffer.ViewLength - 1);
        _lowNibble = low && !extend;
        // A row's tail cell gets a line of its own while the caret is on it (typing past a full line).
        if (old != _caret && _lineY.Length > 0 && (TailOnOwnLine(old) || TailOnOwnLine(_caret)))
        {
            RelayOutLines(_buffer);
            InvalidateText();
        }
        RestartBlink();
        UpdateStatus();
        InvalidateCaret();
        UpdateTouchUi();
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
        int row = buf.RowAt(_caret), col = buf.ColumnAt(_caret);
        var labelWidth = buf.RowLabel(buf.ViewRowCount - 1).Length;
        var colWidth = (buf.RowLength - 1).ToString().Length;
        var offsetWidth = Math.Max(3, (buf.ByteOffset + buf.ViewLength - 1).ToString("X").Length);
        // Variable-length rows: the cell after a row's bytes adds one.
        var unread = buf.VariableRowLength ? Strings.Hex_TypeToAddByte : Strings.Hex_NotRead;

        string value;
        if (HasSelection)
        {
            var (from, to) = Selection();
            value = string.Format(Strings.Hex_Selected, to - from + 1);
        }
        else if (buf.IsRead(_caret))
        {
            var b = buf[_caret];
            value = $"{b:X2} {b,4} {Convert.ToString(b, 2).PadLeft(8, '0')}";
        }
        else if (buf.IsTemplateKnown(_caret) && buf.TemplateAt(_caret) is { } k)
            value = $"{k:X2} {k,4} {Convert.ToString(k, 2).PadLeft(8, '0')}";
        else value = unread;

        StatusText = $"{buf.RowLabel(row).PadLeft(labelWidth)}:{col.ToString().PadLeft(colWidth, '0')}  " +
                     $"0x{buf.DataOffset(_caret).ToString("X" + offsetWidth)}  {value.PadRight(valueWidth, ' ')}";
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
        SelectionStatus = !HasSelection && buf.IsRead(_caret)
            ? new(title, detail, position, buf[_caret].ToString("X2"), buf[_caret].ToString(CultureInfo.InvariantCulture),
                Convert.ToString(buf[_caret], 2).PadLeft(8, '0'), null)
            : new(title, detail, position, null, null, null,
                HasSelection ? string.Format(Strings.Hex_Selected, Selection().to - Selection().from + 1) : unread);
    }

    // ================================================================ Input

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _pointerFocus = e.NavigationMethod == NavigationMethod.Pointer;
        RestartBlink();
        InvalidateCaret();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _pointerFocus = false;
        _blink.Stop();
        InvalidateCaret();
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

        // Touch: place the caret on release so the page can still be scrolled by dragging;
        // holding still selects (see BeginTouchSelection).
        if (e.Pointer.Type == PointerType.Touch)
        {
            (_touchDown, _touchHeld, _touchPointer, _touchPressPoint) = (true, false, e.Pointer, point.Position);
            return;
        }
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
        // A scroll takes the finger over: no long press any more.
        _pointerFocus = _dragging = _touchDown = _touchExtending = false;
    }

    protected override void OnHolding(HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && e.PointerType == PointerType.Touch && _touchDown)
        {
            e.Handled = true;
            BeginTouchSelection();
            return;
        }
        base.OnHolding(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_touchExtending && _buffer is not null && _lineY.Length > 0)
        {
            // Dragging on after a long press extends the selection instead of scrolling.
            e.PreventGestureRecognition();
            e.Handled = true;
            var touchHit = HitTest(e.GetPosition(this), _pane);
            if (touchHit.offset != _caret) MoveCaret(touchHit.offset, false, extend: true);
            return;
        }
        if (!_dragging || _buffer is null) return;
        var hit = HitTest(e.GetPosition(this));
        if (hit.offset != _caret || _anchor < 0) MoveCaret(hit.offset, false, extend: true);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pointerFocus = false;
        var held = _touchHeld;
        if (e.Pointer.Type == PointerType.Touch) _touchDown = _touchHeld = _touchExtending = false;
        if (held)
        {
            // The long press is over: the selection stays, with its bar.
            e.Handled = true;
            e.Pointer.Capture(null);
            _touchBarShown = true;
            UpdateTouchUi();
            return;
        }
        if (_touchSelect && e.Pointer.Type == PointerType.Touch && _lineY.Length > 0)
        {
            // A tap on the selection shows or hides its bar; elsewhere it places the caret as usual.
            var (from, to) = Selection();
            var tap = HitTest(e.GetPosition(this), _pane);
            if (tap.offset >= from && tap.offset <= to)
            {
                _touchBarShown = !_touchBarShown;
                UpdateTouchUi();
                return;
            }
        }
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

        // A list pasted from an on-screen keyboard's clipboard (or Win+V) comes as typed text:
        // paste it, so its lines stay keys of their own.
        if (_pane == Pane.Hex && e.Text.Contains('\n'))
        {
            PasteText(e.Text);
            return;
        }

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
        if (_buffer is not { ViewLength: > 0 } || _lineY.Length == 0 || e.Handled) return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var last = _buffer.ViewLength - 1;
        var line = LineOf(_caret);
        var col = ColumnInLine(_caret);
        var lastLine = _lineY.Length - 1;
        var editsBytes = _buffer is { VariableRowLength: true, IsReadOnly: false };
        e.Handled = true;

        switch (e.Key)
        {
            case Key.Left when _pane == Pane.Hex && _lowNibble && !shift:
                MoveCaret(_caret, false, false);
                break;
            case Key.Left: MoveCaret(_caret - 1, false, shift); break;
            case Key.Right: MoveCaret(_caret + 1, false, shift); break;
            case Key.Up: MoveCaret(line > 0 ? CellOfLine(line - 1, col) : _caret, _lowNibble, shift); break;
            case Key.Down: MoveCaret(line < lastLine ? CellOfLine(line + 1, col) : _caret, _lowNibble, shift); break;
            case Key.PageUp: MoveCaret(CellOfLine(Math.Max(0, line - PageLines), col), false, shift); break;
            case Key.PageDown: MoveCaret(CellOfLine(Math.Min(lastLine, line + PageLines), col), false, shift); break;
            case Key.Home when cmd: MoveCaret(0, false, shift); break;
            case Key.End when cmd: MoveCaret(last, false, shift); break;
            case Key.Home: MoveCaret(_lineStart[line], false, shift); break;
            case Key.End: MoveCaret(_lineStart[line + 1] - 1, false, shift); break;
            case Key.Back when editsBytes && (HasSelection || !_lowNibble): DeleteBackward(); break;
            case Key.Back: MoveCaret(_lowNibble ? _caret : _caret - 1, false, false); break;
            case Key.Insert when editsBytes && !cmd && !shift: InsertByte(); break;
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
        var row = buf.RowAt(_caret);
        if (row < buf.RowCount - 1) buf.InsertRows(row + 1, 1);
        GoTo(buf.RowStart(Math.Min(row + 1, buf.ViewRowCount - 1)));
    }

    /// <summary>
    /// Backspace in variable-length rows: deletes the selected bytes, or the byte before the
    /// caret in its row (at the start of a row it only moves back).
    /// </summary>
    private void DeleteBackward()
    {
        if (_buffer is not { VariableRowLength: true, IsReadOnly: false } buf) return;
        if (HasSelection)
        {
            var (from, to) = Selection();
            _anchor = -1;
            buf.DeleteBytes(from, to);
            GoTo(from);
            return;
        }
        if (buf.ColumnAt(_caret) == 0)
        {
            MoveCaret(_caret - 1, false, false);
            return;
        }
        var target = _caret - 1;
        buf.DeleteBytes(target, target);
        GoTo(target);
    }

    /// <summary>Insert in variable-length rows: a zero byte at the caret, to type over.</summary>
    private void InsertByte()
    {
        if (_buffer is not { VariableRowLength: true, IsReadOnly: false } buf) return;
        var at = HasSelection ? Selection().from : _caret;
        if (buf.InsertBytes(at, 1) > 0) GoTo(at);
    }

    /// <summary>Removes the rows the selection touches, or the caret's row (resizable buffers).</summary>
    private void RemoveRows()
    {
        if (_buffer is not { CanResize: true, RowCount: > 0 } buf) return;
        var (from, to) = HasSelection ? Selection() : (_caret, _caret);
        int first = buf.RowAt(from), last = Math.Min(buf.RowAt(to), buf.RowCount - 1);
        if (first > last) return;
        _anchor = -1;
        buf.RemoveRows(first, last - first + 1);
        GoTo(buf.RowStart(Math.Min(first, buf.ViewRowCount - 1)));
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
                var row = buf.RowAt(_caret);
                from = buf.RowStart(row);
                to = from + (buf.VariableRowLength ? buf.RowDataLength(row) : buf.RowLength) - 1;
                if (to < from) return;
            }

            string text;
            if (_pane == Pane.Ascii)
            {
                var sb = new StringBuilder();
                for (var o = from; o <= to; o++)
                {
                    // Variable-length rows: only bytes, not the cells after them.
                    if (buf.VariableRowLength && !buf.IsRead(o)) continue;
                    sb.Append(buf.IsRead(o) && buf[o] is >= 0x20 and < 0x7F ? (char)buf[o] : '.');
                }
                text = sb.ToString();
            }
            else if (buf.VariableRowLength)
            {
                // A key per line, so a paste gives back the same rows.
                text = string.Join(Environment.NewLine, Enumerable.Range(buf.RowAt(from), buf.RowAt(to) - buf.RowAt(from) + 1)
                    .Select(row =>
                    {
                        var start = buf.RowStart(row);
                        return string.Join(' ', Enumerable.Range(0, buf.RowDataLength(row))
                            .Where(c => start + c >= from && start + c <= to)
                            .Select(c => buf[start + c].ToString("X2")));
                    })
                    .Where(line => line.Length > 0));
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
            if (!string.IsNullOrEmpty(text)) PasteText(text);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    /// <summary>
    /// Overwrites from the caret (or selection start) with pasted hex / text; also takes a paste
    /// an on-screen keyboard (or Win+V) delivers as typed text.
    /// </summary>
    private void PasteText(string text)
    {
        if (_buffer is not { IsReadOnly: false } buf) return;
        var start = HasSelection ? Selection().from : _caret;

        // Variable-length rows take a key per line: several lines replace rows from the caret's on.
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        if (buf.VariableRowLength && _pane == Pane.Hex && lines.Length > 1)
        {
            var rows = new List<byte[]>();
            foreach (var line in lines)
            {
                if (!Hex.TryParse(line, out var row)) return;
                rows.Add(row);
            }
            var first = buf.RowAt(start);
            _anchor = -1;
            var count = buf.WriteRows(first, rows);
            if (count > 0) GoTo(buf.RowStart(Math.Min(first + count, buf.ViewRowCount - 1)));
            return;
        }

        byte[] bytes;
        if (_pane == Pane.Ascii) bytes = Encoding.ASCII.GetBytes(text.Replace("\r", "").Replace("\n", ""));
        else if (!Hex.TryParse(string.Join('\n', lines), out bytes)) return;

        if (buf.VariableRowLength && HasSelection)
        {
            // The pasted bytes take the selection's place, so the row can get shorter or longer.
            var (from, to) = Selection();
            _anchor = -1;
            var replaced = buf.ReplaceBytes(from, to, bytes);
            GoTo(Math.Min(from + replaced, buf.ViewLength - 1));
            return;
        }

        if (HasSelection) bytes = bytes.Take((int)(Selection().to - start + 1)).ToArray();
        var written = buf.Write(start, bytes);
        if (written > 0) GoTo(Math.Min(start + written, buf.ViewLength - 1));
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
        var insertByte = Item(Strings.Hex_InsertByte, "Insert", InsertByte);
        var deleteBytes = Item(Strings.Hex_DeleteBytes, "Back", DeleteBackward);
        var bytesSeparator = new Separator();
        var rowsSeparator = new Separator();
        var paste = Item(Strings.Hex_Paste, "Ctrl+V", Paste);
        var historySeparator = new Separator();
        var menu = new ContextMenu
        {
            Items =
            {
                Item(Strings.Hex_Copy, "Ctrl+C", Copy),
                paste,
                bytesSeparator,
                insertByte,
                deleteBytes,
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
            // Bytes are inserted and deleted only where rows can change length (mixed-length keys).
            bytesSeparator.IsVisible = insertByte.IsVisible = deleteBytes.IsVisible =
                _buffer is { VariableRowLength: true, IsReadOnly: false };
            // A read-only view copies and selects only.
            paste.IsVisible = historySeparator.IsVisible = undo.IsVisible = redo.IsVisible = _buffer?.IsReadOnly != true;
        };
        return menu;
    }

    // ================================================================ Touch selection

    /// <summary>
    /// A long press: selects the byte under the finger, with handles and (once lifted) the bar.
    /// Leaves focus alone, so the on-screen keyboard doesn't pop up just to copy.
    /// </summary>
    private void BeginTouchSelection()
    {
        if (_touchHeld || _buffer is not { ViewLength: > 0 } || _lineY.Length == 0) return;
        _touchHeld = _touchExtending = true;
        PlaceCaret(HitTest(_touchPressPoint), extend: false);
        _anchor = _caret;
        _touchSelect = true;
        _touchBarShown = false;
        // Moves of this finger now extend the selection (see OnPointerMoved), not scroll.
        _touchPointer?.Capture(this);
        UpdateStatus();
        InvalidateCaret();
        UpdateTouchUi();
    }

    private void EndTouchSelection()
    {
        if (!_touchSelect) return;
        _touchSelect = false;
        _anchor = -1;
        UpdateStatus();
        InvalidateCaret();
        UpdateTouchUi();
    }

    /// <summary>Shows or hides the handles and the bar, and places them on the next layout.</summary>
    private void UpdateTouchUi()
    {
        var show = _touchSelect && HasSelection && _lineY.Length > 0;
        _touchLayer.Start.IsVisible = _touchLayer.End.IsVisible = show;
        _touchLayer.Bar.IsVisible = show && _touchBarShown;
        _touchLayer.Paste.IsVisible = _buffer?.IsReadOnly == false;
        if (!show) return;
        _touchLayer.Start.InvalidateVisual();
        _touchLayer.End.InvalidateVisual();
        _touchLayer.InvalidateArrange();
    }

    /// <summary>The point a handle's tip marks: the bottom corner of the selection's first or last cell.</summary>
    private Point HandleTip(bool start)
    {
        var (from, to) = Selection();
        return start ? CellRect(from, _pane).BottomLeft : CellRect(to, _pane).BottomRight;
    }

    private void ArrangeTouchUi(Size size)
    {
        if (!_touchLayer.Start.IsVisible || _lineY.Length == 0 || _buffer is not { ViewLength: > 0 }) return;
        var start = HandleTip(true);
        var end = HandleTip(false);
        _touchLayer.Start.Arrange(new Rect(start - _touchLayer.Start.Tip, _touchLayer.Start.DesiredSize));
        _touchLayer.End.Arrange(new Rect(end - _touchLayer.End.Tip, _touchLayer.End.DesiredSize));
        if (!_touchLayer.Bar.IsVisible) return;

        // Above the selection, else under its end handle, else at the top of the screen;
        // within what is on screen of the editor.
        const double gap = 8;
        var bar = _touchLayer.Bar.DesiredSize;
        var view = _viewport.Width > 0 ? _viewport.Intersect(new Rect(size)) : new Rect(size);
        var top = start.Y - _lineH - gap - bar.Height;
        var below = end.Y + _touchLayer.End.DesiredSize.Height + gap;
        var y = top >= view.Top ? top
            : below + bar.Height <= view.Bottom ? below
            : view.Top + gap;
        var sameLine = LineOf(Selection().from) == LineOf(Selection().to);
        var center = sameLine ? (start.X + end.X) / 2 : view.Center.X;
        var x = Math.Max(view.Left, Math.Min(center - bar.Width / 2, view.Right - bar.Width));
        _touchLayer.Bar.Arrange(new Rect(new Point(x, y), bar));
    }

    private void BeginHandleDrag(bool start, Point p)
    {
        var (from, to) = Selection();
        // The dragged end becomes the caret, the other end the anchor.
        _anchor = start ? to : from;
        _caret = start ? from : to;
        _handleIsStart = start;
        _handleGrab = p - HandleTip(start);
        _touchBarShown = false;
        UpdateTouchUi();
    }

    private void DragHandle(Point p)
    {
        if (_buffer is null || _lineY.Length == 0) return;
        // The cell just inside the tip: a start handle's tip is at a cell's left, an end handle's at its right.
        var tip = p - _handleGrab;
        var inside = new Point(tip.X + (_handleIsStart ? 1 : -1) * _digitW / 2, tip.Y - _lineH / 2);
        var hit = HitTest(inside, _pane);
        if (hit.offset != _caret) MoveCaret(hit.offset, false, extend: true);
    }

    private void EndHandleDrag()
    {
        if (!_touchSelect || _touchBarShown) return;
        _touchBarShown = true;
        UpdateTouchUi();
    }

    /// <summary>The handles and the command bar of a touch selection, over the editor's text.</summary>
    private sealed class TouchSelectionLayer : Panel
    {
        private readonly HexEditor _owner;

        public TouchSelectionLayer(HexEditor owner)
        {
            _owner = owner;
            Start = new SelectionHandle(owner, start: true) { IsVisible = false };
            End = new SelectionHandle(owner, start: false) { IsVisible = false };

            Button Command(string text, Action action)
            {
                // Not focusable: tapping one must not take focus (and the keyboard) from the editor.
                var button = new Button { Content = text, Focusable = false };
                button.Classes.Add("theme-subtle");
                button.Click += (_, _) => action();
                return button;
            }

            Paste = Command(Strings.Hex_Paste, () =>
            {
                // The paste replaces the selection once the clipboard is read, and ends it.
                _owner._touchBarShown = false;
                _owner.UpdateTouchUi();
                _owner.Paste();
            });
            Bar = new Border
            {
                IsVisible = false,
                Child = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Children =
                    {
                        Command(Strings.Hex_Copy, () =>
                        {
                            _owner.Copy();
                            _owner.EndTouchSelection();
                        }),
                        Paste,
                        Command(Strings.Hex_SelectAll, () =>
                        {
                            _owner.SelectAll();
                            _owner._touchBarShown = true;
                            _owner.UpdateTouchUi();
                        }),
                    },
                },
            };
            Bar.Classes.Add("hextouchbar");
            Children.Add(Start);
            Children.Add(End);
            Children.Add(Bar);
        }

        public SelectionHandle Start { get; }
        public SelectionHandle End { get; }
        public Border Bar { get; }
        public Button Paste { get; }

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

    /// <summary>
    /// A drop-shaped handle under one end of a touch selection, pointing at it with its square
    /// corner; dragging it moves that end. Its touch area is larger than the drop.
    /// </summary>
    private sealed class SelectionHandle : Control
    {
        private const double Radius = 11, BoxWidth = 44, BoxHeight = 40;
        private readonly HexEditor _owner;
        private readonly bool _start;

        public SelectionHandle(HexEditor owner, bool start)
        {
            _owner = owner;
            _start = start;
            // The tip at the drop's square corner: right of a start handle's drop, left of an end one's.
            Tip = new Vector(start ? BoxWidth - 14 : 14, 0);
        }

        /// <summary>Where on the handle its tip is.</summary>
        public Vector Tip { get; }

        protected override Size MeasureOverride(Size availableSize) => new(BoxWidth, BoxHeight);

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (_owner.CaretBrush is not { } brush) return;
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
            _owner.BeginHandleDrag(_start, e.GetPosition(_owner));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured != this) return;
            e.Handled = true;
            e.PreventGestureRecognition();
            _owner.DragHandle(e.GetPosition(_owner));
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
            _owner.EndHandleDrag();
        }
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
