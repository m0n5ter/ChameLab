using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ChameLab.Models;

/// <summary>What the capture looks like at first sight; a guess from the lengths of its high and low runs.</summary>
public enum LfModulation
{
    /// <summary>Flat carrier: the signal varies too little to carry data.</summary>
    None,
    /// <summary>Some variation, but too few level changes to measure.</summary>
    FewTransitions,
    /// <summary>Runs of one and two half-bits (Manchester and biphase look alike here).</summary>
    Manchester,
    /// <summary>Many runs far longer than the shortest one.</summary>
    AskNrz,
    /// <summary>Short runs of close but different lengths: a sub-carrier.</summary>
    Fsk,
    Unknown,
}

public static class LfModulationExtensions
{
    public static string Label(this LfModulation m) => m switch
    {
        LfModulation.None => "None (flat carrier)",
        LfModulation.FewTransitions => "Too few transitions",
        LfModulation.Manchester => "Manchester / Biphase",
        LfModulation.AskNrz => "ASK / NRZ",
        LfModulation.Fsk => "FSK / PSK",
        _ => "Unknown",
    };
}

/// <summary>Level statistics of a capture. Samples are one per carrier cycle, 8 µs apart.</summary>
/// <param name="GapThreshold">Half the mean: a sample below it is the field dropping out (a reader gap).</param>
/// <param name="GapCount">Samples below <paramref name="GapThreshold"/> after the start-up ringing.</param>
/// <param name="Threshold">The level splitting the settled signal into high and low, used to decode it.</param>
public sealed record LfSniffSummary(
    int SampleCount, int Min, int Max, int Mean, int GapThreshold, int GapCount, int Threshold)
{
    public int DurationUs => SampleCount * LfSniff.SampleUs;
    public int DynamicRange => Max - Min;
}

/// <param name="ClockDivisor">Bit period in carrier cycles (RF/n) nearest to two of the commonest runs.</param>
/// <param name="RunSamples">The commonest run length in samples: half a bit for Manchester.</param>
public sealed record LfModulationResult(LfModulation Kind, int? ClockDivisor = null, int? RunSamples = null);

public sealed record LfManchesterDecode(int ClockDivisor, bool Invert, IReadOnlyList<byte> Bits, int Errors)
{
    public string BitString => string.Concat(Bits.Select(b => b == 0 ? '0' : '1'));

    /// <summary>The bits as hex, the last digit padded with zeros on the right.</summary>
    public string Hex
    {
        get
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Bits.Count; i += 4)
            {
                var nibble = 0;
                for (var j = 0; j < 4; j++)
                    nibble = (nibble << 1) | (i + j < Bits.Count ? Bits[i + j] : 0);
                sb.Append("0123456789ABCDEF"[nibble]);
            }
            return sb.ToString();
        }
    }
}

/// <summary>Analysis of a raw LF capture (<c>LF_SNIFF</c>), after ChameleonUltraGUI's LF sniffing tool.</summary>
public static class LfSniff
{
    /// <summary>Microseconds between samples: one 125 kHz carrier cycle.</summary>
    public const int SampleUs = 8;

    /// <summary>Samples skipped as antenna start-up ringing (1.6 ms) before statistics and decoding.</summary>
    public const int SettleSamples = 200;

    /// <summary>The bit rates LF tags use, in carrier cycles per bit.</summary>
    public static readonly IReadOnlyList<int> ClockDivisors = [8, 16, 32, 40, 50, 64, 100, 128];

    public static LfSniffSummary Summarize(ReadOnlySpan<byte> samples)
    {
        if (samples.IsEmpty) return new LfSniffSummary(0, 0, 0, 0, 0, 0, 0);
        int min = 255, max = 0;
        long total = 0;
        foreach (var s in samples)
        {
            min = Math.Min(min, s);
            max = Math.Max(max, s);
            total += s;
        }
        var mean = (int)(total / samples.Length);
        var gapThreshold = mean / 2;
        var settled = Settled(samples);
        var gaps = 0;
        long settledTotal = 0;
        foreach (var s in settled)
        {
            if (s < gapThreshold) gaps++;
            settledTotal += s;
        }
        var threshold = (int)(settledTotal / settled.Length);
        return new LfSniffSummary(samples.Length, min, max, mean, gapThreshold, gaps, threshold);
    }

