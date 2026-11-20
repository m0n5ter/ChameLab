using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Sync;
using Xunit;

namespace ChameLab.Tests.Sync;

public class SyncDocumentTests
{
    private static Dictionary<string, JsonNode?> Fields(params (string Name, JsonNode? Value)[] fields) =>
        fields.ToDictionary(f => f.Name, f => f.Value);

    /// <summary>Hands every operation of <paramref name="from"/> that <paramref name="to"/> lacks over, as a sync session does.</summary>
    private static void Send(SyncDocument from, SyncDocument to)
    {
        var ops = from.MissingFor(to.Vector);
        if (ops is null) { to.MergeSnapshot(from.Snapshot()); ops = from.MissingFor(to.Vector)!; }
        foreach (var op in ops) Assert.NotEqual(ApplyResult.Gap, to.Apply(op));
    }

    private static string State(SyncDocument doc) =>
        string.Join("\n", new[] { EntityTypes.Card, EntityTypes.CardFolder }
            .SelectMany(doc.View)
            .OrderBy(v => v.Id, StringComparer.Ordinal)
            .Select(v => $"{v.Id}={v.Fields.ToJsonString()}"));

    [Fact]
    public void FieldsMergePerFieldNewestWins()
    {
        var a = new SyncDocument("A");
        var b = new SyncDocument("B");
        a.Change(EntityTypes.Card, "c1", Fields(("name", "Office"), ("color", "#FF0000")));
        Send(a, b);
        a.Change(EntityTypes.Card, "c1", Fields(("name", "Office door")));
        b.Change(EntityTypes.Card, "c1", Fields(("color", "#00FF00")));
        Send(a, b);
        Send(b, a);
        Assert.Equal(State(a), State(b));
        var card = a.View(EntityTypes.Card).Single();
        Assert.Equal("Office door", (string?)card.Fields["name"]);
        Assert.Equal("#00FF00", (string?)card.Fields["color"]);
    }

    [Fact]
    public void ConcurrentDumpEditsKeepBoth()
    {
        var a = new SyncDocument("A");
        var b = new SyncDocument("B");
        a.Change(EntityTypes.Card, "c1", Fields(("name", "Gym"), ("data", "0")));
        Send(a, b);
        a.Change(EntityTypes.Card, "c1", Fields(("data", "A")));
        b.Change(EntityTypes.Card, "c1", Fields(("data", "B")));
        Send(a, b);
        Send(b, a);
        Assert.Equal(State(a), State(b));
        var views = a.View(EntityTypes.Card).ToList();
        Assert.Equal(2, views.Count);
        Assert.Equal(new[] { "A", "B" }, views.Select(v => (string?)v.Fields["data"]).Order());
        var copy = views.Single(v => v.CopyOf is not null);
        Assert.Equal("c1", copy.CopyOf);

        // Deleting the copy settles it everywhere; a later edit of the original does not bring it back.
        a.Delete(EntityTypes.Card, copy.Id);
        a.Change(EntityTypes.Card, "c1", Fields(("data", "C")));
        Send(a, b);
        Assert.Single(b.View(EntityTypes.Card));
        Assert.Equal(State(a), State(b));
    }

    [Fact]
    public void SequentialDumpEditsMakeNoCopy()
    {
        var a = new SyncDocument("A");
        var b = new SyncDocument("B");
        a.Change(EntityTypes.Card, "c1", Fields(("data", "0")));
        Send(a, b);
        b.Change(EntityTypes.Card, "c1", Fields(("data", "1")));
        Send(b, a);
        a.Change(EntityTypes.Card, "c1", Fields(("data", "2")));
        Send(a, b);
        Assert.Single(b.View(EntityTypes.Card));
        Assert.Equal("2", (string?)b.View(EntityTypes.Card).Single().Fields["data"]);
    }

    [Fact]
    public void EditConcurrentWithDeleteRevives()
    {
        var a = new SyncDocument("A");
        var b = new SyncDocument("B");
        a.Change(EntityTypes.Card, "c1", Fields(("name", "Gym")));
        Send(a, b);
        a.Delete(EntityTypes.Card, "c1");
        b.Change(EntityTypes.Card, "c1", Fields(("name", "Gym 2")));
        Send(a, b);
        Send(b, a);
        Assert.Equal("Gym 2", (string?)a.View(EntityTypes.Card).Single().Fields["name"]);
        Assert.Equal(State(a), State(b));

        // A delete that saw the edit sticks.
        b.Delete(EntityTypes.Card, "c1");
        Send(b, a);
        Assert.Empty(a.View(EntityTypes.Card));
        Assert.Single(a.Deleted(EntityTypes.Card));
    }

    [Fact]
    public void RestoringFromHistoryBringsOldValuesBack()
    {
        var a = new SyncDocument("A");
        a.Change(EntityTypes.Card, "c1", Fields(("name", "One"), ("data", "1")));
        var first = a.History(EntityTypes.Card, "c1").Single();
        a.Change(EntityTypes.Card, "c1", Fields(("name", "Two"), ("data", "2")));
        a.Delete(EntityTypes.Card, "c1");
        Assert.Empty(a.View(EntityTypes.Card));
        Assert.Equal(3, a.History(EntityTypes.Card, "c1").Count);

        var old = a.ValuesAt(EntityTypes.Card, "c1", first.Time);
        a.Change(EntityTypes.Card, "c1", old.ToDictionary(p => p.Key, p => p.Value));
        var card = a.View(EntityTypes.Card).Single();
        Assert.Equal("One", (string?)card.Fields["name"]);
        Assert.Equal("1", (string?)card.Fields["data"]);
    }

