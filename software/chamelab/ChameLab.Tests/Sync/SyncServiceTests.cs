using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Relay;
using ChameLab.Services;
using ChameLab.Sync;
using Xunit;

namespace ChameLab.Tests.Sync;

/// <summary>Sync services share static settings (relays, the UI queue), so these run one at a time.</summary>
[CollectionDefinition("SyncService", DisableParallelization = true)]
public sealed class SyncServiceCollection;

[Collection("SyncService")]
public class SyncServiceTests
{
    private sealed class Node
    {
        public CardLibrary Cards { get; }
        public DictionaryLibrary Dictionaries { get; }
        public SyncService Sync { get; }

        public Node()
        {
            var store = new MemoryKeyValueStore();
            Cards = new CardLibrary(store);
            Dictionaries = new DictionaryLibrary(store);
            Sync = new SyncService(store, Cards, Dictionaries);
        }

        /// <summary>Edits the library the way the app does: on its "UI thread".</summary>
        public Task OnUi(Action action)
        {
            var done = new TaskCompletionSource();
            SyncService.Ui(() => { action(); done.SetResult(); });
            return done.Task;
        }
    }

    private static CardSave Card(string id, string name, string folder = "") =>
        new() { Id = id, Name = name, Folder = folder, Tag = TagSpecificType.Mifare1024, Uid = [1, 2, 3, 4] };

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static async Task<TestRelay> Setup()
    {
        var relay = await TestRelay.StartAsync(new RelayOptions { JoinsPerMinute = 1000, RoomsPerMinute = 1000 });
        SyncService.Relays = () => [relay.Server];
        SyncService.LanDiscovery = () => null;
        return relay;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DevicesJoinAndStayInStep(bool scanQr)
    {
        await using var relay = await Setup();
        var a = new Node();
        var b = new Node();
        await a.OnUi(() =>
        {
            a.Cards.AddFolder("Work");
            a.Cards.Add(Card("c1", "Office", "Work"));
            a.Sync.CreateGroup("Laptop");
        });
        await b.OnUi(() => b.Cards.Add(Card("c2", "Gym", "Work")));
        await Until(() => a.Sync.Status == SyncStatus.Online, "A online");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var invitation = await a.Sync.InviteAsync(cts.Token);
        string? bDigits = null;
        var join = b.Sync.JoinAsync(scanQr ? invitation.Qr : invitation.TypedCode!, "Phone", d => bDigits = d, cts.Token);
        var request = await invitation.WaitAsync(cts.Token);
        Assert.Equal("Phone", request.Name);
        if (scanQr) Assert.Null(request.Digits);
        else Assert.NotNull(request.Digits);
        await invitation.AcceptAsync(cts.Token);
        await join;
        if (!scanQr) Assert.Equal(request.Digits, bDigits);

        // Both libraries now hold both cards, in the one shared folder.
        await Until(() => a.Cards.Cards.Count == 2 && b.Cards.Cards.Count == 2, "both cards on both devices");
        Assert.Equal(new[] { "Work" }, a.Cards.Folders);
        Assert.Equal(new[] { "Work" }, b.Cards.Folders);
        Assert.All(b.Cards.Cards, c => Assert.Equal("Work", c.Folder));
        Assert.Equal(2, a.Sync.Members.Count);

        // Live edits go both ways.
        await a.OnUi(() =>
        {
            var c1 = a.Cards.Cards.Single(c => c.Id == "c1").Clone();
            c1.Id = "c1";
            c1.Name = "Office door";
            a.Cards.Update(c1);
        });
        await Until(() => b.Cards.Cards.Any(c => c.Name == "Office door"), "rename on B");
        await b.OnUi(() => b.Cards.Remove(b.Cards.Cards.Single(c => c.Id == "c2")));
        await Until(() => a.Cards.Cards.Count == 1, "delete on A");

        // A removed device gets nothing new.
        var bId = b.Sync.DeviceId!;
        a.Sync.Remove(bId);
        await Until(() => a.Sync.Members.All(m => m.Id != bId), "B gone from A's list");
        await a.OnUi(() => a.Cards.Add(Card("c3", "Garage")));
        await Task.Delay(1500);
        Assert.DoesNotContain(b.Cards.Cards, c => c.Id == "c3");

        await a.Sync.StopAsync();
        await b.Sync.StopAsync();
    }

    [Fact]
    public async Task CardsCanBeSentToAnyone()
    {
        await using var relay = await Setup();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cards = new[] { Card("c1", "Office"), Card("c2", "Gym") };
        await using var offer = await SyncService.OfferCardsAsync(cards, cts.Token);
        var receive = SyncService.ReceiveCardsAsync(offer.Qr, null, cts.Token);
        var (_, digits) = await offer.WaitAsync(cts.Token);
        Assert.Null(digits);
        await offer.SendAsync(cts.Token);
        var received = await receive;
        Assert.Equal(new[] { "Office", "Gym" }, received.Select(c => c.Name));
        Assert.DoesNotContain(received, c => c.Id is "c1" or "c2");
    }
}
