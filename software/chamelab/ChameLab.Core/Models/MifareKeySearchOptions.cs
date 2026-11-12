using System;
using System.Linq;

namespace ChameLab.Models;

public enum MifareKeySearchMethod { SingleKey, AllSectors, OneSector }
public enum MifareKeySearchStart { Beginning, Selected }
public enum MifareKeySearchOrder { Sequential, Random }

/// <summary>Immutable choices for one dictionary search. Selection belongs to the current card.</summary>
public sealed record MifareKeySearchOptions(
    MifareKeySearchMethod Method = MifareKeySearchMethod.AllSectors,
    MifareKeySearchStart Start = MifareKeySearchStart.Beginning,
    MifareKeySearchOrder Order = MifareKeySearchOrder.Sequential,
    int SelectedSector = 0)
{
    /// <summary>Return after the first successful authentication, once the current firmware reply is drained.</summary>
    public bool StopAfterFirstKey { get; init; }
    /// <summary>Optional shared ledger used by the automatic recovery planner.</summary>
    public MifareRecoverySession? Session { get; init; }

    public int[] GetSectorOrder(int sectorCount)
    {
        if (sectorCount <= 0) return [];
        var first = Start == MifareKeySearchStart.Selected && SelectedSector >= 0 && SelectedSector < sectorCount
            ? SelectedSector : 0;
        var rest = Enumerable.Range(1, sectorCount - 1).Select(offset => (first + offset) % sectorCount).ToArray();
        if (Order == MifareKeySearchOrder.Random) System.Random.Shared.Shuffle(rest);
        return [first, .. rest];
    }
}
