using System;
using System.Linq;
using ChameLab.Models;
using Xunit;

namespace ChameLab.Tests;

public class LfCardCycleTests
{
    [Fact]
    public void EncodesInFirmwareOrderAndKeepsLeadingZeros()
    {
        var cycle = LfCardCycle.Parse("00:01:02:03:04\r\nAA BB CC DD EE", 3);
        Assert.Equal("03020001020304AABBCCDDEE", Convert.ToHexString(cycle.Encode()));
        Assert.Equal("0001020304" + Environment.NewLine + "AABBCCDDEE", LfCardCycle.Decode(cycle.Encode()).Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void AllowsMaximumListAndTiming(int bursts)
    {
        var text = string.Join("\n", Enumerable.Range(0, 7).Select(i => i.ToString("X10")));
        var cycle = LfCardCycle.Parse(text, bursts);
        Assert.Equal(37, cycle.Encode().Length);
        Assert.Equal(7, LfCardCycle.Decode(cycle.Encode()).ExtraIds.Length);
    }

    [Fact]
    public void EmptyListDisablesCycle()
    {
        Assert.Equal(new byte[] { 1, 0 }, LfCardCycle.Parse("\n\r\n", 1).Encode());
    }

    [Theory]
    [InlineData("1234", 1)]
    [InlineData("GG11223344", 1)]
    [InlineData("112233445566", 1)]
    [InlineData("0000000001\n0000000001", 1)]
    [InlineData("", 0)]
    [InlineData("", 11)]
    public void RejectsInvalidInputBeforeUpload(string text, int bursts)
    {
        Assert.Throws<ArgumentException>(() => LfCardCycle.Parse(text, bursts));
    }

    [Fact]
    public void RejectsNinthCard()
    {
        var text = string.Join("\n", Enumerable.Range(0, 8).Select(i => i.ToString("X10")));
        Assert.Throws<ArgumentException>(() => LfCardCycle.Parse(text, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0101")]
    [InlineData("010000")]
    [InlineData("0000")]
    [InlineData("0B00")]
    [InlineData("0108")]
    public void RejectsMalformedFirmwareResponses(string hex)
    {
        Assert.Throws<ArgumentException>(() => LfCardCycle.Decode(Convert.FromHexString(hex)));
    }
}
