using System;
using System.Linq;
using System.Text;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class MctImportTests
{
    private const string Trailer = "112233445566FF078069112233445566";
    private const string Empty = "00000000000000000000000000000000";

    private static string Sector(int n, string first = Empty, string trailer = Trailer) =>
        $"+Sector: {n}\n{first}\n{Empty}\n{Empty}\n{trailer}\n";

    [Fact]
    public void MissingSectorStaysUnreadAndDoesNotShiftTheRest()
    {
        var text = Sector(0, "4D4F4E531F0804000000000000000000")
            + string.Concat(Enumerable.Range(1, 15).Where(s => s != 3).Select(s => Sector(s)));
        var card = Assert.Single(CardFormats.Import("dump.mct", Encoding.UTF8.GetBytes(text)));

        Assert.Equal(TagSpecificType.Mifare1024, card.Tag);
        Assert.Equal(64, card.Data.Count);
        Assert.Equal(Convert.FromHexString("4D4F4E53"), card.Uid);
        Assert.All(Enumerable.Range(12, 4), b => Assert.Empty(card.Data[b]));
        Assert.Equal(Convert.FromHexString(Trailer), card.Data[63]);
    }

    [Fact]
    public void DashedBlockIsUnreadAndDashedKeyIsUnknown()
    {
        var text = Sector(0, "4D4F4E531F0804000000000000000000", "------------FF078069112233445566")
            .Replace($"{Empty}\n------", "--------------------------------\n------");
        var card = CardFormats.FromMct(text);

        Assert.Equal(TagSpecificType.MifareMini, card.Tag);
        Assert.Empty(card.Data[2]);
        Assert.Equal(Convert.FromHexString("000000000000FF078069112233445566"), card.Data[3]);
        Assert.Equal([0], card.Extra.UnknownKeyA);
        Assert.Empty(card.Extra.UnknownKeyB);
    }

    [Fact]
    public void UnknownKeysSurviveJsonAndDumpRoundTrip()
    {
        var card = CardFormats.FromMct(Sector(0, "4D4F4E531F0804000000000000000000", "------------FF078069------------"));
        var reloaded = CardSave.FromJson(card.ToJson().ToJsonString());
        Assert.Equal([0], reloaded.Extra.UnknownKeyA);
        Assert.Equal([0], reloaded.Extra.UnknownKeyB);

        var dump = reloaded.ToMifareDump();
        Assert.True(dump.KeyAUnknown[0]);
        Assert.Empty(dump.TrailerKeys(0));

        // A key found later goes into the trailer and is no longer unknown.
        dump.KeyA[0] = Convert.FromHexString("A0A1A2A3A4A5");
        dump.FillRecoveredKeys();
        var again = CardFormats.FromMifareDump(dump);
        Assert.Empty(again.Extra.UnknownKeyA);
        Assert.Equal([0], again.Extra.UnknownKeyB);
        Assert.Equal(Convert.FromHexString("A0A1A2A3A4A5"), Assert.Single(dump.TrailerKeys(0)));
    }

    [Fact]
    public void TrailerReadFromCardHidesKeyAAndKeyBUnlessReadable()
    {
        var dump = new MifareDump(MifareClassicType.Mini);
        dump.SetTrailerFromCard(0, Convert.FromHexString("000000000000FF078069112233445566"));
        dump.SetTrailerFromCard(1, Convert.FromHexString("00000000000078778800000000000000"));

        Assert.True(dump.KeyAUnknown[0]);
        Assert.False(dump.KeyBUnknown[0]); // FF 07 80: key B readable
        Assert.True(dump.KeyAUnknown[1]);
        Assert.True(dump.KeyBUnknown[1]);  // 78 77 88: key B hidden
    }

    [Fact]
    public void EditingAnUnknownKeyMakesItKnown()
    {
        var card = CardFormats.FromMct(Sector(0, "4D4F4E531F0804000000000000000000", "------------FF078069112233445566"));
        card.Name = "Test";
        var editor = new ChameLab.ViewModels.CardEditorViewModel(card);
        Assert.NotNull(editor.UnknownKeyWarning);
        Assert.Equal([0], editor.Build().Extra.UnknownKeyA);

        editor.Memory!.Write(3 * 16, Convert.FromHexString("A0A1A2A3A4A5"));
        Assert.Null(editor.UnknownKeyWarning);
        Assert.Empty(editor.Build().Extra.UnknownKeyA);
    }
}
