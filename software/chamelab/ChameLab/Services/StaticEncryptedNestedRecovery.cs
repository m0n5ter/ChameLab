using System.Collections.Generic;
using System.Threading;
using ChameLab.Models;
using ChameLab.Crypto;

namespace ChameLab.Services;

/// <summary>Managed ports of staticnested_1nt and the FM11RF08S seed filters in software/src.</summary>
public static class StaticEncryptedNestedRecovery
{
    public static IReadOnlyList<ulong> Recover(uint uid, NestedNonce nonce, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var keys = new HashSet<ulong>();
        var input = uid ^ nonce.Nt;
        var encryptedParity = (nonce.Parity & 1) ^ Crypto1.OddParity8((byte)nonce.NtEnc);
        foreach (var recovered in Crapto1.LfsrRecovery32(nonce.Nt ^ nonce.NtEnc, input))
        {
            ct.ThrowIfCancellationRequested();
            var state = recovered;
            if (state is { Odd: 0, Even: 0 }) continue;
            // The state is immediately after nT, so the next keystream bit encrypts its last parity bit.
            var next = state;
            var parityStream = (next.Crypto1Word(0, false) >> 24) & 1;
            if (Crypto1.OddParity8((byte)nonce.Nt) != (encryptedParity ^ parityStream)) continue;
            state.LfsrRollbackWord(input, false);
            keys.Add(state.Lfsr);
        }
        return new List<ulong>(keys);
    }

    private static readonly ushort[] Previous = BuildPrevious();
    private static readonly byte[] A = [0, 8, 9, 4, 6, 11, 1, 15, 12, 5, 2, 13, 10, 14, 3, 7];
    private static readonly byte[] B = [0, 13, 1, 14, 4, 10, 15, 7, 5, 3, 8, 6, 9, 2, 12, 11];

    public static ushort Seed(uint nt, ulong key)
    {
        var value = (ushort)(nt >> 16);
        for (var i = 0; i < 14; i++) value = Previous[value];
        for (var i = 0; i < 6; i++)
        {
            var lo = (int)((key >> (8 * i)) & 15);
            var hi = (int)((key >> (8 * i + 4)) & 15);
            value ^= (ushort)((i % 2 == 0 ? A[lo] : B[lo]) | ((i % 2 == 0 ? B[hi] : A[hi]) << 4));
            for (var j = 0; j < 8; j++) value = Previous[value];
        }
        return value;
    }

    public static IReadOnlyList<ulong> Filter(uint nt, IReadOnlyList<ulong> keys,
        uint otherNt, IReadOnlyList<ulong> otherKeys, CancellationToken ct = default)
    {
        // Equal nonces do not satisfy the RF08S pair-filter precondition.
        ct.ThrowIfCancellationRequested();
        if (nt == otherNt) return keys;
        var seeds = new HashSet<ushort>();
        foreach (var key in otherKeys) { ct.ThrowIfCancellationRequested(); seeds.Add(Seed(otherNt, key)); }
        var result = new List<ulong>();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (seeds.Contains(Seed(nt, key))) result.Add(key);
        }
        return result;
    }

    private static ushort[] BuildPrevious()
    {
        var result = new ushort[65536];
        ushort x = 1;
        for (var i = 0; i < 65535; i++)
        {
            var next = (ushort)((x >> 1) | ((x ^ (x >> 2) ^ (x >> 3) ^ (x >> 5)) << 15));
            result[(ushort)((next << 8) | (next >> 8))] = (ushort)((x << 8) | (x >> 8));
            x = next;
        }
        // Match the reference's inverse-table underflow for a zero nonce.
        result[0] = result[0x0100];
        return result;
    }
}
