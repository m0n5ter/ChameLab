using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;

namespace ChameLab.Services;

public readonly record struct HardnestedSample(uint EncryptedNonce, byte EncryptedParity);

public sealed record HardnestedCapture(uint AuthenticationUid, IReadOnlyList<HardnestedSample> Samples);

public interface IHardnestedEngine
{
    string? UnavailableReason { get; }
    Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct);
}

public sealed class UnavailableHardnestedEngine : IHardnestedEngine
{
    public string UnavailableReason =>
        Strings.Recovery_HardnestedUnavailable;

    public Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct) =>
        throw new NotSupportedException(UnavailableReason);
}

public interface IHardnestedNonceSource
{
    Task<IReadOnlyList<HardnestedSample>> ReadBatchAsync(bool slow, CancellationToken ct);
}

public static class HardnestedCollector
{
    public static async Task<HardnestedCapture> CollectAsync(
        IHardnestedNonceSource source, uint authenticationUid,
        IProgress<string>? progress, CancellationToken ct,
        int minimumSamples = 4096, int maximumBatches = 256, int maximumAttempts = 2)
    {
        if (minimumSamples < 1) throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        if (maximumBatches < 1) throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var samples = new List<HardnestedSample>();
            var parityByFirstByte = new int?[256];
            var distinct = 0;
            var paritySum = 0;
            var inconsistent = false;
            for (var batch = 0; batch < maximumBatches; batch++)
            {
                ct.ThrowIfCancellationRequested();
                var received = await source.ReadBatchAsync(attempt != 0, ct);
                foreach (var sample in received)
                {
                    if (sample.EncryptedParity > 15)
                        throw new InvalidOperationException(Strings.Recovery_InvalidNonceParity);
                    var firstByte = (int)(sample.EncryptedNonce >> 24);
                    var parity = BitOperations.PopCount((uint)firstByte ^ (uint)((sample.EncryptedParity >> 3) & 1)) & 1;
                    if (parityByFirstByte[firstByte] is { } previous)
                    {
                        if (previous != parity) inconsistent = true;
                    }
                    else
                    {
                        parityByFirstByte[firstByte] = parity;
                        distinct++;
                        paritySum += parity;
                    }
                    samples.Add(sample);
                }
                progress?.Report(string.Format(attempt == 0 ? Strings.Recovery_HardnestedCollecting : Strings.Recovery_HardnestedCollectingSlow,
                    samples.Count, distinct));
                if (inconsistent) break;
                if (distinct != 256 || samples.Count < minimumSamples) continue;
                if (!ValidSum(paritySum)) break;
                return new HardnestedCapture(authenticationUid, samples.ToArray());
            }
        }
        throw new InvalidOperationException(
            Strings.Recovery_HardnestedInconsistent);
    }

    private static bool ValidSum(int sum) => sum is
        0 or 32 or 56 or 64 or 80 or 96 or 104 or 112 or 120 or 128 or
        136 or 144 or 152 or 160 or 176 or 192 or 200 or 224 or 256;
}
