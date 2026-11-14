using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public sealed class DumpDiffTests
{
    private static CardSave Classic1K()
    {
        var card = new CardSave
        {
            Tag = TagSpecificType.Mifare1024,
            Uid = new byte[] { 1, 2, 3, 4 },
            Sak = 0x08,
            Atqa = new byte[] { 0x04, 0x00 },
        };
        for (var i = 0; i < 64; i++) card.Data.Add(Enumerable.Repeat((byte)i, 16).ToArray());
        return card;
    }

    [Fact]
    public void SameCard_IsSame()
    {
        var a = Classic1K();

        var diff = DumpComparison.Compare(a, a.Clone());

        Assert.True(diff.IsSame);
        Assert.Equal(64, diff.Blocks.Count);
        Assert.Equal(0, diff.DifferentBytes);
    }

    [Fact]
    public void ChangedBytes_AreFoundPerBlockAndByte()
    {
        var a = Classic1K();
        var b = a.Clone();
        b.Data[5] = (byte[])b.Data[5].Clone();
        b.Data[5][0] ^= 0xFF;
        b.Data[5][15] ^= 0x01;

        var diff = DumpComparison.Compare(a, b);

        Assert.Equal(1, diff.DifferentBlocks);
        Assert.Equal(2, diff.DifferentBytes);
        var block = Assert.Single(diff.Blocks, x => x.Differs);
        Assert.Equal(5, block.Index);
        Assert.True(block.ByteDiffers(0));
        Assert.False(block.ByteDiffers(1));
        Assert.True(block.ByteDiffers(15));
        Assert.Empty(diff.Fields);
    }

    [Fact]
    public void UnreadAndMissingBlocks_Differ()
    {
        var a = Classic1K();
        var b = a.Clone();
        b.Data[3] = Array.Empty<byte>();
        b.Data.RemoveRange(60, 4);

        var diff = DumpComparison.Compare(a, b);

        Assert.Equal(64, diff.Blocks.Count);
        Assert.Equal(new[] { 3, 60, 61, 62, 63 }, diff.Blocks.Where(x => x.Differs).Select(x => x.Index));
        Assert.Null(diff.Blocks[3].B);
        Assert.Equal(5 * 16, diff.DifferentBytes);
    }

    [Fact]
    public void IdentityFields_AreListed()
    {
        var a = Classic1K();
        var b = a.Clone();
        b.Uid = new byte[] { 9, 9, 9, 9 };
        b.Sak = 0x88;
        b.Tag = TagSpecificType.Mifare4096;

        var diff = DumpComparison.Compare(a, b);

        Assert.Equal(new[] { DumpField.Type, DumpField.Uid, DumpField.Sak }, diff.Fields.Select(f => f.Field));
        Assert.Equal("01 02 03 04", diff.Fields[1].A);
        Assert.Equal("09 09 09 09", diff.Fields[1].B);
        Assert.False(diff.IsSame);
    }

    [Fact]
    public void OnlySameKindOfMemory_IsCompared()
    {
        var classic = Classic1K();
        var ntag = new CardSave { Tag = TagSpecificType.Ntag215 };
        var em = new CardSave { Tag = TagSpecificType.Em410x };

        Assert.True(DumpComparison.CanCompare(classic, new CardSave { Tag = TagSpecificType.Mifare4096 }));
        Assert.True(DumpComparison.CanCompare(ntag, new CardSave { Tag = TagSpecificType.Ntag213 }));
        Assert.False(DumpComparison.CanCompare(classic, ntag));
        Assert.False(DumpComparison.CanCompare(em, em));
    }
}
