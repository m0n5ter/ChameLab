using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using Xunit;

namespace ChameLab.Tests;

public class LfSniffTests
{
    private const byte High = 0xC0;
    private const byte Low = 0x90;

    /// <summary>
    /// A capture of <paramref name="bits"/> sent as Manchester (1 = high then low) at RF/<paramref name="clock"/>,
    /// behind the start-up samples the analysis skips; <paramref name="jitter"/> moves every other edge.
    /// </summary>
    private static byte[] Manchester(string bits, int clock, int jitter = 0)
    {
        var samples = new List<byte>(Enumerable.Repeat(High, LfSniff.SettleSamples));
        var half = clock / 2;
        var edge = 0;
        foreach (var bit in bits)
        {
            var first = bit == '1' ? High : Low;
            var second = bit == '1' ? Low : High;
            var shift = edge++ % 2 == 0 ? jitter : -jitter;
            samples.AddRange(Enumerable.Repeat(first, half + shift));
            samples.AddRange(Enumerable.Repeat(second, half - shift));
        }
        return samples.ToArray();
    }

    private const string Pattern = "1111111110010110100101101001011010011010010110100101100110";

    [Fact]
    public void SummarizesLevelsAndGaps()
    {
        var samples = Enumerable.Repeat((byte)0xB0, 300).Concat(Enumerable.Repeat((byte)0x05, 20)).ToArray();
        var s = LfSniff.Summarize(samples);
        Assert.Equal(320, s.SampleCount);
        Assert.Equal(320 * 8, s.DurationUs);
        Assert.Equal(0x05, s.Min);
        Assert.Equal(0xB0, s.Max);
        Assert.Equal((300 * 0xB0 + 20 * 5) / 320, s.Mean);
        Assert.Equal(s.Mean / 2, s.GapThreshold);
        Assert.Equal(20, s.GapCount);
    }

    [Fact]
    public void FlatCarrierHasNoModulationAndDecodesNothing()
    {
        var samples = Enumerable.Repeat((byte)0xB0, 4000).ToArray();
        Assert.Equal(LfModulation.None, LfSniff.DetectModulation(samples).Kind);
        Assert.Empty(LfSniff.DecodeManchester(samples, 64, invert: false).Bits);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void DetectsManchesterAndItsClock(int clock)
    {
        var m = LfSniff.DetectModulation(Manchester(Pattern, clock));
        Assert.Equal(LfModulation.Manchester, m.Kind);
        Assert.Equal(clock, m.ClockDivisor);
        Assert.Equal(clock / 2, m.RunSamples);
    }

    [Theory]
    [InlineData(32, 0)]
    [InlineData(64, 0)]
    [InlineData(64, 4)]
    public void DecodesManchesterBits(int clock, int jitter)
    {
        var d = LfSniff.DecodeManchester(Manchester(Pattern, clock, jitter), clock, invert: false);
        Assert.Equal(0, d.Errors);
        // The first bit's leading half is merged with the start-up carrier, so it may be lost.
        Assert.EndsWith(d.BitString, Pattern);
        Assert.True(d.Bits.Count >= Pattern.Length - 1);
    }

    [Fact]
    public void InvertDecodesTheComplement()
    {
        var d = LfSniff.DecodeManchester(Manchester(Pattern, 32), 32, invert: true);
        var complement = new string(Pattern.Select(c => c == '1' ? '0' : '1').ToArray());
        Assert.EndsWith(d.BitString, complement);
    }

    [Fact]
    public void WrongClockReportsBreaks()
    {
        var d = LfSniff.DecodeManchester(Manchester(Pattern, 64), 16, invert: false);
        Assert.True(d.Errors > 0 || d.Bits.Count == 0);
    }

    [Fact]
    public void HexPadsTheLastNibble()
    {
        var d = new LfManchesterDecode(64, false, new byte[] { 1, 0, 1, 0, 1, 1 }, 0);
        Assert.Equal("101011", d.BitString);
        Assert.Equal("AC", d.Hex);
    }

    [Fact]
    public void HexDumpShowsOffsetBytesAndLevels()
    {
        var samples = new byte[] { 0x00, 0x20, 0x70, 0x90, 0xB0, 0xD0, 0xFF };
        Assert.Equal("0000  00 20 70 90 B0 D0 FF " + new string(' ', 27) + " _.-+oO#", LfSniff.HexDump(samples));
        var lines = LfSniff.HexDump(new byte[600]).Split('\n');
        Assert.Equal(32, lines.Length);
        Assert.StartsWith("01F0  ", lines[^1]);
    }
}
