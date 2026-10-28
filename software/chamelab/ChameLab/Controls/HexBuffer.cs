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
/// where an empty row means "not read". Edits overwrite in place and can be undone. Card memory
/// has a fixed size; with <see cref="CanResize"/> rows can also be inserted and removed, and
/// pasting past the end adds rows.
/// </summary>
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
    }

    /// <summary>A fixed range sharing edits, original bytes and undo history with this buffer.</summary>
    public HexBuffer Slice(int firstRow, int rowCount)
    {
        if (firstRow < 0 || rowCount < 0 || firstRow > RowCount - rowCount)
            throw new ArgumentOutOfRangeException(nameof(firstRow));
        return new HexBuffer(this, firstRow, rowCount);
    }

    /// <summary>Absolute byte position of the first row of this view.</summary>
    public long ByteOffset => (_parent?.ByteOffset ?? 0) + (long)_firstRow * RowLength;

    /// <param name="rows">Rows edited in place; an empty array marks an unread row.</param>
    /// <param name="rowLength">Bytes per row (16 for MIFARE Classic, 4 for Ultralight, 6 for keys).</param>
    public HexBuffer(List<byte[]> rows, int rowLength)
    {
        if (rowLength <= 0) throw new ArgumentOutOfRangeException(nameof(rowLength));
        _rows = rows;
        RowLength = rowLength;
        _original = rows.Select(r => (byte[]?)r.Clone()).ToList();
    }

    public int RowCount => _parent is null ? _rows.Count : _viewRows;
    public int RowLength { get; }
    public int Length => RowCount * RowLength;

    /// <summary>Rows can be inserted and removed, and pasting past the end adds rows.</summary>
    public bool CanResize { get; init; }

    /// <summary>
    /// Rows the editor shows: a resizable buffer ends with one more, empty row that typing or
    /// pasting into turns into a real one (and a new empty row appears under it).
    /// </summary>
    public int ViewRowCount => RowCount + (CanResize ? 1 : 0);
    public long ViewLength => (long)ViewRowCount * RowLength;

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

    public bool IsRead(long offset) => IsRead((int)(offset / RowLength));

    public byte this[long offset] => _parent is { } parent
        ? parent[(long)_firstRow * RowLength + offset]
        : IsRead(offset) ? _rows[(int)(offset / RowLength)][offset % RowLength] : (byte)0;

    /// <summary>The byte differs from what the buffer was created with.</summary>
    public bool IsChanged(long offset)
    {
        if (_parent is { } parent) return parent.IsChanged((long)_firstRow * RowLength + offset);
        int row = (int)(offset / RowLength), col = (int)(offset % RowLength);
        if (row >= _rows.Count) return false;
        var now = _rows[row];
        var was = _original[row];
        if (was is null) return now.Length != 0;
        if (now.Length == 0) return was.Length != 0;
        return was.Length == 0 || was[col] != now[col];
    }

    public bool CanUndo => _parent?.CanUndo ?? (_undo.Count > 0);
    public bool CanRedo => _parent?.CanRedo ?? (_redo.Count > 0);

    /// <summary>
    /// Overwrites bytes starting at <paramref name="offset"/>; unread rows become read (zero-filled).
    /// Bytes past the end add rows when the buffer <see cref="CanResize"/>, otherwise they are dropped.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    public int Write(long offset, ReadOnlySpan<byte> bytes) => Write(offset, bytes, merge: false);

    /// <summary>Sets one nibble (half-byte) of a byte; <paramref name="high"/> picks the upper one.</summary>
    public void WriteNibble(long offset, bool high, int value)
    {
        var old = IsRead(offset) ? this[offset] : (byte)0;
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

    private int Write(long offset, ReadOnlySpan<byte> bytes, bool merge)
    {
        _typingOffset = -1;
        // A resizable buffer also takes writes into the empty row after the last one.
        if (offset < 0 || bytes.IsEmpty || offset >= ViewLength) return 0;
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
            if (_rows[row].Length == 0) _rows[row] = new byte[RowLength];
            _rows[row][o % RowLength] = bytes[i];
        }

        var edit = new Edit(first, before, beforeOrig, Snapshot(first, last - first + 1), Originals(first, last - first + 1));
        if (merge && _undo.TryPeek(out var top) && top.FirstRow == first && top.After.Length == edit.After.Length)
        {
            _undo.Pop();
            edit = edit with { Before = top.Before, BeforeOriginal = top.BeforeOriginal };
        }
        _undo.Push(edit);
        _redo.Clear();
        _changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    /// <summary>Makes a structural edit (rows inserted or removed) and records it for undo.</summary>
    private void Apply(Edit edit)
    {
        _typingOffset = -1;
        Replace(edit.FirstRow, edit.Before.Length, edit.After, edit.AfterOriginal);
        _undo.Push(edit);
        _redo.Clear();
        _changed?.Invoke(this, EventArgs.Empty);
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
