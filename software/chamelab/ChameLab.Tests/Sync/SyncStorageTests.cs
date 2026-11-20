using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Services;
using ChameLab.Sync;
using Xunit;

namespace ChameLab.Tests.Sync;

public class SyncStorageTests
{
    private static Dictionary<string, JsonNode?> Name(string name) => new() { ["name"] = name };

    [Fact]
    public void LogAndSnapshotRoundTrip()
    {
        var store = new MemoryKeyValueStore();
        var storage = new SyncStorage(store);
        var device = new DeviceIdentity("A", "Laptop", KeyPair.Generate());
        storage.SaveDevice(device);
        var doc = new SyncDocument("A");
        storage.Append([doc.Change(EntityTypes.Card, "c1", Name("One"))!]);
        storage.SaveSnapshot(doc);
        storage.Append([doc.Change(EntityTypes.Card, "c1", Name("Two"))!, doc.Change(EntityTypes.Card, "c2", Name("Other"))!]);

        var loaded = new SyncStorage(store);
        Assert.Equal(device.Key.Public, loaded.LoadDevice()!.Key.Public);
        var again = loaded.LoadDocument("A");
        Assert.Equal(doc.Vector, again.Vector);
        Assert.Equal(new[] { "Two", "Other" }, again.View(EntityTypes.Card).OrderBy(v => v.Id).Select(v => (string?)v.Fields["name"]));
        Assert.Equal(2, loaded.Logged);
    }

    [Fact]
    public void TornLastLineIsDropped()
    {
        var store = new MemoryKeyValueStore();
        var storage = new SyncStorage(store);
        var doc = new SyncDocument("A");
        storage.Append([doc.Change(EntityTypes.Card, "c1", Name("One"))!]);
        store.Write("sync/log", store.Read("sync/log") + "{\"author\":\"A\",\"se");

        var again = new SyncStorage(store).LoadDocument("A");
        Assert.Equal(1, again.Seq("A"));
        Assert.Equal("One", (string?)again.View(EntityTypes.Card).Single().Fields["name"]);
    }
}
