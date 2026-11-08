using System;
using System.Collections.Generic;
using System.Linq;

namespace ChameLab.Controls;

/// <summary>What a byte means, so the editor can color it.</summary>
public enum HexCellKind
{
    Data,
    /// <summary>UID / manufacturer bytes (MIFARE block 0, Ultralight pages 0–2).</summary>
    Manufacturer,
    KeyA,
    AccessBits,
    KeyB,
}

/// <summary>
/// Memory edited by <see cref="HexEditor"/>: a list of equally long rows (blocks / pages / keys)
/// where an empty row means "not read" (optionally shown with a <see cref="Template"/>). Edits overwrite in place and can be undone. Card memory
/// has a fixed size; with <see cref="CanResize"/> rows can also be inserted and removed, and
/// pasting past the end adds rows. With <see cref="VariableRowLength"/> each row holds up to
/// <see cref="RowLength"/> bytes and grows or shrinks as it is edited (e.g. keys of mixed lengths).
/// </summary>
/// <remarks>
/// Offsets count the cells the editor shows. A variable-length row shorter than the maximum
/// ends with one more, empty cell: typing there appends a byte. An empty row is that cell alone.
/// </remarks>
public sealed class HexBuffer
{
    private readonly List<byte[]> _rows;
    // What each row held when the buffer was created; null for a row added since.
    private readonly List<byte[]?> _original;
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private long _typingOffset = -1;
    private readonly HexBuffer? _parent;
    private readonly int _firstRow, _viewRows;

    private HexBuffer(HexBuffer parent, int firstRow, int rowCount)
        : this(new List<byte[]>(), parent.RowLength)
    {
        _parent = parent;
        _firstRow = firstRow;
        _viewRows = rowCount;
        RowLabel = row => parent.RowLabel(firstRow + row);
        Classify = (row, col) => parent.Classify(firstRow + row, col);
        StartsGroup = row => parent.StartsGroup(firstRow + row);
        GroupHeader = row => parent.GroupHeader(firstRow + row);
        Describe = (row, col) => parent.Describe(firstRow + row, col);
        Template = row => parent.Template(firstRow + row);
        TemplateKnown = (row, col) => parent.TemplateKnown(firstRow + row, col);
        RowsPerLine = parent.RowsPerLine;
    }

    /// <summary>A fixed range sharing edits, original bytes and undo history with this buffer.</summary>
    public HexBuffer Slice(int firstRow, int rowCount)
    {
        if (VariableRowLength) throw new NotSupportedException("Variable-length rows can't be sliced.");
        if (firstRow < 0 || rowCount < 0 || firstRow > RowCount - rowCount)
            throw new ArgumentOutOfRangeException(nameof(firstRow));
        return new HexBuffer(this, firstRow, rowCount);
    }

    /// <summary>Absolute byte position of the first row of this view.</summary>
    public long ByteOffset => (_parent?.ByteOffset ?? 0) + (long)_firstRow * RowLength;

    /// <param name="rows">Rows edited in place; an empty array marks an unread row.</param>
    /// <param name="rowLength">
    /// Bytes per row (16 for MIFARE Classic, 4 for Ultralight, 6 for keys); the most a row can
    /// hold with <see cref="VariableRowLength"/>.
    /// </param>
    public HexBuffer(List<byte[]> rows, int rowLength)
    {
        if (rowLength <= 0) throw new ArgumentOutOfRangeException(nameof(rowLength));
        _rows = rows;
        RowLength = rowLength;
        _original = rows.Select(r => (byte[]?)r.Clone()).ToList();
    }

    public int RowCount => _parent is null ? _rows.Count : _viewRows;
    public int RowLength { get; }
    public long Length => RowStart(RowCount);

    /// <summary>
    /// Rows hold from none to <see cref="RowLength"/> bytes: typing after a row's last byte adds
    /// one, and bytes can be inserted and deleted. Not for slices or templates.
    /// </summary>
    public bool VariableRowLength { get; init; }

