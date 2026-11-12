using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class SenReferenceTests
{
    [Fact]
    public void CandidateMembershipOrderAndPathCostMatchProxmark3C()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reference", "sen_order_vectors.json")));
        foreach (var vector in vectors.RootElement.EnumerateArray())
        {
            ulong state = 123456789;
            ulong Next()
            {
                state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
                return (state >> 16) & 0xFFFFFFFFFFFFUL;
            }
            var a = Enumerable.Range(0, vector.GetProperty("Na").GetInt32()).Select(_ => Next()).ToArray();
            var b = Enumerable.Range(0, vector.GetProperty("Nb").GetInt32()).Select(_ => Next()).ToArray();
            var ntA = vector.GetProperty("NtA").GetUInt32();
            var ntB = vector.GetProperty("NtB").GetUInt32();
            var defaults = vector.GetProperty("Preferred").EnumerateArray().Select(k => Convert.ToUInt64(k.GetString(), 16)).ToArray();
            var orderedA = KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.OrderStaticEncrypted)
                { Nonces = [new(ntA, 0, 0), new(ntB, 0, 0)], Keys = a, OtherKeys = b, PreferredKeys = defaults });
            var orderedB = KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.OrderStaticEncrypted)
                { Nonces = [new(ntB, 0, 0), new(ntA, 0, 0)], Keys = b, OtherKeys = a, PreferredKeys = defaults });
            Assert.Equal(vector.GetProperty("CountA").GetInt32(), orderedA.Count);
            Assert.Equal(vector.GetProperty("CountB").GetInt32(), orderedB.Count);
            static string Hash(System.Collections.Generic.IReadOnlyList<ulong> keys)
            {
                var hash = 14695981039346656037UL;
                foreach (var key in keys)
                    for (var j = 0; j < 6; j++) hash = unchecked((hash ^ ((key >> (8 * j)) & 255)) * 1099511628211UL);
                return hash.ToString("X16");
            }
            Assert.Equal(vector.GetProperty("HashA").GetString(), Hash(orderedA));
            Assert.Equal(vector.GetProperty("HashB").GetString(), Hash(orderedB));
            var planner = new SenRecoveryPlanner();
            var pa = new SenRecoveryPlanner.Path(0, MfKeyType.A, new(ntA, 0, 0), orderedA);
            var pb = new SenRecoveryPlanner.Path(0, MfKeyType.B, new(ntB, 0, 0), orderedB);
            planner.Add(pa); planner.Add(pb);
            Assert.Equal(Math.Min(int.MaxValue, vector.GetProperty("CostA").GetInt64()), planner.Cost(pa));
            Assert.Equal(Math.Min(int.MaxValue, vector.GetProperty("CostB").GetInt64()), planner.Cost(pb));
        }
    }

    [Fact]
    public void ReusePriorityCountsDistinctSectorsThenSlotsAndDuplicatePartitionUsesSwaps()
    {
        var planner = new SenRecoveryPlanner();
        planner.Add(new(0, MfKeyType.A, new(0, 0, 0), [9, 8, 30, 20]));
        planner.Add(new(0, MfKeyType.B, new(0, 0, 0), [30, 40]));
        planner.Add(new(4, MfKeyType.A, new(0, 0, 0), [20, 40, 30]));
        planner.Add(new(32, MfKeyType.B, new(0, 0, 0), [40]));
        var priority = planner.PrioritizeGlobal();
        Assert.Equal(new ulong[] { 40, 30, 20 }, priority.Reuse);
        Assert.Equal(new ulong[] { 30, 20, 40 }, priority.Duplicates);
        Assert.Equal(new ulong[] { 30, 20, 9, 8 }, planner.Get(0, MfKeyType.A)!.Keys);
    }

    [Fact]
    public void HiddenSectorBIsPreferredToHiddenSectorAAndExhaustedPathsAreSkipped()
    {
        var planner = new SenRecoveryPlanner();
        var session = new MifareRecoverySession();
        planner.Add(new(32, MfKeyType.A, new(0, 0, 0), [1, 2]));
        planner.Add(new(32, MfKeyType.B, new(0, 0, 0), [3, 4]));
        Assert.Equal(MfKeyType.B, planner.Next(session)!.Type);
        session.SenExhausted.Add((32, MfKeyType.B));
        Assert.Equal(MfKeyType.A, planner.Next(session)!.Type);
    }
}
