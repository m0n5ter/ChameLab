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
        var cycle = new LfCardCycle(3, [
            Card(TagSpecificType.Em410x, "0001020304"),
            Card(TagSpecificType.Em410xElectra, "AABBCCDDEE0102030405060708"),
            Card(TagSpecificType.Viking, "DEADBEEF"),
        ], 7);
        var data = cycle.Encode();
        Assert.Equal("030703"
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
        var cycle = new LfCardCycle(1, types.Select(t => new LfCycleCard(t, Enumerable.Range(1, t.LfIdLength()).Select(i => (byte)i).ToArray())).ToArray());
        Assert.Equal(3 + 7 * 18, cycle.Encode().Length);
        Assert.True(cycle.SameAs(LfCardCycle.Decode(cycle.Encode())));
        var more = new LfCardCycle(10, [Card(TagSpecificType.Idteck, "4944544BDEADBEEF"),
            new LfCycleCard(TagSpecificType.FarpointePyramid, new byte[16])], 30);
        Assert.True(more.SameAs(LfCardCycle.Decode(more.Encode())));
    }

    [Fact]
    public void EmptyListDisablesCycle() => Assert.Equal(new byte[] { 1, 0, 0 }, new LfCardCycle(1, []).Encode());

    [Fact]
    public void RejectsInvalidCyclesBeforeUpload()
    {
        LfCardCycle[] invalid =
        [
            new(0, []), new(11, []), new(1, [], -1), new(1, [], 31),
            new(1, Enumerable.Range(0, 8).Select(i => Card(TagSpecificType.Em410x, i.ToString("X10"))).ToArray()),
            new(1, [new LfCycleCard(TagSpecificType.Em410x, new byte[6])]),
            new(1, [new LfCycleCard(TagSpecificType.Em410xElectra, new byte[5])]),
            new(1, [new LfCycleCard(TagSpecificType.Em410x64, new byte[5])]),
            new(1, [new LfCycleCard(TagSpecificType.Mifare1024, new byte[4])]),
        ];
        foreach (var cycle in invalid) Assert.Throws<ArgumentException>(() => cycle.Encode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0100")]
    [InlineData("010001")]
    [InlineData("000000")]
    [InlineData("0B0000")]
    [InlineData("011F00")]
    [InlineData("010008")]
    [InlineData("010001" + "0000" + "00000000000000000000000000000000")]
    public void RejectsMalformedFirmwareResponses(string hex)
    {
        Assert.Throws<ArgumentException>(() => LfCardCycle.Decode(Convert.FromHexString(hex)));
    }
}
