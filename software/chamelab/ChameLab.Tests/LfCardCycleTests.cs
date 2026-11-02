using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class LfCardCycleTests
{
    private static LfCycleCard Card(TagSpecificType type, string hex) => new(type, Convert.FromHexString(hex));

    [Fact]
    public void EncodesMixedCardsInFirmwareOrderAndKeepsLeadingZeros()
    {
        var cycle = new LfCardCycle([
            Card(TagSpecificType.Em410x, "0001020304"),
            Card(TagSpecificType.Em410xElectra, "AABBCCDDEE0102030405060708"),
            Card(TagSpecificType.Viking, "DEADBEEF"),
        ]);
        var data = cycle.Encode();
        Assert.Equal("03"
            + "0064" + "0001020304" + "0000000000000000000000"
            + "0068" + "AABBCCDDEE0102030405060708" + "000000"
            + Convert.ToHexString([(byte)((ushort)TagSpecificType.Viking >> 8), (byte)TagSpecificType.Viking]) + "DEADBEEF" + "000000000000000000000000",
            Convert.ToHexString(data));
        Assert.True(cycle.SameAs(LfCardCycle.Decode(data)));
    }

    [Fact]
    public void EveryEmulatedLfTypeRoundTrips()
    {
        TagSpecificType[] types =
        [
            TagSpecificType.Em410x, TagSpecificType.Em410xElectra, TagSpecificType.HidProx, TagSpecificType.IoProx,
            TagSpecificType.Viking, TagSpecificType.Pac, TagSpecificType.Jablotron,
        ];
        var cycle = new LfCardCycle(types.Select(t => new LfCycleCard(t, Enumerable.Range(1, t.LfIdLength()).Select(i => (byte)i).ToArray())).ToArray());
        Assert.Equal(1 + 7 * 18, cycle.Encode().Length);
        Assert.True(cycle.SameAs(LfCardCycle.Decode(cycle.Encode())));
        var more = new LfCardCycle([Card(TagSpecificType.Idteck, "4944544BDEADBEEF"),
            new LfCycleCard(TagSpecificType.FarpointePyramid, new byte[16])]);
        Assert.True(more.SameAs(LfCardCycle.Decode(more.Encode())));
    }

    [Fact]
    public void EmptyListDisablesCycle() => Assert.Equal(new byte[] { 0 }, new LfCardCycle([]).Encode());

    [Fact]
    public void RejectsInvalidCyclesBeforeUpload()
    {
        LfCardCycle[] invalid =
        [
            new(Enumerable.Range(0, 8).Select(i => Card(TagSpecificType.Em410x, i.ToString("X10"))).ToArray()),
            new([new LfCycleCard(TagSpecificType.Em410x, new byte[6])]),
            new([new LfCycleCard(TagSpecificType.Em410xElectra, new byte[5])]),
            new([new LfCycleCard(TagSpecificType.Em410x64, new byte[5])]),
            new([new LfCycleCard(TagSpecificType.Mifare1024, new byte[4])]),
        ];
        foreach (var cycle in invalid) Assert.Throws<ArgumentException>(() => cycle.Encode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("08")]
    [InlineData("0100")]
    [InlineData("01")]
    [InlineData("0100640000000000")]
    [InlineData("01" + "0000" + "00000000000000000000000000000000")]
    public void RejectsMalformedFirmwareResponses(string hex)
    {
        Assert.Throws<ArgumentException>(() => LfCardCycle.Decode(Convert.FromHexString(hex)));
    }
}
