using System;
using System.Text;
using ChameLab.Models;
using Xunit;

namespace ChameLab.Tests;

public class Block0IdentityTests
{
    // 4-byte UID: UID(4) BCC SAK ATQA(2)
    private const string Block0Uid4 = "4D4F4E531F0804006263646566676869";
    // 7-byte NXP UID: UID(7) SAK ATQA(2), ATQA 0x0044 LSB first
    private const string Block0Uid7 = "04A1B2C3D4E5F60844000000000000AA";
    // 10-byte UID: UID(10) SAK ATQA(2), ATQA 0x0084 LSB first
    private const string Block0Uid10 = "0411223344556677889908840000000A";

    [Theory]
    [InlineData(Block0Uid4, "4D4F4E53", 0x08, "0400")]
    [InlineData(Block0Uid7, "04A1B2C3D4E5F6", 0x08, "4400")]
    [InlineData(Block0Uid10, "04112233445566778899", 0x08, "8400")]
    public void DecodesUidSakAtqaForEachUidSize(string block0, string uid, byte sak, string atqa)
    {
        var id = MifareClassic.DecodeBlock0(Convert.FromHexString(block0));

        Assert.NotNull(id);
        Assert.Equal(Convert.FromHexString(uid), id.Value.Uid);
        Assert.Equal(sak, id.Value.Sak);
        Assert.Equal(Convert.FromHexString(atqa), id.Value.Atqa);
    }

    [Fact]
    public void SevenByteUidWhoseFourthByteLooksLikeABccStaysSevenBytes()
    {
        // 04^A1^B2^C3 = D4: the BCC check alone would call it a 4-byte UID, the ATQA says double size.
        var id = MifareClassic.DecodeBlock0(Convert.FromHexString(Block0Uid7));
        Assert.Equal(7, id!.Value.Uid.Length);
    }

    [Theory]
    [InlineData(Block0Uid4, "4D4F4E53")]
    [InlineData(Block0Uid7, "04A1B2C3D4E5F6")]
    [InlineData(Block0Uid10, "04112233445566778899")]
    public void BinRoundTripKeepsTheUid(string block0, string uid)
    {
        var dump = new MifareDump(MifareClassicType.Classic1K);
        dump.SetBlock(0, Convert.FromHexString(block0));
        var card = Assert.Single(CardFormats.Import("card.bin", dump.ToBin()));

        Assert.Equal(Convert.FromHexString(uid), card.Uid);
        Assert.Equal(0x08, card.Sak);
    }

    [Fact]
    public void EmlImportKeepsASevenByteUid()
    {
        var dump = new MifareDump(MifareClassicType.Classic1K);
        dump.SetBlock(0, Convert.FromHexString(Block0Uid7));
        var card = Assert.Single(CardFormats.Import("card.eml", Encoding.UTF8.GetBytes(dump.ToEml())));

        Assert.Equal(Convert.FromHexString("04A1B2C3D4E5F6"), card.Uid);
        Assert.Equal(Convert.FromHexString("4400"), card.Atqa);
    }

    [Fact]
    public void WriteDumpBuildsBlock0ForACardKnownOnlyByItsUid()
    {
        var card = new CardSave
        {
            Tag = ChameLab.Protocol.TagSpecificType.Mifare1024,
            Uid = Convert.FromHexString("4D4F4E53"), Sak = 0x08, Atqa = Convert.FromHexString("0400"),
        };

        var dump = card.ToMifareWriteDump();

        Assert.Equal(Convert.FromHexString(Block0Uid4), dump.GetBlock(0));
        Assert.Null(dump.GetBlock(1));
    }

    [Fact]
    public void WriteDumpBuildsASevenByteBlock0()
    {
        var card = new CardSave
        {
            Tag = ChameLab.Protocol.TagSpecificType.Mifare1024,
            Uid = Convert.FromHexString("04A1B2C3D4E5F6"), Sak = 0x08, Atqa = Convert.FromHexString("4400"),
        };

        Assert.Equal(Convert.FromHexString("04A1B2C3D4E5F6" + "08" + "4400" + "000000000000"), card.ToMifareWriteDump().GetBlock(0));
    }

    [Fact]
    public void WriteDumpPutsTheCardUidIntoAStaleBlock0()
    {
        var card = new CardSave
        {
            Tag = ChameLab.Protocol.TagSpecificType.Mifare1024,
            Uid = Convert.FromHexString("11223344"), Sak = 0x08, Atqa = Convert.FromHexString("0400"),
            Data = [Convert.FromHexString(Block0Uid4)],
        };

        // UID and BCC replaced; SAK, ATQA and manufacturer data kept.
        Assert.Equal(Convert.FromHexString("11223344440804006263646566676869"), card.ToMifareWriteDump().GetBlock(0));
        Assert.Equal(Convert.FromHexString(Block0Uid4), card.ToMifareDump().GetBlock(0));
    }

    [Fact]
    public void WriteDumpKeepsAMatchingBlock0()
    {
        var card = new CardSave
        {
            Tag = ChameLab.Protocol.TagSpecificType.Mifare1024,
            Uid = Convert.FromHexString("4D4F4E53"), Sak = 0x08, Atqa = Convert.FromHexString("0400"),
            Data = [Convert.FromHexString(Block0Uid4)],
        };

        Assert.Equal(Convert.FromHexString(Block0Uid4), card.ToMifareWriteDump().GetBlock(0));
    }
}
