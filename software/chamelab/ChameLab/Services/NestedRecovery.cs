using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Crypto;

namespace ChameLab.Services;

/// <summary>
/// MIFARE Classic "nested" key recovery for weak-PRNG cards, ported from the pm3
/// reference (<c>nested.c</c> / <c>nested_util.c</c>, as wrapped by ChameleonUltraGUI's
/// <c>recovery.c</c>) onto the crapto1 primitives in <see cref="ChameLab.Crypto"/>.
///
/// One sector key must already be known; the firmware collects two (nt, ntEnc, parity)
/// samples of the target sector while authenticating through the known one, and this turns
/// them into candidate keys. The caller verifies each candidate by authenticating on the card.
/// </summary>
public static class NestedRecovery
{
    /// <summary>
    /// Recovers candidate 48-bit keys from two nested nonce samples. <paramref name="dist"/>
    /// is the PRNG distance from the known key's nonce (device <c>Mf1DetectNtDist</c>); the
    /// <c>par</c> bytes carry the three low parity bits of each sample. Candidates are ordered
    /// most-frequent first; try them in that order.
    /// </summary>
    /// <remarks>
    /// Uses all available logical processors, up to the number of independent
    /// nonce candidates. Browser runtimes execute the same algorithm sequentially.
    /// </remarks>
    public static IReadOnlyList<ulong> Recover(
        uint uid, uint dist,
        uint nt0, uint nt0Enc, byte par0,
        uint nt1, uint nt1Enc, byte par1,
        CancellationToken ct = default)
        => RecoverCore(uid, dist, nt0, nt0Enc, par0, nt1, nt1Enc, par1, ct, Environment.ProcessorCount);

    /// <summary>Runs the same recovery with a bounded number of independent workers.</summary>
    internal static IReadOnlyList<ulong> RecoverCore(
        uint uid, uint dist,
        uint nt0, uint nt0Enc, byte par0,
        uint nt1, uint nt1Enc, byte par1,
        CancellationToken ct, int maxDegreeOfParallelism)
    {
        ct.ThrowIfCancellationRequested();
        var pnk = new List<(uint Ntp, uint Ks1)>();

        for (var i = 0; i < 2; i++)
        {
            var nt = i == 0 ? nt0 : nt1;
            var ntEnc = i == 0 ? nt0Enc : nt1Enc;
            var parInt = i == 0 ? par0 : par1;

            // Three low parity bits of this sample.
            var par = new byte[3];
            for (var m = 0; m < 3; m++) par[m] = (byte)((parInt >> m) & 1);

            // Sweep +/-14 PRNG steps around the measured distance and keep nonces whose
            // predicted keystream parity matches the observed encrypted parity.
            var ntTest = Crypto1.PrngSuccessor(nt, (int)dist - 14);
            for (var m = (int)dist - 14; m <= (int)dist + 14; m++)
            {
                var ks1 = ntEnc ^ ntTest;
                if (ValidNonce(ntTest, ntEnc, ks1, par))
                    pnk.Add((ntTest, ks1));
                ntTest = Crypto1.PrngSuccessor(ntTest, 1);
            }
        }

        return NestedCore.Run(pnk, uid, ct, maxDegreeOfParallelism);
    }

    /// <summary>
    /// Whether a candidate recovered nonce is consistent with the observed encrypted parity
    /// bits (pm3 <c>valid_nonce</c>): each byte's odd parity must equal the encrypted parity
    /// XOR the matching keystream bit.
    /// </summary>
    internal static bool ValidNonce(uint nt, uint ntEnc, uint ks1, byte[] parity) =>
        Crypto1.OddParity8((byte)((nt >> 24) & 0xFF)) == (parity[0] ^ Crypto1.OddParity8((byte)((ntEnc >> 24) & 0xFF)) ^ (int)((ks1 >> 16) & 1)) &&
        Crypto1.OddParity8((byte)((nt >> 16) & 0xFF)) == (parity[1] ^ Crypto1.OddParity8((byte)((ntEnc >> 16) & 0xFF)) ^ (int)((ks1 >> 8) & 1)) &&
        Crypto1.OddParity8((byte)((nt >> 8) & 0xFF)) == (parity[2] ^ Crypto1.OddParity8((byte)((ntEnc >> 8) & 0xFF)) ^ (int)(ks1 & 1));
}

