using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using System.Reflection;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class RandomUidTests
{
    [Theory]
    [InlineData(TagSpecificType.Mifare1024, 0, 4)]
    [InlineData(TagSpecificType.Mifare1024, 7, 7)]
    [InlineData(TagSpecificType.Mifare4096, 10, 10)]
    [InlineData(TagSpecificType.Mifare1024, 5, 4)]
    [InlineData(TagSpecificType.Ntag215, 4, 7)]
    public void HfUidHasAValidLength(TagSpecificType tag, int current, int expected)
    {
        for (var i = 0; i < 200; i++)
        {
            var uid = RandomUid.Generate(tag, current, new Random(i));
            Assert.Equal(expected, uid.Length);
            if (uid.Length == 4) Assert.DoesNotContain(uid[0], new byte[] { 0x88, 0x08 });
            else
            {
                Assert.Equal(0x04, uid[0]);
                Assert.NotEqual(0x88, uid[3]);
            }
        }
    }

    [Fact]
    public void LfIdsDecodeToTheirFormat()
    {
        foreach (var tag in TagTypeExtensions.LfTypes().Where(RandomUid.Supports))
            Assert.Equal(tag.LfIdLength(), RandomUid.Generate(tag).Length);

        var hid = RandomUid.Generate(TagSpecificType.HidProx);
        Assert.Equal(1, hid[0]);
        Assert.Contains(LfIdDecoder.Decode(TagSpecificType.HidProx, hid), f => f.Field == ChameLab.Localization.Strings.Lf_CardNumber);

        var (format, fc, _) = PyramidFrame.Decode(RandomUid.Generate(TagSpecificType.FarpointePyramid));
        Assert.Equal(26, format);
        Assert.NotNull(fc);

        for (var i = 0; i < 200; i++)
        {
            var electra = RandomUid.Generate(TagSpecificType.Em410xElectra, random: new Random(i));
            Assert.DoesNotContain("111111111", Convert.ToString(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(electra.AsSpan(5)), 2).PadLeft(64, '0'));
        }

        Assert.All(RandomUid.Generate(TagSpecificType.Pac), b => Assert.True(Uri.IsHexDigit((char)b)));
    }

    [Theory]
    [InlineData(TagSpecificType.IoProx)]
    [InlineData(TagSpecificType.Idteck)]
    public void FramesWithCheckFieldsAreNotOffered(TagSpecificType tag) => Assert.False(RandomUid.Supports(tag));

    [Fact]
    public void EditorFillsTheUidAndBlockZero()
    {
        var form = new CardEditorViewModel(new CardSave { Tag = TagSpecificType.Mifare1024 }, blankMemory: true);
        Assert.True(form.CanGenerateUid);
        form.GenerateUidCommand.Execute(null);
        Assert.True(Hex.TryParse(form.Uid, out var uid));
        Assert.Equal(4, uid.Length);
    }

    private static CardSave ReadClassic()
    {
        var uid = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var data = BlankMemory.Create(TagSpecificType.Mifare1024, uid, 0x08, [0x04, 0x00]);
        data[0][8] = 0xAB; // manufacturer data
        return new CardSave { Name = "Door", Tag = TagSpecificType.Mifare1024, Uid = uid, Sak = 0x08, Atqa = [0x04, 0x00], Data = data };
    }

    [Fact]
    public void GeneratedUidIsWrittenToBlockZeroOfASavedCard()
    {
        var form = new CardEditorViewModel(ReadClassic());
        form.GenerateUidCommand.Execute(null);

        Assert.True(Hex.TryParse(form.Uid, out var uid));
        var block0 = form.Draft().Data[0];
        Assert.Equal(uid, block0[..4]);
        Assert.Equal((byte)(uid[0] ^ uid[1] ^ uid[2] ^ uid[3]), block0[4]);
        Assert.Equal(0x08, block0[5]);
        Assert.Equal(0xAB, block0[8]);
        Assert.True(form.IsDirty);
        Assert.True(form.Memory!.Undo());
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, form.Draft().Data[0][..4]);
    }

    [Fact]
    public void UnreadBlockZeroStaysUnread()
    {
        var card = ReadClassic();
        card.Data[0] = [];
        var form = new CardEditorViewModel(card);
        form.GenerateUidCommand.Execute(null);
        Assert.Empty(form.Draft().Data[0]);
    }

    [Fact]
    public void GeneratedUidIsWrittenToUltralightPages()
    {
        var uid = new byte[] { 0x04, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var card = new CardSave { Name = "Tag", Tag = TagSpecificType.Ntag215, Uid = uid, Sak = 0, Atqa = [0x44, 0x00],
            Data = BlankMemory.Create(TagSpecificType.Ntag215, uid, 0, [0x44, 0x00]) };
        var form = new CardEditorViewModel(card);
        form.GenerateUidCommand.Execute(null);

        Assert.True(Hex.TryParse(form.Uid, out var next));
        var pages = form.Draft().Data;
        Assert.Equal(next[..3], pages[0][..3]);
        Assert.Equal((byte)(0x88 ^ next[0] ^ next[1] ^ next[2]), pages[0][3]);
        Assert.Equal(next[3..], pages[1]);
        Assert.Equal((byte)(next[3] ^ next[4] ^ next[5] ^ next[6]), pages[2][0]);
    }

    [Fact]
    public void ScannedCardMatchingASavedOneIsCheckedAgain()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        library.Add(ReadClassic());
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.Pane))!.SetValue(vm, CardsPane.Add);
        var scanned = ReadClassic();
        typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.Form))!.SetValue(vm, new CardEditorViewModel(scanned, sameFrequency: true));
        typeof(CardsViewModel).GetMethod("SetScanMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [scanned]);
        Assert.True(vm.HasScanMatches);
        Assert.False(vm.IsNewCardUnsaved);

        vm.Form!.GenerateUidCommand.Execute(null);
        Assert.False(vm.HasScanMatches);
        Assert.True(vm.IsNewCardUnsaved);

        vm.Form.Memory!.Undo();
        vm.Form.Uid = "11 22 33 44";
        Assert.True(vm.HasScanMatches);
    }
}