    /// <summary>Rows can be inserted and removed, and pasting past the end adds rows.</summary>
    public bool CanResize { get; init; }

    /// <summary>The editor only shows and copies the bytes: typing, pasting and undo are off.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Rows the editor shows: a resizable buffer ends with one more, empty row that typing or
    /// pasting into turns into a real one (and a new empty row appears under it).
    /// </summary>
    public int ViewRowCount => RowCount + (CanResize ? 1 : 0);
    public long ViewLength => RowStart(ViewRowCount);

    // Variable-length rows: where each shown row starts (one more entry for the end), and how
    // many bytes come before it; rebuilt after an edit.
    private long[]? _starts;
    private long[]? _dataStarts;

    private long[] Starts
    {
        get
        {
            if (_starts is not null) return _starts;
            var count = ViewRowCount;
            var starts = new long[count + 1];
            var data = new long[count + 1];
            for (var row = 0; row < count; row++)
            {
                starts[row + 1] = starts[row] + RowViewLength(row);
                data[row + 1] = data[row] + RowDataLength(row);
            }
            _dataStarts = data;
            return _starts = starts;
        }
    }

    /// <summary>Cells a row takes in the editor: its bytes, and one more cell while it has room.</summary>
    public int RowViewLength(int row)
    {
        if (!VariableRowLength) return RowLength;
        var length = row < RowCount ? _rows[row].Length : 0;
        return length < RowLength ? length + 1 : length;
    }

    /// <summary>Bytes a row holds: none while unread, fewer than <see cref="RowLength"/> in a short variable-length row.</summary>
    public int RowDataLength(int row) => !IsRead(row) ? 0 : VariableRowLength ? _rows[row].Length : RowLength;

    /// <summary>Offset of a row's first cell; <see cref="ViewRowCount"/> gives the end.</summary>
    public long RowStart(int row) => VariableRowLength ? Starts[row] : (long)row * RowLength;

