using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
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

        public Node(IKeyValueStore? store = null)
        {
            store ??= new MemoryKeyValueStore();
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

    private sealed class CancelOnSnapshotStore(Action cancel) : IKeyValueStore
    {
        private readonly MemoryKeyValueStore _inner = new();
        public string? Read(string key) => _inner.Read(key);
        public void Write(string key, string value)
        {
            _inner.Write(key, value);
            if (key == "sync/snapshot") cancel();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RelayPairingSurvivesWaitingForApproval(bool scanQr)
    {
        await using var relay = await TestRelay.StartAsync(new RelayOptions
        {
            KeepAlive = TimeSpan.FromMilliseconds(200),
            JoinsPerMinute = 1000,
            RoomsPerMinute = 1000,
        });
        SyncService.Relays = () => [relay.Server];
        SyncService.LanDiscovery = () => null;
        var a = new Node();
        var b = new Node();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await a.OnUi(() =>
            {
                a.Cards.Add(Card("c1", "Office"));
                a.Sync.CreateGroup("Laptop");
            });
            Task join;
            await using (var invitation = await a.Sync.InviteAsync(cts.Token))
            {
                // Force the QR path through the relay even on a shared local network.
                var code = PairingCode.Parse(invitation.Qr)! with { Lan = Array.Empty<IPEndPoint>() };
                join = b.Sync.JoinAsync(scanQr ? code.ToQr() : invitation.TypedCode!, "Phone", null, cts.Token);
                var request = await invitation.WaitAsync(cts.Token);
                Assert.Equal(scanQr, request.Digits is null);
                // Several ping timeouts pass while the inviter has no application read pending.
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                await invitation.AcceptAsync(cts.Token);
            }
            // The UI disposes the invitation immediately after approval; the session must survive.
            await join;
            await Until(() => b.Cards.Cards.Any(c => c.Id == "c1"), "initial relay sync");
            await b.OnUi(() => b.Cards.Add(Card("c2", "Gym")));
            await Until(() => a.Cards.Cards.Any(c => c.Id == "c2"), "relay sync after approval");
        }
        finally
        {
            await a.Sync.StopAsync();
            await b.Sync.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledJoinDoesNotLeaveAPhantomMember(bool afterAcceptance)
    {
        await using var relay = await Setup();
        var a = new Node();
        var b = new Node();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var joining = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var previousUi = SyncService.Ui;
        var queued = new ConcurrentQueue<Action>();
        try
        {
            await a.OnUi(() => a.Sync.CreateGroup("Laptop"));
            var room = a.Sync.Locked(doc => (string?)doc.Find(EntityTypes.Group, "group")?.Value("room"));
            await using var invitation = await a.Sync.InviteAsync(timeout.Token);
            // Hold UI work so that even after receiving the welcome and data, B cannot finish joining.
            if (afterAcceptance) SyncService.Ui = queued.Enqueue;
            var join = b.Sync.JoinAsync(invitation.TypedCode!, "Phone", null, joining.Token);
            await invitation.WaitAsync(timeout.Token);
            Task accept;
            if (afterAcceptance)
            {
                accept = invitation.AcceptAsync(timeout.Token);
                await Until(() => b.Sync.IsEnabled, "B received the welcome");
                Assert.False(accept.IsCompleted);
                joining.Cancel();
            }
            else
            {
                joining.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join);
                accept = invitation.AcceptAsync(timeout.Token);
            }
            if (afterAcceptance) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join);
            await Assert.ThrowsAnyAsync<System.IO.IOException>(() => accept);
            Assert.False(b.Sync.IsEnabled);
            Assert.Single(a.Sync.Members);
            Assert.True(a.Sync.Members[0].IsThis);
            Assert.NotEqual(room, a.Sync.Locked(doc => (string?)doc.Find(EntityTypes.Group, "group")?.Value("room")));
        }
        finally
        {
            SyncService.Ui = previousUi;
            while (queued.TryDequeue(out var action)) previousUi(action);
            await a.Sync.StopAsync();
            await b.Sync.StopAsync();
        }
    }

    [Fact]
    public async Task CancellationDuringFinalSaveFinishesJoiningOnBothDevices()
    {
        await using var relay = await Setup();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var joining = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var a = new Node();
        var b = new Node(new CancelOnSnapshotStore(joining.Cancel));
        try
        {
            await a.OnUi(() => a.Sync.CreateGroup("Laptop"));
            await using var invitation = await a.Sync.InviteAsync(timeout.Token);
            var join = b.Sync.JoinAsync(invitation.TypedCode!, "Phone", null, joining.Token);
            await invitation.WaitAsync(timeout.Token);
            var accept = invitation.AcceptAsync(timeout.Token);
            await join;
            await accept;
            Assert.True(joining.IsCancellationRequested);
            Assert.True(b.Sync.IsEnabled);
            Assert.Equal(2, a.Sync.Members.Count);
            Assert.Equal(2, b.Sync.Members.Count);
        }
        finally
        {
            await a.Sync.StopAsync();
            await b.Sync.StopAsync();
        }
    }

    [Fact]
    public async Task LegacyPeerCanJoinWithoutCompletionAcknowledgment()
    {
        await using var relay = await Setup();
        var a = new Node();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await a.OnUi(() => a.Sync.CreateGroup("Laptop"));
            await using var invitation = await a.Sync.InviteAsync(timeout.Token);
            var key = KeyPair.Generate();
            var (channel, _) = await Rendezvous.ConnectAsync(PairingCode.Parse(invitation.TypedCode)!, "join",
                key, [relay.Server], timeout.Token);
            await using var peer = channel;
            await peer.SendAsync(new JsonObject
            {
                ["type"] = "join", ["device"] = "legacy-phone", ["name"] = "Phone", ["key"] = SyncCrypto.B64(key.Public),
            }, timeout.Token);
            await invitation.WaitAsync(timeout.Token);
            var accept = invitation.AcceptAsync(timeout.Token);
            var welcome = await peer.ReceiveAsync(timeout.Token);
            Assert.Equal("welcome", (string?)welcome?["type"]);
            Assert.Null(welcome?["confirmJoin"]);
            await accept;
            Assert.Equal(2, a.Sync.Members.Count);
        }
        finally { await a.Sync.StopAsync(); }
    }

    [Theory]
    [InlineData("  Laptop  ", "Laptop")]
    [InlineData("  My\t\n  Laptop  ", "My Laptop")]
    [InlineData("My<Dev>:name\"/\\|?*", "MyDevname")]
    [InlineData("A\u0000B\u200BC\u202ED\uFFFDE", "ABCDE")]
    [InlineData("Мой ноутбук (дом)", "Мой ноутбук (дом)")]
    [InlineData("   <>|?* \t ", "")]
    [InlineData(null, "")]
    public void SanitizeDeviceName_TrimsCollapsesStripsIllegal(string? input, string expected) =>
        Assert.Equal(expected, SyncService.SanitizeDeviceName(input));

    [Fact]
    public void SanitizeDeviceName_CapsLengthWithoutSplittingSurrogatePairs()
    {
        var tooLong = new string('x', SyncService.MaxDeviceNameLength + 10);
        Assert.Equal(SyncService.MaxDeviceNameLength, SyncService.SanitizeDeviceName(tooLong).Length);

        var endsWithSurrogate = new string('a', SyncService.MaxDeviceNameLength - 1) + "\U0001F600";
        var cleaned = SyncService.SanitizeDeviceName(endsWithSurrogate);
        Assert.Equal(SyncService.MaxDeviceNameLength - 1, cleaned.Length);
        Assert.All(cleaned, c => Assert.False(char.IsSurrogate(c)));
    }

    [Fact]
    public async Task Rename_SanitizesIgnoresEmptyAndNoOp()
    {
        var node = new Node();
        await node.OnUi(() => node.Sync.CreateGroup("  First/Name  "));
        Assert.Equal("FirstName", node.Sync.DeviceName);

        var seqBefore = node.Sync.Locked(doc => doc.Seq(doc.DeviceId));
        await node.OnUi(() => node.Sync.Rename("   <>|?*  "));
        Assert.Equal("FirstName", node.Sync.DeviceName);
        Assert.Equal(seqBefore, node.Sync.Locked(doc => doc.Seq(doc.DeviceId)));

        await node.OnUi(() => node.Sync.Rename("  First\tName  "));
        Assert.Equal("First Name", node.Sync.DeviceName);
        var seqAfterRename = node.Sync.Locked(doc => doc.Seq(doc.DeviceId));
        Assert.True(seqAfterRename > seqBefore);

        // Sanitized value matches current name -> no-op.
        await node.OnUi(() => node.Sync.Rename("  First  Name/  "));
        Assert.Equal("First Name", node.Sync.DeviceName);
        Assert.Equal(seqAfterRename, node.Sync.Locked(doc => doc.Seq(doc.DeviceId)));

        await node.Sync.StopAsync();
    }

    [Fact]
    public async Task DevicesExchangePlatformTags()
    {
        var previousPlatform = SyncService.CurrentPlatform;
        await using var relay = await Setup();
        var a = new Node();
        var b = new Node();
        try
        {
            SyncService.CurrentPlatform = "windows";
            await a.OnUi(() => a.Sync.CreateGroup("Workstation"));
            await Until(() => a.Sync.Status == SyncStatus.Online, "A online");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var invitation = await a.Sync.InviteAsync(cts.Token);

            SyncService.CurrentPlatform = "android";
            var join = b.Sync.JoinAsync(invitation.Qr, "Phone", null, cts.Token);
            var request = await invitation.WaitAsync(cts.Token);
            Assert.Equal("Phone", request.Name);
            await invitation.AcceptAsync(cts.Token);
            await join;

            await Until(() => a.Sync.Members.Count == 2 && b.Sync.Members.Count == 2, "both members present");

            Assert.Equal("windows", a.Sync.Members.Single(m => m.IsThis).Platform);
            Assert.Equal("android", a.Sync.Members.Single(m => !m.IsThis).Platform);
            Assert.Equal("android", b.Sync.Members.Single(m => m.IsThis).Platform);
            Assert.Equal("windows", b.Sync.Members.Single(m => !m.IsThis).Platform);
        }
        finally
        {
            SyncService.CurrentPlatform = previousPlatform;
            await a.Sync.StopAsync();
            await b.Sync.StopAsync();
        }
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
