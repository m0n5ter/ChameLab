using System;
using System.Collections.Generic;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class HexBufferVariableRowTests
{
    private static HexBuffer Keys(List<byte[]> rows) => new(rows, 16) { VariableRowLength = true, CanResize = true };

    [Fact]
    public void RowsTakeTheirOwnLengthPlusACellToGrow()
    {
        var rows = new List<byte[]> { new byte[4], new byte[6], new byte[16] };
        var buffer = Keys(rows);
        // 4 + 1, 6 + 1, 16 (full), and the empty row at the end: one cell.
        Assert.Equal(new[] { 5, 7, 16, 1 }, new[] { buffer.RowViewLength(0), buffer.RowViewLength(1), buffer.RowViewLength(2), buffer.RowViewLength(3) });
        Assert.Equal(29, buffer.ViewLength);
        Assert.Equal(5, buffer.RowStart(1));
        Assert.Equal(12, buffer.RowStart(2));
        Assert.Equal(1, buffer.RowAt(11));
        Assert.Equal(6, buffer.ColumnAt(11));
        Assert.Equal(3, buffer.RowAt(28));
        Assert.True(buffer.IsRead(3L));
        Assert.False(buffer.IsRead(4L));
        // The status line counts bytes, not cells: row 2 starts after 4 + 6 bytes.
        Assert.Equal(10, buffer.DataOffset(12));
    }

    [Fact]
    public void TypingAfterTheLastByteLengthensTheRowUpToTheMaximum()
    {
        var rows = new List<byte[]> { new byte[] { 1, 2, 3, 4 } };
        var buffer = Keys(rows);
        buffer.WriteNibble(4, high: true, 0xA);
        buffer.WriteNibble(4, high: false, 0xB);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0xAB }, rows[0]);
        Assert.True(buffer.IsChanged(4));
        Assert.False(buffer.IsChanged(0));
        // Both nibbles are one undo step.
        Assert.True(buffer.Undo());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, rows[0]);
        Assert.True(buffer.Redo());

        // Overwrites, then appends; what passes 16 bytes is dropped.
        Assert.Equal(14, buffer.Write(2, new byte[20]));
        Assert.Equal(16, rows[0].Length);
        Assert.Equal(16, buffer.RowViewLength(0));
        Assert.Equal(1, buffer.Write(buffer.RowStart(1), [1]));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void TypingIntoTheEmptyRowAtTheEndAddsAKeyOfTheTypedLength()
    {
        var rows = new List<byte[]>();
        var buffer = Keys(rows);
        Assert.Equal(1, buffer.ViewLength);
        for (var i = 0; i < 4; i++)
        {
            buffer.WriteNibble(i, high: true, 1);
            buffer.WriteNibble(i, high: false, 2);
        }
        Assert.Equal(new byte[] { 0x12, 0x12, 0x12, 0x12 }, Assert.Single(rows));
        Assert.Equal(6, buffer.ViewLength); // 4 bytes, the cell to grow, the new empty row
        while (buffer.Undo()) { }
        Assert.Empty(rows);
    }

    [Fact]
    public void BytesAreDeletedAcrossRowsAndInsertedInOneStep()
    {
        var rows = new List<byte[]> { new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 7, 8, 9, 10 } };
        var buffer = Keys(rows);
        // From the 5th byte of row 0 to the 2nd of row 1 (the cell after row 0 is skipped).
        Assert.Equal(4, buffer.DeleteBytes(4, buffer.RowStart(1) + 1));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, rows[0]);
        Assert.Equal(new byte[] { 9, 10 }, rows[1]);
        Assert.True(buffer.Undo());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, rows[0]);
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, rows[1]);

        Assert.Equal(1, buffer.InsertBytes(1, 1));
        Assert.Equal(new byte[] { 1, 0, 2, 3, 4, 5, 6 }, rows[0]);
        Assert.Equal(2, buffer.DeleteBytes(0, 1));
        Assert.Equal(new byte[] { 2, 3, 4, 5, 6 }, rows[0]);

        // A row emptied stays as an empty row.
        Assert.Equal(4, buffer.DeleteBytes(buffer.RowStart(1), buffer.RowStart(1) + 3));
        Assert.Empty(rows[1]);
        Assert.False(buffer.IsRead(1));
    }

    [Fact]
    public void ReplacingASelectionCanShortenOrLengthenTheRow()
    {
        var rows = new List<byte[]> { new byte[] { 1, 2, 3, 4, 5, 6 } };
        var buffer = Keys(rows);
        Assert.Equal(1, buffer.ReplaceBytes(1, 4, [0xEE]));
        Assert.Equal(new byte[] { 1, 0xEE, 6 }, rows[0]);
        Assert.Equal(3, buffer.ReplaceBytes(1, 1, [7, 8, 9]));
        Assert.Equal(new byte[] { 1, 7, 8, 9, 6 }, rows[0]);
        // Only as many as fit in 16 bytes.
        Assert.Equal(11, buffer.ReplaceBytes(0, -1, new byte[20]));
        Assert.Equal(16, rows[0].Length);
    }

    [Fact]
    public void PastedRowsKeepTheirLengthsAndAddRowsPastTheEnd()
    {
        var rows = new List<byte[]> { new byte[] { 1, 2, 3, 4, 5, 6 } };
        var buffer = Keys(rows);
        Assert.Equal(3, buffer.WriteRows(0, [new byte[] { 9, 9, 9, 9 }, new byte[8], new byte[20]]));
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 4, 8, 16 }, new[] { rows[0].Length, rows[1].Length, rows[2].Length });
        Assert.True(buffer.Undo());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, Assert.Single(rows));
    }

    [Fact]
    public void FixedRowsAreUnchanged()
    {
        var rows = new List<byte[]> { new byte[6], Array.Empty<byte>() };
        var buffer = new HexBuffer(rows, 6) { CanResize = true };
        Assert.Equal(18, buffer.ViewLength);
        Assert.Equal(6, buffer.RowStart(1));
        Assert.Equal(1, buffer.RowAt(7));
        Assert.Equal(1, buffer.ColumnAt(7));
        Assert.Equal(0, buffer.InsertBytes(0, 1));
        Assert.Equal(0, buffer.DeleteBytes(0, 3));
        Assert.Equal(1, buffer.WriteRows(1, [new byte[] { 1, 2 }]));
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0 }, rows[1]);
        Assert.Throws<NotSupportedException>(() => Keys([]).Slice(0, 0));
    }
}
