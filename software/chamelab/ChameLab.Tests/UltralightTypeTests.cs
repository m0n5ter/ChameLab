using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public sealed class UltralightTypeTests
{
    private static byte[] Version(byte product, byte size) => [0x00, 0x04, product, 0x01, 0x01, 0x00, size, 0x03];

    [Theory]
    [InlineData(0x03, 0x0B, TagSpecificType.Mf0Ul11)]
    [InlineData(0x03, 0x0E, TagSpecificType.Mf0Ul21)]
    [InlineData(0x04, 0x0B, TagSpecificType.Ntag210)]
    [InlineData(0x04, 0x0E, TagSpecificType.Ntag212)]
    [InlineData(0x04, 0x0F, TagSpecificType.Ntag213)]
    [InlineData(0x04, 0x11, TagSpecificType.Ntag215)]
    [InlineData(0x04, 0x13, TagSpecificType.Ntag216)]
    // Clones with a wrong product type are told by their size.
    [InlineData(0x00, 0x0E, TagSpecificType.Mf0Ul21)]
    [InlineData(0x05, 0x0B, TagSpecificType.Mf0Ul11)]
    [InlineData(0x00, 0x0F, TagSpecificType.Ntag213)]
    public void VersionNamesTheModel(byte product, byte size, TagSpecificType expected) =>
        Assert.Equal(expected, CardTags.UltralightFromVersion(Version(product, size)));

    [Theory]
    [InlineData(0x03, 0x0F)] // Ultralight AES
    [InlineData(0x04, 0x15)]
    public void UnknownVersionIsNull(byte product, byte size) =>
        Assert.Null(CardTags.UltralightFromVersion(Version(product, size)));

    [Fact]
    public void ProxmarkMfuCloneImportsAsUltralightEv1()
    {
        var blocks = string.Join(",", Enumerable.Range(0, 41).Select(i => $"\"{i}\": \"{(i == 0 ? "34C5F38A" : "00000000")}\""));
        var json = $$"""
            {
              "Created": "proxmark3",
              "FileType": "mfu",
              "Card": {
                "UID": "34C5F389623418",
                "Version": "0034210101000E03",
                "Signature": "C6E1979AAC6202DBFC7DFC14B8CC2F79E10C3FEA41D46A086CD1069806227BF9",
                "Counter0": "010000", "Tearing0": "00",
                "Counter1": "000000", "Tearing1": "00",
                "Counter2": "000000", "Tearing2": "00"
              },
              "blocks": { {{blocks}} }
            }
            """;

        var card = Assert.Single(CardFormats.Import("ul.json", System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Equal(TagSpecificType.Mf0Ul21, card.Tag);
        Assert.Equal(41, card.Data.Count);
        Assert.Equal(0x00, card.Sak);
        Assert.Equal(new byte[] { 0x44, 0x00 }, card.Atqa);
        Assert.Equal(8, card.Extra.UltralightVersion?.Length);
        Assert.Equal(32, card.Extra.UltralightSignature?.Length);
        Assert.Equal(new[] { 1, 0, 0 }, card.Extra.UltralightCounters);
    }

    [Theory]
    [InlineData(0x00, 41, TagSpecificType.Mf0Ul21)]
    [InlineData(0x03, 20, TagSpecificType.Mf0Ul11)]
    [InlineData(0x04, 41, TagSpecificType.Ntag212)]
    [InlineData(0x04, 20, TagSpecificType.Ntag210)]
    [InlineData(0x03, 45, TagSpecificType.Ntag213)]
    [InlineData(0x04, 100, TagSpecificType.Ntag215)]
    [InlineData(0x04, 200, TagSpecificType.Ntag216)]
    public void UnknownModelIsTypedByItsPages(byte product, int pages, TagSpecificType expected) =>
        Assert.Equal(expected, CardTags.UltralightForUnknownVersion(Version(product, 0xFF), pages));
}
