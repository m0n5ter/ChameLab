// Derived from Proxmark3, GPL-3.0-or-later. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Crypto.Hardnested;

internal sealed class HardnestedSearch
{
    internal readonly HardnestedCapture Capture;
    internal readonly int FirstByte;
    internal readonly uint[] Nonces;
    internal readonly byte[] Parities;
    internal readonly int[] CommonBits;
    internal readonly CancellationToken Cancellation;
    private long found = -1;
    internal bool Stopped => Interlocked.Read(ref found) >= 0;

    internal HardnestedSearch(HardnestedCapture capture, int firstByte, HardnestedSample[] nonces, CancellationToken ct)
    {
        Capture = capture;
        FirstByte = firstByte;
        Cancellation = ct;
        // Visit a binary trie in transmission order, so consecutive nonces share
        // their first bits. Keep independent second bytes to constrain the state.
        var ordered = nonces.OrderBy(n => Reverse((byte)(n.EncryptedNonce >> 16))).Take(16).ToArray();
        Nonces = ordered.Select(n => n.EncryptedNonce ^ capture.AuthenticationUid).ToArray();
        byte uidParity = 0;
        for (int shift = 24; shift >= 0; shift -= 8)
            uidParity = (byte)((uidParity << 1) | Crypto1.OddParity8((byte)(capture.AuthenticationUid >> shift)));
        Parities = ordered.Select(n => (byte)(n.EncryptedParity ^ uidParity)).ToArray();
        CommonBits = new int[Nonces.Length];
        for (int i = 1; i < Nonces.Length; i++)
            CommonBits[i] = Math.Min(7, BitOperations.TrailingZeroCount((Nonces[i] ^ Nonces[i - 1]) >> 16));
    }

    private static int Reverse(byte b) => (int)((b * 0x0202020202UL & 0x010884422010UL) % 1023);

    internal bool Verify(uint odd, uint even)
    {
        var initial = new Crypto1State(odd, even);
        initial.LfsrRollbackByte((byte)((Capture.AuthenticationUid >> 24) ^ FirstByte), true);
        if (!VerifyKey(initial, Capture, Cancellation)) return false;
        Interlocked.CompareExchange(ref found, (long)initial.Lfsr, -1);
        return true;
    }

