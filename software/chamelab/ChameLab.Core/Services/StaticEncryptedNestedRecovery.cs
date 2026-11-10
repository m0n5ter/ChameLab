using System;
using System.Buffers;
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
        var parityStream = (nonce.Parity & 1) ^ Crypto1.OddParity8((byte)nonce.NtEnc) ^
            Crypto1.OddParity8((byte)nonce.Nt);
        var inputContribution = NestedKeyTransform.GetInputContribution(input);
        var workers = OperatingSystem.IsBrowser() ? 1 : Math.Min(8, Environment.ProcessorCount);
        var batches = new HashSet<ulong>[workers];
        var sinks = new Func<Crypto1State, bool>[workers];
        for (var worker = 0; worker < workers; worker++)
        {
            var batch = batches[worker] = worker == 0 ? keys : new HashSet<ulong>();
            sinks[worker] = state =>
            {
                ct.ThrowIfCancellationRequested();
                if (state is not { Odd: 0, Even: 0 } && Crypto1.Filter(state.Odd) == parityStream)
                    batch.Add(NestedKeyTransform.GetKey(state, inputContribution));
                return true;
            };
        }
        using var workspace = new Crypto1RecoveryWorkspace();
        var scratch = ArrayPool<uint>.Shared.Rent(Crypto1RecoveryWorkspace.Recovery32BufferLength);
        workspace.Enter();
        try
        {
            // Consume states directly; contribution grouping avoids comparison sorting.
            // The first filter bit after nT encrypts its last parity bit: no clocking is needed.
            Crapto1.Recover32Core(nonce.Nt ^ nonce.NtEnc, input, workspace, sinks[0], scratch,
                parallel: workers > 1, parallelSinks: workers > 1 ? sinks : null, ct: ct);
        }
        finally
        {
            workspace.Exit();
            Array.Clear(scratch);
            ArrayPool<uint>.Shared.Return(scratch);
        }
        ct.ThrowIfCancellationRequested();
        for (var worker = 1; worker < workers; worker++) keys.UnionWith(batches[worker]);
        return new List<ulong>(keys);
    }

    private static readonly ushort[] Previous = BuildPrevious();
    private static readonly ushort[] Previous8 = BuildPrevious8();
    private static readonly byte[] A = [0, 8, 9, 4, 6, 11, 1, 15, 12, 5, 2, 13, 10, 14, 3, 7];
    private static readonly byte[] B = [0, 13, 1, 14, 4, 10, 15, 7, 5, 3, 8, 6, 9, 2, 12, 11];
    private static readonly byte[] MixEven = BuildKeyMix(false);
    private static readonly byte[] MixOdd = BuildKeyMix(true);

    private static byte[] BuildKeyMix(bool odd)
    {
        var result = new byte[256];
        for (var value = 0; value < result.Length; value++)
            result[value] = (byte)(odd ? B[value & 15] | (A[value >> 4] << 4)
                : A[value & 15] | (B[value >> 4] << 4));
        return result;
    }

    public static ushort Seed(uint nt, ulong key) => SeedFromNonce(RollbackNonce(nt), key);

    private static ushort RollbackNonce(uint nt)
    {
        var value = (ushort)(nt >> 16);
        for (var i = 0; i < 14; i++) value = Previous[value];
        return value;
    }

    private static ushort SeedFromNonce(ushort value, ulong key)
    {
        for (var i = 0; i < 3; i++)
        {
            value = Previous8[value ^ MixEven[(byte)key]];
            key >>= 8;
            value = Previous8[value ^ MixOdd[(byte)key]];
            key >>= 8;
        }
        return value;
    }

    public static IReadOnlyList<ulong> Filter(uint nt, IReadOnlyList<ulong> keys,
        uint otherNt, IReadOnlyList<ulong> otherKeys, CancellationToken ct = default)
    {
        // Equal nonces do not satisfy the RF08S pair-filter precondition.
        ct.ThrowIfCancellationRequested();
        if (nt == otherNt) return keys;
        if (keys.Count == 0 || otherKeys.Count == 0) return Array.Empty<ulong>();
        if (otherKeys.Count == 1)
        {
            var seed = Seed(otherNt, otherKeys[0]);
            var initial = RollbackNonce(nt);
            var filtered = new List<ulong>();
            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();
                if (SeedFromNonce(initial, key) == seed) filtered.Add(key);
            }
            return filtered;
        }
        var seeds = new bool[1 << 16];
        var otherNonce = RollbackNonce(otherNt);
        foreach (var key in otherKeys)
        {
            ct.ThrowIfCancellationRequested();
            seeds[SeedFromNonce(otherNonce, key)] = true;
        }
        var nonce = RollbackNonce(nt);
        var result = new List<ulong>();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (seeds[SeedFromNonce(nonce, key)]) result.Add(key);
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

    private static ushort[] BuildPrevious8()
    {
        // Compose the actual transition, including its special zero behavior.
        var result = new ushort[1 << 16];
        for (var i = 0; i < result.Length; i++)
        {
            var value = (ushort)i;
            for (var j = 0; j < 8; j++) value = Previous[value];
            result[i] = value;
        }
        return result;
    }
}