/// <summary>
/// Shared back half of the nested attacks (pm3 <c>nested_recover</c> + <c>most_frequent</c>):
/// turn (ntp, ks1) samples into LFSR states, roll them back over the plain nonce to the key,
/// and return up to 50 keys occurring at least twice, ordered by frequency.
/// </summary>
internal static class NestedCore
{
    public static IReadOnlyList<ulong> Run(IReadOnlyList<(uint Ntp, uint Ks1)> pnk, uint authUid,
        CancellationToken ct = default, int maxDegreeOfParallelism = int.MaxValue)
    {
        ct.ThrowIfCancellationRequested();
        if (pnk.Count == 0) return Array.Empty<ulong>();
        if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        var workers = Math.Min(pnk.Count, OperatingSystem.IsBrowser() ? 1 :
            Math.Min(Environment.ProcessorCount, maxDegreeOfParallelism));
        var batches = new List<ulong>[workers];
        var nextSample = -1;

        void RecoverBatch(int worker)
        {
            using var workspace = new Crypto1RecoveryWorkspace();
            var scratch = ArrayPool<uint>.Shared.Rent(Crypto1RecoveryWorkspace.Recovery32BufferLength);
            var keys = new List<ulong>();
            ulong inputContribution = 0;
            bool Emit(Crypto1State state)
            {
                ct.ThrowIfCancellationRequested();
                if (state is { Odd: 0, Even: 0 }) return true;
                keys.Add(NestedKeyTransform.GetKey(state, inputContribution));
                return true;
            }
            workspace.Enter();
            try
            {
                int i;
                while ((i = Interlocked.Increment(ref nextSample)) < pnk.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    var (ntp, ks1) = pnk[i];
                    var seed = ntp ^ authUid;
                    inputContribution = NestedKeyTransform.GetInputContribution(seed);
                    // This sink consumes states immediately; ordering is immaterial.
                    Crapto1.Recover32Core(ks1, seed, workspace, Emit, scratch);
                }
                batches[worker] = keys;
            }
            finally
            {
                workspace.Exit();
                Array.Clear(scratch);
                ArrayPool<uint>.Shared.Return(scratch);
            }
        }

        if (workers == 1) RecoverBatch(0);
        else Parallel.For(0, workers, new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = ct
        }, RecoverBatch);

        ct.ThrowIfCancellationRequested();
        var length = batches.Sum(batch => batch.Count);
        if (length == 0) return Array.Empty<ulong>();
        var allKeys = new ulong[length];
        var offset = 0;
        foreach (var batch in batches)
        {
            batch.CopyTo(allKeys, offset);
            offset += batch.Count;
        }
        Sort48BitKeys(allKeys, ct);
        var repeated = new List<(ulong Key, int Count)>();
        for (var i = 0; i < allKeys.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var end = i + 1;
            while (end < allKeys.Length && allKeys[end] == allKeys[i]) end++;
            if (end - i > 1) repeated.Add((allKeys[i], end - i));
            i = end;
        }

        // C nested_util.c counts repetitions after the first occurrence (count > 0)
        // and tries the first 50. A less frequent repeated key can still be correct;
        // a list of singletons means the captures did not corroborate a key.
        return repeated.OrderByDescending(kv => kv.Count)
            .ThenBy(kv => kv.Key)
            .Take(50)
            .Select(kv => kv.Key)
            .ToArray();
    }

    /// <summary>Sorts 48-bit candidate keys with six linear byte-radix passes.</summary>
    internal static void Sort48BitKeys(ulong[] keys, CancellationToken ct = default)
    {
        if (keys.Length < 8192)
        {
            ct.ThrowIfCancellationRequested();
            Array.Sort(keys);
            return;
        }
        var scratch = ArrayPool<ulong>.Shared.Rent(keys.Length);
        try
        {
            Span<int> counts = stackalloc int[256];
            var source = keys;
            var target = scratch;
            for (var shift = 0; shift < 48; shift += 8)
            {
                ct.ThrowIfCancellationRequested();
                counts.Clear();
                for (var i = 0; i < keys.Length; i++) counts[(int)((source[i] >> shift) & 255)]++;
                var offset = 0;
                for (var digit = 0; digit < counts.Length; digit++)
                {
                    var count = counts[digit];
                    counts[digit] = offset;
                    offset += count;
                }
                for (var i = 0; i < keys.Length; i++)
                {
                    var key = source[i];
                    target[counts[(int)((key >> shift) & 255)]++] = key;
                }
                (source, target) = (target, source);
            }
            // An even number of passes leaves the result in the original array.
        }
        finally
        {
            Array.Clear(scratch, 0, keys.Length);
            ArrayPool<ulong>.Shared.Return(scratch);
        }
    }
}

/// <summary>
/// The plaintext rollback and key-bit permutation form a linear map over GF(2).
/// Ten byte tables replace a 32-bit rollback and a 48-bit permutation per state;
/// the input's four contributions are calculated once per nonce candidate.
/// </summary>
internal static class NestedKeyTransform
{
    private static readonly ulong[][] Tables = CreateTables();

    internal static ulong GetInputContribution(uint input) =>
        Tables[6][(byte)input] ^ Tables[7][(byte)(input >> 8)] ^
        Tables[8][(byte)(input >> 16)] ^ Tables[9][(byte)(input >> 24)];

    internal static ulong GetKey(Crypto1State state, ulong inputContribution) =>
        inputContribution ^
        Tables[0][(byte)state.Odd] ^ Tables[1][(byte)(state.Odd >> 8)] ^
        Tables[2][(byte)(state.Odd >> 16)] ^ Tables[3][(byte)state.Even] ^
        Tables[4][(byte)(state.Even >> 8)] ^ Tables[5][(byte)(state.Even >> 16)];

    private static ulong[][] CreateTables()
    {
        var tables = new ulong[10][];
        Span<ulong> basis = stackalloc ulong[8];
        for (var component = 0; component < tables.Length; component++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var state = new Crypto1State();
                uint input = 0;
                if (component < 3) state.Odd = 1u << (component * 8 + bit);
                else if (component < 6) state.Even = 1u << ((component - 3) * 8 + bit);
                else input = 1u << ((component - 6) * 8 + bit);
                state.LfsrRollbackWord(input, false);
                basis[bit] = state.Lfsr;
            }
            var table = tables[component] = new ulong[256];
            for (var value = 1; value < table.Length; value++)
                table[value] = table[value & (value - 1)] ^
                    basis[System.Numerics.BitOperations.TrailingZeroCount((uint)value)];
        }
        return tables;
    }
}