    /// <summary>The row a cell belongs to.</summary>
    public int RowAt(long offset)
    {
        if (!VariableRowLength) return (int)(offset / RowLength);
        var starts = Starts;
        // Every shown row has a cell (an empty one shows the cell for its first byte), so starts only increase.
        var i = Array.BinarySearch(starts, 0, starts.Length - 1, offset);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, Math.Max(0, starts.Length - 2));
    }

    /// <summary>Position of a cell in its row.</summary>
    public int ColumnAt(long offset) => VariableRowLength ? (int)(offset - RowStart(RowAt(offset))) : (int)(offset % RowLength);

    /// <summary>Position of a cell among all the bytes (what the status line shows).</summary>
    public long DataOffset(long offset)
    {
        if (!VariableRowLength) return ByteOffset + offset;
        var row = RowAt(offset);
        _ = Starts;
        return _dataStarts![row] + ColumnAt(offset);
    }

    /// <summary>
    /// Short rows the editor may put side by side on one line when there is room (e.g. four
    /// 4-byte Ultralight pages as one 16-byte line), labeled by the first. Group separators
    /// and headings are only drawn at the start of a line, so keep 1 for grouped rows.
    /// </summary>
    public int RowsPerLine { get; init; } = 1;

    /// <summary>Row label shown in the gutter (defaults to the row number).</summary>
    public Func<int, string> RowLabel { get; init; } = row => row.ToString("D3");

    /// <summary>Color class of a byte.</summary>
    public Func<int, int, HexCellKind> Classify { get; init; } = (_, _) => HexCellKind.Data;

    /// <summary>A separator is drawn above rows for which this returns true (sector starts).</summary>
    public Func<int, bool> StartsGroup { get; init; } = _ => false;

    /// <summary>
    /// Heading drawn on its own line above a row that starts a group (e.g. "Sector 3" with
    /// "blocks 12–15" as a dimmer detail); null for rows without one.
    /// </summary>
    public Func<int, (string Title, string? Detail)?> GroupHeader { get; init; } = _ => null;

    /// <summary>Short description of a position for the status line.</summary>
    public Func<int, int, string?> Describe { get; init; } = (_, _) => null;

    /// <summary>
    /// What an unread row holds on a blank card (e.g. default keys in a MIFARE trailer); null when
    /// unknown. Shown dimmed in place of the unread row and the start of it once edited, so
    /// defaults stand in for memory that was never read without passing for read data.
    /// </summary>
    public Func<int, byte[]?> Template { get; init; } = _ => null;

    /// <summary>
    /// The template byte is known rather than a default (e.g. the UID, which the scan tells
    /// although it doesn't read block 0): drawn like data, while the row stays unread.
    /// </summary>
    public Func<int, int, bool> TemplateKnown { get; init; } = (_, _) => false;

    /// <summary>The byte is unread but known from the <see cref="Template"/>.</summary>
    public bool IsTemplateKnown(long offset) =>
        TemplateAt(offset) is not null && TemplateKnown(RowAt(offset), ColumnAt(offset));

    /// <summary>The <see cref="Template"/> now gives other bytes: tells listeners (the editor redraws).</summary>
    public void TemplateChanged()
    {
        if (_parent is { } parent) parent.TemplateChanged();
        else _changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The template byte at an unread position; null when the row is read or has no template.</summary>
    public byte? TemplateAt(long offset)
    {
        if (VariableRowLength) return null;
        var row = (int)(offset / RowLength);
        if (row < 0 || row >= RowCount || IsRead(row)) return null;
        return Template(row) is { } t && t.Length == RowLength ? t[offset % RowLength] : null;
    }

    /// <summary>Raised after every edit, undo and redo.</summary>
    private event EventHandler? _changed;
    public event EventHandler? Changed
    {
        add { if (_parent is { } parent) parent.Changed += value; else _changed += value; }
        remove { if (_parent is { } parent) parent.Changed -= value; else _changed -= value; }
    }

    public IReadOnlyList<byte[]> Rows => _parent is null ? _rows : _parent.Rows.Skip(_firstRow).Take(_viewRows).ToArray();

    /// <summary>The row holds data (false for an unread row and for the empty row after the last).</summary>
    public bool IsRead(int row) => row >= 0 && row < RowCount
        && (_parent is { } parent ? parent.IsRead(_firstRow + row) : _rows[row].Length > 0);

    /// <summary>The cell holds a byte (false in an unread row and for the cell after a short row's last byte).</summary>
    public bool IsRead(long offset)
    {
        if (!VariableRowLength) return IsRead((int)(offset / RowLength));
        var row = RowAt(offset);
        return IsRead(row) && ColumnAt(offset) < _rows[row].Length;
    }

    public byte this[long offset] => _parent is { } parent
        ? parent[(long)_firstRow * RowLength + offset]
        : !IsRead(offset) ? (byte)0
        : VariableRowLength ? _rows[RowAt(offset)][ColumnAt(offset)]
        : _rows[(int)(offset / RowLength)][offset % RowLength];

    /// <summary>The byte differs from what the buffer was created with.</summary>
    public bool IsChanged(long offset)
    {
        if (_parent is { } parent) return parent.IsChanged((long)_firstRow * RowLength + offset);
        int row = RowAt(offset), col = ColumnAt(offset);
        if (row >= _rows.Count) return false;
        var now = _rows[row];
        var was = _original[row];
        if (VariableRowLength && col >= now.Length) return false;
        if (was is null) return now.Length != 0;
        if (now.Length == 0) return was.Length != 0;
        return was.Length <= col || was[col] != now[col];
    }

    public bool CanUndo => _parent?.CanUndo ?? (_undo.Count > 0);
    public bool CanRedo => _parent?.CanRedo ?? (_redo.Count > 0);

    /// <summary>
    /// Overwrites bytes starting at <paramref name="offset"/>; unread rows become read (zero-filled).
    /// Bytes past the end add rows when the buffer <see cref="CanResize"/>, otherwise they are dropped.
    /// With <see cref="VariableRowLength"/> the bytes stay in the offset's row: they overwrite and
    /// then lengthen it, and what doesn't fit is dropped.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    public int Write(long offset, ReadOnlySpan<byte> bytes) => Write(offset, bytes, merge: false);

    /// <summary>Sets one nibble (half-byte) of a byte; <paramref name="high"/> picks the upper one.</summary>
    public void WriteNibble(long offset, bool high, int value)
    {
        var old = IsRead(offset) ? this[offset] : TemplateAt(offset) ?? 0;
        var b = high ? (byte)((value << 4) | (old & 0x0F)) : (byte)((old & 0xF0) | value);
        // Typing both nibbles of one byte is a single undo step.
        var merge = !high && _typingOffset == offset;
        Write(offset, [b], merge);
        _typingOffset = high ? offset : -1;
    }

    /// <summary>Marks a fixed range as unread, preserving its size and undo history.</summary>
    public void ClearRows(int first, int count)
    {
        if (first < 0 || first >= RowCount || count <= 0) return;
        count = Math.Min(count, RowCount - first);
        if (_parent is { } parent)
        {
            parent.ClearRows(_firstRow + first, count);
            return;
        }
        var empty = Enumerable.Range(0, count).Select(_ => Array.Empty<byte>()).ToArray();
        Apply(new Edit(first, Snapshot(first, count), Originals(first, count), empty, Originals(first, count)));
    }

    /// <summary>Inserts <paramref name="count"/> unread (empty) rows before row <paramref name="at"/>.</summary>
    public void InsertRows(int at, int count)
    {
        if (!CanResize || count <= 0) return;
        at = Math.Clamp(at, 0, RowCount);
        var rows = new byte[count][];
        for (var i = 0; i < count; i++) rows[i] = Array.Empty<byte>();
        Apply(new Edit(at, Array.Empty<byte[]>(), Array.Empty<byte[]?>(), rows, new byte[]?[count]));
    }

    /// <summary>Removes <paramref name="count"/> rows starting at row <paramref name="first"/>.</summary>
    public void RemoveRows(int first, int count)
    {
        if (!CanResize || first < 0 || first >= RowCount) return;
        count = Math.Min(count, RowCount - first);
        if (count <= 0) return;
        Apply(new Edit(first, Snapshot(first, count), Originals(first, count),
            Array.Empty<byte[]>(), Array.Empty<byte[]?>()));
    }

    public bool Undo() => _parent?.Undo() ?? Step(_undo, _redo, undo: true);
    public bool Redo() => _parent?.Redo() ?? Step(_redo, _undo, undo: false);

    /// <summary>
    /// Puts <paramref name="rows"/> in place of the rows from <paramref name="firstRow"/> (past
    /// the end they are added when the buffer <see cref="CanResize"/>, else dropped); rows are cut
    /// to <see cref="RowLength"/>, and fixed-length ones zero-padded to it. One undo step.
    /// </summary>
    /// <returns>Number of rows written.</returns>
    public int WriteRows(int firstRow, IReadOnlyList<byte[]> rows)
    {
        _typingOffset = -1;
        if (firstRow < 0 || firstRow >= ViewRowCount || rows.Count == 0) return 0;
        if (_parent is { } parent)
            return parent.WriteRows(_firstRow + firstRow, rows.Take(RowCount - firstRow).ToArray());
        var count = CanResize ? rows.Count : Math.Min(rows.Count, RowCount - firstRow);
        var fitted = rows.Take(count).Select(r =>
        {
            var row = new byte[VariableRowLength ? Math.Min(r.Length, RowLength) : RowLength];
            r.AsSpan(0, Math.Min(r.Length, row.Length)).CopyTo(row);
            return row;
        }).ToArray();
        ReplaceRows(firstRow, fitted, merge: false);
        return count;
    }

    /// <summary>
    /// Inserts <paramref name="count"/> zero bytes at <paramref name="offset"/> into a
    /// variable-length row, as many as it has room for.
    /// </summary>
    /// <returns>Number of bytes inserted.</returns>
    public int InsertBytes(long offset, int count) =>
        count > 0 ? ReplaceBytes(offset, offset - 1, new byte[count]) : 0;

    /// <summary>
    /// Puts <paramref name="bytes"/> (as many as the row has room for) in place of the bytes from
    /// <paramref name="from"/> to <paramref name="to"/> of a variable-length row, so the row can
    /// get shorter or longer; a range ending before it starts inserts. Only <paramref name="from"/>'s
    /// row changes. One undo step.
    /// </summary>
    /// <returns>Number of bytes put in.</returns>
    public int ReplaceBytes(long from, long to, ReadOnlySpan<byte> bytes)
    {
        _typingOffset = -1;
        if (!VariableRowLength || from < 0 || from >= ViewLength) return 0;
        var row = RowAt(from);
        var col = ColumnAt(from);
        var old = row < RowCount ? _rows[row] : Array.Empty<byte>();
        var end = (int)Math.Clamp(to - RowStart(row) + 1, col, old.Length);
        var count = Math.Clamp(bytes.Length, 0, RowLength - (old.Length - (end - col)));
        if (count == 0 && end == col) return 0;
        byte[] now = [.. old.AsSpan(0, col), .. bytes[..count], .. old.AsSpan(end)];
        ReplaceRows(row, [now], merge: false);
        return count;
    }

    /// <summary>
    /// Deletes the bytes of variable-length rows from <paramref name="from"/> to
    /// <paramref name="to"/> (inclusive); a row left without bytes stays as an empty row. One undo step.
    /// </summary>
    /// <returns>Number of bytes deleted.</returns>
    public int DeleteBytes(long from, long to)
    {
        _typingOffset = -1;
        if (!VariableRowLength || RowCount == 0) return 0;
        from = Math.Max(0, from);
        to = Math.Min(to, ViewLength - 1);
        if (from > to) return 0;
        int first = RowAt(from), last = Math.Min(RowAt(to), RowCount - 1);
        if (first > last) return 0;
        var deleted = 0;
        var rows = new byte[last - first + 1][];
        for (var row = first; row <= last; row++)
        {
            var old = _rows[row];
            var start = RowStart(row);
            var a = (int)Math.Max(0, from - start);
            var b = (int)Math.Min(old.Length - 1, to - start);
            if (a > b) { rows[row - first] = old; continue; }
            rows[row - first] = [.. old.AsSpan(0, a), .. old.AsSpan(b + 1)];
            deleted += b - a + 1;
        }
        if (deleted > 0) ReplaceRows(first, rows, merge: false);
        return deleted;
    }

    private int Write(long offset, ReadOnlySpan<byte> bytes, bool merge)
    {
        _typingOffset = -1;
        // A resizable buffer also takes writes into the empty row after the last one.
        if (offset < 0 || bytes.IsEmpty || offset >= ViewLength) return 0;
        if (VariableRowLength)
        {
            var row = RowAt(offset);
            var col = ColumnAt(offset);
            var old = row < RowCount ? _rows[row] : Array.Empty<byte>();
            var written = Math.Min(bytes.Length, RowLength - col);
            if (written <= 0) return 0;
            var now = new byte[Math.Max(old.Length, col + written)];
            old.CopyTo(now, 0);
            bytes[..written].CopyTo(now.AsSpan(col));
            ReplaceRows(row, [now], merge);
            return written;
        }
        if (_parent is { } parent)
            return parent.Write((long)_firstRow * RowLength + offset,
                bytes[..(int)Math.Min(bytes.Length, Length - offset)], merge);
        var count = CanResize ? bytes.Length : (int)Math.Min(bytes.Length, Length - offset);
        int first = (int)(offset / RowLength), last = (int)((offset + count - 1) / RowLength);
        // Rows that exist now, then the ones a paste past the end adds.
        var existing = Math.Max(0, Math.Min(last, RowCount - 1) - first + 1);
        var before = Snapshot(first, existing);
        var beforeOrig = Originals(first, existing);
        while (RowCount <= last)
        {
            _rows.Add(Array.Empty<byte>());
            _original.Add(null);
        }

        for (var i = 0; i < count; i++)
        {
            var o = offset + i;
            var row = (int)(o / RowLength);
            if (_rows[row].Length == 0)
                _rows[row] = Template(row) is { } t && t.Length == RowLength ? (byte[])t.Clone() : new byte[RowLength];
            _rows[row][o % RowLength] = bytes[i];
        }

        Commit(new Edit(first, before, beforeOrig, Snapshot(first, last - first + 1), Originals(first, last - first + 1)), merge);
        return count;
    }

    /// <summary>Puts <paramref name="rows"/> in place of the rows from <paramref name="first"/>, adding rows past the end.</summary>
    private void ReplaceRows(int first, byte[][] rows, bool merge)
    {
        var existing = Math.Clamp(RowCount - first, 0, rows.Length);
        var before = Snapshot(first, existing);
        var beforeOrig = Originals(first, existing);
        while (RowCount < first + rows.Length)
        {
            _rows.Add(Array.Empty<byte>());
            _original.Add(null);
        }
        for (var i = 0; i < rows.Length; i++) _rows[first + i] = rows[i];
        Commit(new Edit(first, before, beforeOrig, Snapshot(first, rows.Length), Originals(first, rows.Length)), merge);
    }

    /// <summary>Records an edit already made for undo (merged into the last one when typing a byte's second nibble).</summary>
    private void Commit(Edit edit, bool merge)
    {
        if (merge && _undo.TryPeek(out var top) && top.FirstRow == edit.FirstRow && top.After.Length == edit.After.Length)
        {
            _undo.Pop();
            edit = edit with { Before = top.Before, BeforeOriginal = top.BeforeOriginal };
        }
        _undo.Push(edit);
        _redo.Clear();
        _starts = null;
        _changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes a structural edit (rows inserted or removed) and records it for undo.</summary>
    private void Apply(Edit edit)
    {
        _typingOffset = -1;
        Replace(edit.FirstRow, edit.Before.Length, edit.After, edit.AfterOriginal);
        Commit(edit, merge: false);
    }

    private bool Step(Stack<Edit> from, Stack<Edit> to, bool undo)
    {
        _typingOffset = -1;
        if (from.Count == 0) return false;
        var edit = from.Pop();
        if (undo) Replace(edit.FirstRow, edit.After.Length, edit.Before, edit.BeforeOriginal);
        else Replace(edit.FirstRow, edit.Before.Length, edit.After, edit.AfterOriginal);
        to.Push(edit);
        _changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Puts <paramref name="rows"/> in place of the <paramref name="count"/> rows at <paramref name="first"/>.</summary>
    private void Replace(int first, int count, byte[][] rows, byte[]?[] originals)
    {
        _rows.RemoveRange(first, count);
        _rows.InsertRange(first, rows.Select(r => (byte[])r.Clone()));
        _original.RemoveRange(first, count);
        _original.InsertRange(first, originals);
        _starts = null;
    }

    private byte[][] Snapshot(int first, int count)
    {
        var s = new byte[count][];
        for (var i = 0; i < count; i++) s[i] = (byte[])_rows[first + i].Clone();
        return s;
    }

    private byte[]?[] Originals(int first, int count) => _original.GetRange(first, count).ToArray();

    /// <summary>
    /// Rows <see cref="FirstRow"/>.. held <see cref="Before"/> and now hold <see cref="After"/>
    /// (as many for an overwrite, fewer or more when rows were removed or added).
    /// </summary>
    private sealed record Edit(int FirstRow, byte[][] Before, byte[]?[] BeforeOriginal, byte[][] After, byte[]?[] AfterOriginal);
}
