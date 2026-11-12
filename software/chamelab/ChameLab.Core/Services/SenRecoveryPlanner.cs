using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

/// <summary>A bounded working set of SEN candidate paths. Recalculate their value after every confirmed key.</summary>
internal sealed class SenRecoveryPlanner
{
    internal sealed record Path(int Sector, MfKeyType Type, NestedNonce Nonce, IReadOnlyList<ulong> Keys,
        int PreferredCount = 0, double CascadeBenefit = 1);
    private readonly Dictionary<(int, MfKeyType), Path> _paths = [];
    internal int Capacity { get; } = OperatingSystem.IsBrowser() ? 2 : OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? 4 : 8;
    internal long ByteBudget { get; } = OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? 16L * 1024 * 1024 : 64L * 1024 * 1024;
    internal IEnumerable<Path> Paths => _paths.Values;
    internal bool HasRoom => _paths.Count < Capacity && _paths.Values.Sum(p => (long)p.Keys.Count * 32) < ByteBudget;
    internal bool Contains(int sector, MfKeyType type) => _paths.ContainsKey((sector, type));
    internal void Add(Path path) => _paths[(path.Sector, path.Type)] = path;
    internal void Remove(int sector, MfKeyType type) => _paths.Remove((sector, type));

    internal Path? Next(MifareDump dump, MifareRecoverySession session)
    {
        foreach (var p in _paths.Values.ToArray())
            if ((p.Type == MfKeyType.A ? dump.KeyA : dump.KeyB)[p.Sector] is not null) Remove(p.Sector, p.Type);
        return _paths.Values.OrderBy(p => session.EstimateCandidateMilliseconds(p.PreferredCount > 0 ? p.PreferredCount : p.Keys.Count)
            / Math.Max(1, p.CascadeBenefit)).ThenBy(p => p.Keys.Count).ThenBy(p => p.Sector).FirstOrDefault();
    }

    internal IReadOnlyList<ulong> Prioritize(Path path)
    {
        var counts = new Dictionary<ulong, int>();
        foreach (var p in _paths.Values)
            foreach (var key in p.Keys) counts[key] = counts.GetValueOrDefault(key) + 1;
        return path.Keys.OrderByDescending(k => counts.GetValueOrDefault(k)).ToArray();
    }
}
