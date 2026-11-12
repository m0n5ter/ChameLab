using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

// Port of Proxmark3 cmdhfmfsen.c (GPLv3), c7b80e1bc89ed03983832f93b1d2b5f980c8bc9b:
// fm11_prioritize_duplicates, fm11_build_reuse_priority_keys and
// fm11_estimate_candidate_path_cost. Keep all sector lists available to these steps.
internal sealed class SenRecoveryPlanner
{
    internal const int GlobalPriorityLimit = 8000;
    internal sealed record Path(int Sector, MfKeyType Type, NestedNonce Nonce, IReadOnlyList<ulong> Keys)
    {
        internal ulong? KnownKey { get; init; }
        private ushort[]? _seeds;
        internal ushort[] Seeds => _seeds ??= Keys.Select(k => StaticEncryptedNestedRecovery.Seed(Nonce.Nt, k)).ToArray();
    }
    private readonly Dictionary<(int, MfKeyType), Path> _paths = [];
    internal IEnumerable<Path> Paths => _paths.Values;
    internal Path? Get(int sector, MfKeyType type) => _paths.GetValueOrDefault((sector, type));
    internal void Add(Path path) => _paths[(path.Sector, path.Type)] = path;
    internal void ReplaceKeys(Path path, IReadOnlyList<ulong> keys) =>
        Add(new(path.Sector, path.Type, path.Nonce, keys) { KnownKey = path.KnownKey });

    internal void UpdateKnown(Func<int, MfKeyType, ulong?> known)
    {
        foreach (var p in Paths.ToArray())
            if (known(p.Sector, p.Type) is { } key && p.KnownKey != key)
                Add(p with { KnownKey = key });
    }

    internal int Cost(Path path)
    {
        if (path.KnownKey is not null || path.Keys.Count == 0) return int.MaxValue;
        var other = Get(path.Sector, path.Type == MfKeyType.A ? MfKeyType.B : MfKeyType.A);
        if (other is null) return path.Keys.Count;
        if (other.KnownKey is { } known)
        {
            var seed = StaticEncryptedNestedRecovery.Seed(other.Nonce.Nt, known);
            for (var i = 0; i < path.Keys.Count; i++)
                if (path.Nonce.Nt == other.Nonce.Nt ? path.Keys[i] == known : path.Seeds[i] == seed) return i + 1;
        }
        else if (other.Keys.Count > 0 && other.Nonce.Nt != path.Nonce.Nt)
        {
            var counts = new int[65536];
            foreach (var seed in other.Seeds) counts[seed]++;
            for (var i = 0; i < path.Keys.Count; i++)
                if (counts[path.Seeds[i]] == 1) return i + 1;
        }
        return path.Keys.Count;
    }

    internal Path? Next(MifareRecoverySession session)
    {
        Path? best = null;
        var bestCost = int.MaxValue;
        foreach (var path in Paths.OrderBy(p => p.Sector).ThenBy(p => p.Type))
        {
            if (path.KnownKey is not null || path.Keys.Count == 0 || session.SenExhausted.Contains((path.Sector, path.Type))) continue;
            var cost = Cost(path);
            if (path.Sector == 32 && path.Type == MfKeyType.B && best?.Sector == 32) cost = Math.Max(0, bestCost - 1);
            if (best is null || cost < bestCost || cost == bestCost && path.Keys.Count < best.Keys.Count)
            { best = path; bestCost = cost; }
        }
        return best;
    }

    internal (IReadOnlyList<ulong> Reuse, IReadOnlyList<ulong> Duplicates) PrioritizeGlobal()
    {
        var paths = Paths.OrderBy(p => p.Sector).ThenBy(p => p.Type).ToArray();
        var total = paths.Sum(p => p.Keys.Count);
        var refs = new Dictionary<ulong, (ulong Sectors, int Count, int Ordinal)>();
        foreach (var p in paths)
            for (var i = 0; i < p.Keys.Count; i++)
            {
                var old = refs.GetValueOrDefault(p.Keys[i], (Sectors: 0UL, Count: 0, Ordinal: int.MaxValue));
                refs[p.Keys[i]] = (old.Sectors | (1UL << p.Sector), old.Count + 1, Math.Min(old.Ordinal, i));
            }
        var duplicateKeys = new List<ulong>();
        var seen = new HashSet<ulong>();
        // PM3 skips duplicate partitioning above this size, but still builds its reuse index.
        if (total <= GlobalPriorityLimit * 32)
            foreach (var p in paths)
            {
                var keys = p.Keys.ToArray();
                var dst = 0;
                for (var i = 0; i < keys.Length; i++)
                    if (refs[keys[i]].Count > 1)
                    {
                        if (seen.Add(keys[i])) duplicateKeys.Add(keys[i]);
                        (keys[dst], keys[i]) = (keys[i], keys[dst]);
                        dst++;
                    }
                ReplaceKeys(p, keys);
            }
        // Ordinals in the reuse index refer to the duplicate-partitioned lists.
        foreach (var key in refs.Keys.ToArray()) refs[key] = (refs[key].Sectors, refs[key].Count, int.MaxValue);
        foreach (var p in Paths)
            for (var i = 0; i < p.Keys.Count; i++)
            {
                var item = refs[p.Keys[i]];
                refs[p.Keys[i]] = (item.Sectors, item.Count, Math.Min(item.Ordinal, i));
            }
        var reuse = refs.Where(r => BitOperations.PopCount(r.Value.Sectors) >= 2)
            .OrderByDescending(r => BitOperations.PopCount(r.Value.Sectors)).ThenByDescending(r => r.Value.Count)
            .ThenBy(r => r.Value.Ordinal).ThenBy(r => r.Key).Take(GlobalPriorityLimit).Select(r => r.Key).ToArray();
        return (reuse, duplicateKeys.Count <= GlobalPriorityLimit ? duplicateKeys : []);
    }
}
