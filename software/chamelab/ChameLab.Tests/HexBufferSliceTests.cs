using System;
using System.Linq;
using ChameLab.Controls;
using Xunit;

namespace ChameLab.Tests;

public class HexBufferSliceTests
{
    [Fact]
    public void SliceEditsOriginalRowsAndSharesUndoAcrossSectors()
    {
        var rows = Enumerable.Range(0, 12).Select(_ => Array.Empty<byte>()).ToList();
        var memory = new HexBuffer(rows, 16);
        var first = memory.Slice(4, 4);
        var second = memory.Slice(8, 4);
        var changes = 0;
        first.Changed += (_, _) => changes++;
        Assert.False(first.IsRead(0));
        first.Write(0, [0xAB]);
        second.Write(0, [0xCD]);
        Assert.Equal(0xAB, rows[4][0]);
        Assert.Equal(0xCD, rows[8][0]);
        Assert.True(first.IsChanged(0));
        Assert.True(second.Undo());
        Assert.Empty(rows[8]);
        Assert.True(first.Undo());
        Assert.Empty(rows[4]);
        Assert.True(second.Redo());
        Assert.Equal(0xAB, rows[4][0]);
        Assert.Equal(5, changes);
    }

    [Fact]
    public void SliceClipsPasteAndRetainsAbsoluteLabelsAndByteMeaning()
    {
        var rows = Enumerable.Range(0, 20).Select(_ => new byte[16]).ToList();
        var memory = new HexBuffer(rows, 16)
        {
            RowLabel = row => row.ToString("D3"),
            Classify = (row, _) => row == 15 ? HexCellKind.KeyA : HexCellKind.Data,
            GroupHeader = row => ($"Sector {row / 4}", null),
            Describe = (row, col) => $"{row}:{col}",
        };
        var sector = memory.Slice(12, 4);
        Assert.Equal(192, sector.ByteOffset);
        Assert.Equal("012", sector.RowLabel(0));
        Assert.Equal("Sector 3", sector.GroupHeader(0)!.Value.Title);
        Assert.Equal(HexCellKind.KeyA, sector.Classify(3, 0));
        Assert.Equal("15:10", sector.Describe(3, 10));
        Assert.Equal(1, sector.Write(63, [1, 2, 3]));
        Assert.Equal(1, rows[15][15]);
        Assert.Equal(0, rows[16][0]);
        Assert.Equal(0, sector.Write(64, [1]));
        Assert.False(sector.CanResize);
    }
}