    internal static bool VerifyKey(Crypto1State initial, HardnestedCapture capture, CancellationToken ct = default)
    {
        int tested = 0;
        foreach (var sample in capture.Samples)
        {
            if ((tested++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var state = initial;
            for (int shift = 24; shift >= 0; shift -= 8)
            {
                byte encrypted = (byte)(sample.EncryptedNonce >> shift);
                byte plain = (byte)(encrypted ^ state.Crypto1Byte((byte)(encrypted ^ (capture.AuthenticationUid >> shift)), true));
                int parity = Crypto1.OddParity8(plain) ^ Crypto1.Filter(state.Odd);
                if (parity != ((sample.EncryptedParity >> (shift / 8)) & 1)) return false;
            }
        }
        return true;
    }

    internal ulong? Run(uint[][][] candidates, int sum0, IProgress<string>? progress,
        Func<int, uint[], int, int, uint[]>? refine = null, bool scalar = false, double[]? prior = null)
    {
        int observed = 0;
        // Sum(a8) uses all distinct second bytes, supplied separately by Run below.
        foreach (var sample in Capture.Samples.Where(s => (s.EncryptedNonce >> 24) == FirstByte)
                     .DistinctBy(s => (s.EncryptedNonce >> 16) & 255))
        {
            observed += BitOperations.PopCount(((sample.EncryptedNonce >> 16) & 255) ^ (uint)((sample.EncryptedParity >> 2) & 1)) & 1;
        }
        int n = Capture.Samples.Where(s => (s.EncryptedNonce >> 24) == FirstByte).Select(s => (s.EncryptedNonce >> 16) & 255).Distinct().Count();
        var guesses = new List<(int Sum, double Score, List<(uint[] Odd, uint[] Even)> Work)>();
        foreach (int sum8 in HardnestedTables.Sums)
        {
            double likelihood = Hypergeometric(sum8, n, observed);
            if (likelihood == 0) continue;
            var work = new List<(uint[] Odd, uint[] Even)>();
            long count = 0;
            for (int p = 0; p < 9; p++)
                for (int q = 0; q < 9; q++)
                {
                    if (HardnestedTables.Sum(p, q) != sum0) continue;
                    for (int r = 0; r < 9; r++)
                        for (int s = 0; s < 9; s++)
                        {
                            if (HardnestedTables.Sum(r, s) != sum8) continue;
                            var odd = candidates[1][p * 9 + r];
                            var even = candidates[0][q * 9 + s];
                            if (odd.Length == 0 || even.Length == 0) continue;
                            count += (long)odd.Length * even.Length;
                            work.Add((odd, even));
                        }
                }
            if (count != 0) guesses.Add((sum8, likelihood * (prior is null ? 1 : prior[Array.IndexOf(HardnestedTables.Sums, sum8)]), work));
        }
        var cache = new Dictionary<(int Half, uint[] States), uint[]>();
        var parallel = new ParallelOptions { CancellationToken = Cancellation,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 16)) };
        foreach (var guess in guesses.OrderByDescending(g => g.Score))
        {
            Cancellation.ThrowIfCancellationRequested();
            progress?.Report($"Matching hardnested states: Sum(a8) = {guess.Sum}…");
            // Cross-byte matching is expensive. Refine only classes used by the
            // current guess, retaining them for later guesses, as upstream does.
            var needed = guess.Work.SelectMany(w => new[] { (Half: 1, States: w.Odd), (Half: 0, States: w.Even) })
                .Distinct().Where(w => !cache.ContainsKey(w)).ToArray();
            if (refine is null)
                foreach (var item in needed) cache[item] = item.States;
            else if (OperatingSystem.IsBrowser() || needed.Sum(w => w.States.Length) < 1024)
                foreach (var item in needed) cache[item] = refine(item.Half, item.States, 0, item.States.Length);
            else
            {
                // A sum class can contain millions of states. Flatten its ranges
                // into the same bounded worker budget as the rest of matching.
                const int blockSize = 4096;
                var blocks = needed.ToDictionary(w => w, w => new uint[(w.States.Length + blockSize - 1) / blockSize][]);
                var matching = needed.SelectMany(w => Enumerable.Range(0, blocks[w].Length)
                    .Select(i => (Item: w, Part: i, Start: i * blockSize, End: Math.Min(w.States.Length, (i + 1) * blockSize))));
                Parallel.ForEach(matching, parallel, job =>
                    blocks[job.Item][job.Part] = refine(job.Item.Half, job.Item.States, job.Start, job.End));
                foreach (var item in needed)
                {
                    var parts = blocks[item];
                    if (parts.Length == 1) { cache[item] = parts[0]; continue; }
                    var merged = new uint[parts.Sum(p => p.Length)];
                    int offset = 0;
                    foreach (var part in parts) { part.CopyTo(merged, offset); offset += part.Length; }
                    cache[item] = merged;
                }
            }
            progress?.Report($"Searching hardnested candidates: Sum(a8) = {guess.Sum}…");
            // Bounded odd-state buckets distribute even a single large sum class.
            var jobs = guess.Work.Select(w => (Odd: cache[(1, w.Odd)], Even: cache[(0, w.Even)]))
                .Where(w => w.Odd.Length != 0 && w.Even.Length != 0)
                .SelectMany(w => Enumerable.Range(0, (w.Odd.Length + 31) / 32)
                .Select(i => (w.Odd, w.Even, Start: i * 32, End: Math.Min(w.Odd.Length, (i + 1) * 32))));
            void Search((uint[] Odd, uint[] Even, int Start, int End) job)
            {
                if (Stopped) return;
                if (scalar || !Vector.IsHardwareAccelerated) HardnestedScalar.Search(this, job.Odd, job.Even, job.Start, job.End);
                else HardnestedVector.Search(this, job.Odd, job.Even, job.Start, job.End);
            }
            if (OperatingSystem.IsBrowser())
            {
                foreach (var job in jobs) { Search(job); if (Stopped) break; }
            }
            else Parallel.ForEach(jobs, parallel, Search);
            if (Stopped) return (ulong)Interlocked.Read(ref found);
        }
        return null;
    }

    internal static double Hypergeometric(int populationOnes, int drawn, int observed)
    {
        if (populationOnes is < 0 or > 256 || drawn is < 0 or > 256 || observed < 0 || observed > drawn ||
            observed > populationOnes || drawn - observed > 256 - populationOnes) return 0;
        return Math.Exp(LogChoose(populationOnes, observed) + LogChoose(256 - populationOnes, drawn - observed) - LogChoose(256, drawn));
    }

    private static readonly double[] LogFactorials = BuildLogFactorials();

    private static double[] BuildLogFactorials()
    {
        var values = new double[257];
        for (int i = 2; i < values.Length; i++) values[i] = values[i - 1] + Math.Log(i);
        return values;
    }

    private static double LogChoose(int n, int k) => LogFactorials[n] - LogFactorials[k] - LogFactorials[n - k];
}
