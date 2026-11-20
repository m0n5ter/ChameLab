using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Services;

namespace ChameLab.Sync;

/// <summary>This device in a sync group: its id, its name as others see it and its key pair.</summary>
public sealed record DeviceIdentity(string Id, string Name, KeyPair Key);

/// <summary>
/// Sync's files, plain JSON in the app's data folder next to the library:
/// <list type="bullet">
/// <item><c>sync/device</c>: this device's identity and key;</item>
/// <item><c>sync/snapshot</c>: the whole merged state as of its last write;</item>
/// <item><c>sync/log</c>: operations since, one JSON object per line, appended as they come.</item>
/// </list>
/// A crash can leave half a line at the end of the log; it is dropped on load.
/// </summary>
public sealed class SyncStorage(IKeyValueStore store)
{
    private const string DeviceKey = "sync/device";
    private const string SnapshotKey = "sync/snapshot";
    private const string LogKey = "sync/log";

    /// <summary>Operations appended since the last snapshot.</summary>
    public int Logged { get; private set; }

    public DeviceIdentity? LoadDevice()
    {
        try
        {
            if (JsonNode.Parse(store.Read(DeviceKey) is { Length: > 0 } text ? text : "null") is not JsonObject o) return null;
            if ((string?)o["device"] is not { Length: > 0 } id || (string?)o["key"] is not { } key) return null;
            return new DeviceIdentity(id, (string?)o["name"] ?? "", new KeyPair(SyncCrypto.FromB64(key)));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void SaveDevice(DeviceIdentity device) =>
        store.Write(DeviceKey, new JsonObject
        {
            ["version"] = 1,
            ["device"] = device.Id,
            ["name"] = device.Name,
            ["key"] = SyncCrypto.B64(device.Key.Private),
        }.ToJsonString());

    /// <summary>The saved state: the snapshot, then the log on top.</summary>
    public SyncDocument LoadDocument(string deviceId)
    {
        var doc = new SyncDocument(deviceId);
        if (store.Read(SnapshotKey) is { Length: > 0 } snapshot && JsonNode.Parse(snapshot) is JsonObject o)
            doc.MergeSnapshot(o);
        Logged = 0;
        var log = store.Read(LogKey) ?? string.Empty;
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var torn = false;
        for (var i = 0; i < lines.Length; i++)
        {
            SyncOp op;
            try { op = SyncOp.FromJson(JsonNode.Parse(lines[i])!); }
            catch (Exception) when (i == lines.Length - 1)
            {
                torn = true;
                break;
            }
            doc.Apply(op);
            Logged++;
        }
        if (torn) SaveSnapshot(doc);
        return doc;
    }

    public void Append(IReadOnlyCollection<SyncOp> ops)
    {
        if (ops.Count == 0) return;
        var text = new StringBuilder();
        foreach (var op in ops) text.Append(op.ToJson().ToJsonString()).Append('\n');
        store.Append(LogKey, text.ToString());
        Logged += ops.Count;
    }

    /// <summary>Writes the whole state and starts an empty log.</summary>
    public void SaveSnapshot(SyncDocument doc)
    {
        store.Write(SnapshotKey, doc.Snapshot().ToJsonString());
        store.Write(LogKey, string.Empty);
        Logged = 0;
    }

    /// <summary>Forgets the group on this device (the library stays).</summary>
    public void Clear()
    {
        store.Write(LogKey, string.Empty);
        store.Write(SnapshotKey, string.Empty);
        store.Write(DeviceKey, string.Empty);
        Logged = 0;
    }
}
