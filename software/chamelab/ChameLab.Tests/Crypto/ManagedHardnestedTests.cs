using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Crypto;
using ChameLab.Crypto.Hardnested;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests.Crypto;

public class ManagedHardnestedTests
{
    [Fact]
    public void PackedPartialSums_MatchEveryGeneratedEntry()
    {
        var expected = HardnestedTables.BuildPartialSums();
        var actual = HardnestedTables.Shared.Value.PartialSums;
        Assert.True(expected[0].AsSpan().SequenceEqual(actual[0]));
        Assert.True(expected[1].AsSpan().SequenceEqual(actual[1]));
    }

    [Fact]
    public void PartialSums_MatchUpstreamEnumeration()
    {
        var actual = HardnestedTables.Shared.Value.PartialSums;
        for (uint state = 0; state < (1 << 20); state += 251)
            for (int half = 0; half < 2; half++)
            {
                int sum = 0;
                for (int path = 0; path < 16; path++)
                {
                    uint s = state;
                    int parity = half == 1 ? Crypto1.Filter(s) ^ 1 : 0;
                    for (int bit = 3; bit >= 0; bit--)
                    {
                        s = (s << 1) | (uint)((path >> bit) & 1);
                        parity ^= Crypto1.Filter(s);
                    }
                    sum += parity;
                }
                Assert.Equal(sum / 2, actual[half][state]);
            }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 63)]
    [InlineData(false, 64)]
    [InlineData(false, 255)]
    [InlineData(false, 256)]
    [InlineData(false, 276)]
    [InlineData(true, 0)]
    [InlineData(true, 63)]
    [InlineData(true, 64)]
    [InlineData(true, 276)]
    public void BitslicedCore_RecoversAcrossLanesAndPartialBlocks(bool scalar, int position)
    {
        const ulong key = 0xA0A1A2A3A4A5;
        var capture = Generate(key, 0x10203040, 4096);
        var group = capture.Samples.GroupBy(n => n.EncryptedNonce >> 24).First(g => g.Count() >= 16);
        int first = (int)group.Key;
        var state = new Crypto1State(key);
        state.Crypto1Byte((byte)((capture.AuthenticationUid >> 24) ^ first), true);
        var even = Enumerable.Range(0, 277).Select(i => (uint)(i * 12345) & 0xffffff).ToArray();
        even[position] = state.Even & 0xffffff;
        uint[] odd = [1, 17, state.Odd & 0xffffff, 103];
        var samples = group.DistinctBy(n => (n.EncryptedNonce >> 16) & 255).ToArray();
        var search = new HardnestedSearch(capture, first, samples, default);
        if (scalar) HardnestedScalar.Search(search, odd, even, 0, odd.Length);
        else HardnestedVector.Search(search, odd, even, 0, odd.Length);
        Assert.True(search.Stopped);
    }

    [Fact]
    public void BitslicedCore_RejectsIncorrectCandidates()
    {
        var capture = Generate(0x123456789ABC, 0xCAFEBABE, 4096);
        var group = capture.Samples.GroupBy(n => n.EncryptedNonce >> 24).First(g => g.Count() >= 16);
        var search = new HardnestedSearch(capture, (int)group.Key,
            group.DistinctBy(n => (n.EncryptedNonce >> 16) & 255).ToArray(), default);
        HardnestedVector.Search(search, [1, 2, 3], [4, 5, 6], 0, 3);
        Assert.False(search.Stopped);
    }

    [Theory]
    [InlineData(0UL, 0U)]
    [InlineData(0xA0A1A2A3A4A5UL, 0xFFFFFFFFU)]
    public void Recover_CompleteCapture(ulong key, uint uid)
    {
        Assert.Equal(key, ManagedHardnestedEngine.Recover(Generate(key, uid, 12000)));
    }

    [Fact]
    public void Recover_OriginalCCapture()
    {
        var samples = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Reference", "hardnested_capture.txt"))
            .Select(line => line.Split('|')).Select(p => new HardnestedSample(uint.Parse(p[0]), byte.Parse(p[1]))).ToArray();
        Assert.Equal(0x112233445566UL, ManagedHardnestedEngine.Recover(new(0x12345678, samples)));
    }

    [Fact]
    public void Capture_RejectsMissingBytesAndConflictingParity()
    {
        Assert.Throws<ArgumentException>(() => ManagedHardnestedEngine.Recover(new(1, [])));
        var capture = Generate(0x112233445566, 0x12345678, 4096);
        var bad = capture.Samples[0] with { EncryptedParity = (byte)(capture.Samples[0].EncryptedParity ^ 8) };
        Assert.Throws<ArgumentException>(() => new HardnestedReduction(capture with { Samples = [.. capture.Samples, bad] }));
        bad = bad with { EncryptedParity = 16 };
        Assert.Throws<ArgumentException>(() => new HardnestedReduction(capture with { Samples = [.. capture.Samples, bad] }));
    }

    [Fact]
    public async Task Cancellation_StopsReductionAndReleasesEngine()
    {
        var capture = Generate(0x112233445566, 0x12345678, 4096);
        using var stop = new CancellationTokenSource();
        var engine = new ManagedHardnestedEngine();
        var progress = new CallbackProgress(message => { if (message.StartsWith("Reducing")) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecoverAsync(capture, progress, stop.Token));
        // A subsequent call can acquire the run lock and validates its own input.
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RecoverAsync(new(0, []), null, default));
    }

    [Fact]
    public void Cancellation_StopsBeforeWork()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ManagedHardnestedEngine.Recover(new(0, []), ct: stop.Token));
    }

    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    internal static HardnestedCapture Generate(ulong key, uint uid, int count)
    {
        uint random = 0x9325abcd;
        var samples = new HardnestedSample[count];
        for (int i = 0; i < count; i++)
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            var state = new Crypto1State(key);
            uint encrypted = 0; byte parity = 0;
            for (int shift = 24; shift >= 0; shift -= 8)
            {
                byte plain = (byte)(random >> shift);
                encrypted = (encrypted << 8) | (uint)(plain ^ state.Crypto1Byte((byte)(plain ^ (uid >> shift))));
                parity = (byte)((parity << 1) | (Crypto1.OddParity8(plain) ^ Crypto1.Filter(state.Odd)));
            }
            samples[i] = new(encrypted, parity);
        }
        return new(uid, samples);
    }
}
