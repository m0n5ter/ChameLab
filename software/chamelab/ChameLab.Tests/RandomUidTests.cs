using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
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
}
