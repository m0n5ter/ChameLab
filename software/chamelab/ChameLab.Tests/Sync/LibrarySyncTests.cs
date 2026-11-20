using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Sync;
using Xunit;

namespace ChameLab.Tests.Sync;

public class LibrarySyncTests
{
    /// <summary>One device: its library, its document and the sync between them.</summary>
    private sealed class Device
    {
        public CardLibrary Cards { get; }
        public SyncDocument Doc { get; }
        public LibrarySync Sync { get; }

        public Device(string id)
        {
            Cards = new CardLibrary(new MemoryKeyValueStore());
            Doc = new SyncDocument(id);
            Sync = new LibrarySync(new CardLibrarySync(Cards));
        }

        public void Capture() => Sync.Capture(Doc);

        /// <summary>Takes another device's operations and shows the merged state.</summary>
        public void Receive(Device from)
        {
            Capture();
            from.Capture();
            var ops = from.Doc.MissingFor(Doc.Vector);
            if (ops is null) Doc.MergeSnapshot(from.Doc.Snapshot());
            else foreach (var op in ops) Doc.Apply(op);
            Sync.Materialize(Doc);
        }

        public string State => string.Join("\n",
            Cards.Folders.Select(f => $"[{f}]")
                .Concat(Cards.Cards.Select(c => $"{c.Id}:{c.Name}@{c.Folder}")));
    }

    private static CardSave Card(string id, string name, string folder = "") =>
        new() { Id = id, Name = name, Folder = folder, Tag = TagSpecificType.Mifare1024, Uid = [1, 2, 3, 4] };

    [Fact]
    public void CardsAndFoldersReachTheOtherDevice()
    {
        var a = new Device("A");
        var b = new Device("B");
        a.Cards.AddFolder("Work/Doors");
        a.Cards.Add(Card("c1", "Office", "Work/Doors"));
        a.Cards.Add(Card("c2", "Gym"));
        b.Receive(a);
        Assert.Equal(a.State, b.State);
        Assert.Contains("Work/Doors", b.Cards.Folders);
    }

    [Fact]
    public void FolderRenameAndFilingElsewhereBothHold()
    {
        var a = new Device("A");
        var b = new Device("B");
        a.Cards.AddFolder("Work");
        a.Cards.Add(Card("c1", "Office", "Work"));
        a.Cards.Add(Card("c2", "Gym"));
        b.Receive(a);

        a.Cards.RenameFolder("Work", "Job");
        b.Cards.MoveToFolder(b.Cards.Cards.Single(c => c.Id == "c2"), "Work");
        b.Receive(a);
        a.Receive(b);
        Assert.Equal(a.State, b.State);
        Assert.All(a.Cards.Cards, c => Assert.Equal("Job", c.Folder));
        Assert.Equal(new[] { "Job" }, a.Cards.Folders);
    }

    [Fact]
    public void ReorderAndDeleteSync()
    {
        var a = new Device("A");
        var b = new Device("B");
        foreach (var i in Enumerable.Range(1, 5)) a.Cards.Add(Card($"c{i}", $"Card {i}"));
        b.Receive(a);
        a.Cards.Reorder(new[] { "c5", "c1", "c2", "c3", "c4" });
        b.Cards.Remove(b.Cards.Cards.Single(c => c.Id == "c3"));
        b.Receive(a);
        a.Receive(b);
        Assert.Equal(a.State, b.State);
        Assert.Equal(new[] { "c5", "c1", "c2", "c4" }, a.Cards.Cards.Select(c => c.Id));
    }

    [Fact]
    public void ConcurrentDumpEditsShowACopy()
    {
        var a = new Device("A");
        var b = new Device("B");
        a.Cards.Add(Card("c1", "Office"));
        b.Receive(a);
        var ca = a.Cards.Cards.Single().Clone(); ca.Id = "c1"; ca.Data = [[1]]; a.Cards.Update(ca);
        var cb = b.Cards.Cards.Single().Clone(); cb.Id = "c1"; cb.Data = [[2]]; b.Cards.Update(cb);
        b.Receive(a);
        a.Receive(b);
        Assert.Equal(a.State, b.State);
        Assert.Equal(2, a.Cards.Cards.Count);
        Assert.Contains(a.Cards.Cards, c => c.Name == "Office (conflict)");

        // Nothing more happens while nobody edits.
        var before = a.Doc.Log.Count;
        a.Receive(b);
        b.Receive(a);
        Assert.Equal(before, a.Doc.Log.Count);
    }

    [Fact]
    public void JoiningKeepsLocalCardsAndSharesFolders()
    {
        var a = new Device("A");
        a.Cards.AddFolder("Work");
        a.Cards.Add(Card("c1", "Office", "Work"));
        a.Capture();

        var b = new Device("B");
        b.Cards.Add(Card("c1", "Office (old name)", "Work"));
        b.Cards.Add(Card("c9", "Home", "Work"));
        foreach (var op in a.Doc.MissingFor(b.Doc.Vector)!) b.Doc.Apply(op);
        b.Sync.Capture(b.Doc, CaptureMode.Joining);
        b.Sync.Materialize(b.Doc);
        a.Receive(b);

        Assert.Equal(a.State, b.State);
        Assert.Equal(new[] { "Work" }, a.Cards.Folders);
        Assert.Equal("Office", a.Cards.Cards.Single(c => c.Id == "c1").Name);
        Assert.Equal("Work", a.Cards.Cards.Single(c => c.Id == "c9").Folder);
    }

    [Fact]
    public void SameFolderNameMadeOnTwoDevicesGetsANumber()
    {
        var a = new Device("A");
        var b = new Device("B");
        a.Cards.AddFolder("Work");
        b.Cards.AddFolder("Work");
        b.Receive(a);
        a.Receive(b);
        b.Receive(a);
        Assert.Equal(a.State, b.State);
        Assert.Equal(new[] { "Work", "Work (2)" }, a.Cards.Folders);
    }
}