    [Fact]
    public void GapsAndDuplicatesAreRefused()
    {
        var a = new SyncDocument("A");
        var b = new SyncDocument("B");
        var op1 = a.Change(EntityTypes.Card, "c1", Fields(("name", "1")))!;
        var op2 = a.Change(EntityTypes.Card, "c1", Fields(("name", "2")))!;
        Assert.Equal(ApplyResult.Gap, b.Apply(op2));
        Assert.Equal(ApplyResult.Applied, b.Apply(op1));
        Assert.Equal(ApplyResult.Duplicate, b.Apply(op1));
        Assert.Equal(ApplyResult.Applied, b.Apply(SyncOp.FromJson(op2.ToJson())));
        Assert.Equal(State(a), State(b));
    }

    [Fact]
    public void RevokedDeviceOpsAreRejected()
    {
        var a = new SyncDocument("A");
        var b = new SyncDocument("B");
        var b1 = b.Change(EntityTypes.Card, "c1", Fields(("name", "1")))!;
        a.Apply(b1);
        a.Change(EntityTypes.Member, "B", Fields(("revoked", 1)));
        var b2 = b.Change(EntityTypes.Card, "c1", Fields(("name", "2")))!;
        Assert.Equal(ApplyResult.Rejected, a.Apply(b2));
        Assert.Equal("1", (string?)a.View(EntityTypes.Card).Single().Fields["name"]);
    }

    [Fact]
    public void SnapshotStandsInForTrimmedLog()
    {
        var a = new SyncDocument("A");
        for (var i = 0; i < 30; i++) a.Change(EntityTypes.Card, $"c{i % 5}", Fields(("name", $"n{i}"), ("data", $"{i}")));
        a.TrimLog(4);
        var b = new SyncDocument("B");
        Assert.Null(a.MissingFor(b.Vector));
        Send(a, b);
        Assert.Equal(State(a), State(b));
        Assert.Equal(a.Vector, b.Vector);

        // Through JSON, as it is saved and sent.
        var c = new SyncDocument("C");
        c.MergeSnapshot((JsonObject)JsonNode.Parse(a.Snapshot().ToJsonString())!);
        Assert.Equal(State(a), State(c));
    }

    /// <summary>Many devices editing at random, syncing pairwise in random order, end up with the same state.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomEditsConverge(int seed)
    {
        var random = new Random(seed);
        var clock = 1_000_000L;
        var docs = Enumerable.Range(0, 4).Select(i => new SyncDocument($"D{i}", () => clock + random.Next(-50, 50))).ToList();
        var ids = Enumerable.Range(0, 6).Select(i => $"c{i}").ToArray();
        for (var round = 0; round < 300; round++)
        {
            clock += random.Next(0, 3);
            var doc = docs[random.Next(docs.Count)];
            var id = ids[random.Next(ids.Length)];
            switch (random.Next(6))
            {
                case 0:
                    doc.Delete(EntityTypes.Card, id);
                    break;
                case 1:
                    var a = docs[random.Next(docs.Count)];
                    var b = docs[random.Next(docs.Count)];
                    if (a != b) Send(a, b);
                    break;
                case 2:
                    doc.TrimLog(random.Next(0, 20));
                    break;
                case 3:
                    if (doc.View(EntityTypes.Card).Where(v => v.CopyOf is not null).Select(v => v.Id).FirstOrDefault() is { } copy)
                        doc.Delete(EntityTypes.Card, copy);
                    break;
                default:
                    doc.Change(EntityTypes.Card, id, Fields(
                        ("name", $"n{random.Next(5)}"),
                        ("data", $"d{random.Next(5)}"),
                        ("folder", random.Next(2) == 0 ? "f1" : "")));
                    break;
            }
        }
        // Everyone meets everyone.
        foreach (var a in docs)
            foreach (var b in docs)
                if (a != b) Send(a, b);
        foreach (var a in docs)
            foreach (var b in docs)
                if (a != b) Send(a, b);
        var expected = State(docs[0]);
        foreach (var doc in docs.Skip(1)) Assert.Equal(expected, State(doc));
    }

    [Fact]
    public void FractionalIndexSortsBetween()
    {
        var keys = new List<string> { FractionalIndex.Between(null, null) };
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var at = random.Next(keys.Count + 1);
            var key = FractionalIndex.Between(at == 0 ? null : keys[at - 1], at == keys.Count ? null : keys[at]);
            Assert.True(FractionalIndex.IsValid(key));
            keys.Insert(at, key);
        }
        Assert.Equal(keys, keys.Order(StringComparer.Ordinal));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void HlcRoundTripsAndOrders()
    {
        var clock = new HlcClock("dev-1", () => 5);
        var t1 = clock.Next();
        var t2 = clock.Next();
        Assert.True(t2 > t1);
        Assert.Equal(t1, Hlc.Parse(t1.ToString()));
        clock.Observe(new Hlc(10, 3, "x"));
        Assert.True(clock.Next() > new Hlc(10, 3, "x"));
        Assert.True(string.CompareOrdinal(t1.ToString(), t2.ToString()) < 0);
    }
}