    public static LfModulationResult DetectModulation(ReadOnlySpan<byte> samples, LfSniffSummary? summary = null)
    {
        summary ??= Summarize(samples);
        if (samples.IsEmpty || summary.DynamicRange < 0x20) return new LfModulationResult(LfModulation.None);

        // The first and last runs are cut off by the capture window.
        var runs = Runs(Settled(samples), summary.Threshold, invert: false);
        var lengths = runs.Skip(1).SkipLast(1).Select(r => r.Length).ToArray();
        if (lengths.Length < 4) return new LfModulationResult(LfModulation.FewTransitions);

        // Commonest length, letting the edges wander by a sample either way.
        var counts = lengths.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        int Around(int l) => counts.GetValueOrDefault(l - 1) + counts.GetValueOrDefault(l) + counts.GetValueOrDefault(l + 1);
        var run = counts.Keys.OrderByDescending(Around).ThenBy(l => l).First();
        var clock = ClockDivisors.MinBy(d => Math.Abs(d - 2 * run));

        var tolerance = Math.Max(1, run / 3);
        bool Near(int l, int target) => Math.Abs(l - target) <= tolerance;
        var single = lengths.Count(l => Near(l, run));
        var dbl = lengths.Count(l => Near(l, 2 * run));
        var longRuns = lengths.Count(l => l > 3 * run);

        LfModulation kind;
        if (longRuns > lengths.Length * 0.3) kind = LfModulation.AskNrz;
        else if (single + dbl >= lengths.Length * 0.9 && dbl >= lengths.Length * 0.1) kind = LfModulation.Manchester;
        else if (run <= 6) kind = LfModulation.Fsk;
        else kind = LfModulation.Unknown;
        return new LfModulationResult(kind, clock, run);
    }

    /// <summary>
    /// Decodes the settled signal as Manchester at <paramref name="clockDivisor"/> carrier cycles per bit:
    /// high-to-low in a bit is 1, low-to-high 0 (<paramref name="invert"/> swaps them). Runs are counted in
    /// half-bits, so jitter of up to a third of a half-bit is absorbed; a run of any other length, or a bit
    /// without its mid-bit transition, counts as an error and the decoder picks up again after it.
    /// </summary>
    public static LfManchesterDecode DecodeManchester(ReadOnlySpan<byte> samples, int clockDivisor, bool invert,
        LfSniffSummary? summary = null)
    {
        summary ??= Summarize(samples);
        var bits = new List<byte>();
        var errors = 0;
        if (samples.IsEmpty || clockDivisor < 2) return new LfManchesterDecode(clockDivisor, invert, bits, 0);

        var half = clockDivisor / 2.0;
        var runs = Runs(Settled(samples), summary.Threshold, invert);
        var halves = new List<byte>();
        // The first run is cut off by the start of the window.
        for (var i = 1; i < runs.Count; i++)
        {
            var units = (int)Math.Round(runs[i].Length / half);
            var valid = units is 1 or 2 && Math.Abs(runs[i].Length - units * half) <= half / 3 + 1;
            if (!valid)
            {
                errors += DecodeHalves(halves, bits);
                halves.Clear();
                if (i < runs.Count - 1) errors++;
                continue;
            }
            for (var u = 0; u < units; u++) halves.Add(runs[i].Level);
        }
        errors += DecodeHalves(halves, bits);
        return new LfManchesterDecode(clockDivisor, invert, bits, errors);
    }

    /// <summary>Pairs half-bits into bits in whichever phase leaves fewer pairs without a transition.</summary>
    private static int DecodeHalves(List<byte> halves, List<byte> bits)
    {
        if (halves.Count < 2) return 0;
        int Bad(int phase)
        {
            var bad = 0;
            for (var i = phase; i + 1 < halves.Count; i += 2)
                if (halves[i] == halves[i + 1]) bad++;
            return bad;
        }
        var start = Bad(1) < Bad(0) ? 1 : 0;
        var errors = 0;
        for (var i = start; i + 1 < halves.Count; i += 2)
        {
            if (halves[i] == halves[i + 1]) errors++;
            else bits.Add(halves[i]);
        }
        return errors;
    }

    private static ReadOnlySpan<byte> Settled(ReadOnlySpan<byte> samples) =>
        samples.Length > SettleSamples ? samples[SettleSamples..] : samples;

    private static List<(byte Level, int Length)> Runs(ReadOnlySpan<byte> samples, int threshold, bool invert)
    {
        var runs = new List<(byte, int)>();
        if (samples.IsEmpty) return runs;
        byte Level(byte s) => (byte)((s > threshold) ^ invert ? 1 : 0);
        var current = Level(samples[0]);
        var length = 0;
        foreach (var s in samples)
        {
            var level = Level(s);
            if (level == current)
            {
                length++;
                continue;
            }
            runs.Add((current, length));
            current = level;
            length = 1;
        }
        runs.Add((current, length));
        return runs;
    }
}
