using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Sync;

/// <summary>What a sync session needs from the device it runs on.</summary>
public interface ISyncHost
{
    /// <summary>Runs <paramref name="action"/> with the document, holding its lock.</summary>
    T Locked<T>(Func<SyncDocument, T> action);

    /// <summary>Operations (or a snapshot) arrived and were applied: save them and show them.</summary>
    void Received(IReadOnlyList<SyncOp> applied, bool snapshot);
}

/// <summary>
/// The sync exchange between two members over a secure channel:
/// <list type="number">
/// <item>both send <c>hello</c> with their version vector;</item>
/// <item>each sends what the other lacks: <c>ops</c> (oldest first, in batches), or a
///   <c>snapshot</c> first when it no longer keeps some of them;</item>
/// <item>while connected, new operations are pushed as they are made or received;</item>
/// <item>a receiver that finds a gap sends <c>want</c> with its vector to have them sent again.</item>
/// </list>
/// </summary>
public sealed class SyncSession(SecureChannel channel, string peerDevice, ISyncHost host, Action? joined = null)
{
    private const int Batch = 200;

    private readonly SemaphoreSlim _pushing = new(1, 1);
    private readonly object _vectorLock = new();
    private readonly TaskCompletionSource _greeted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, long>? _peerVector;

    public string PeerDevice => peerDevice;
    public string Kind => channel.Link.Kind;
    public SecureChannel Channel => channel;

    /// <summary>Completes once the other side said hello and got everything this side had then.</summary>
    public Task Greeted => _greeted.Task;

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// <summary>Runs until the link closes or <paramref name="ct"/> is canceled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var vector = host.Locked(doc => SyncDocument.VectorJson(doc.Vector));
        await channel.SendAsync(new JsonObject { ["type"] = "hello", ["device"] = host.Locked(d => d.DeviceId), ["vector"] = vector }, ct);
        try
        {
            while (await channel.ReceiveAsync(ct) is { } message)
            {
                switch ((string?)message["type"])
                {
                    case "joined":
                        joined?.Invoke();
                        break;
                    case "hello":
                    case "want":
                        lock (_vectorLock) _peerVector = SyncDocument.ReadVector(message["vector"]);
                        await PushAsync(ct);
                        _greeted.TrySetResult();
                        break;
                    case "snapshot":
                        if (message["state"] is JsonObject state)
                        {
                            host.Locked(doc => { doc.MergeSnapshot(state); return 0; });
                            host.Received(Array.Empty<SyncOp>(), snapshot: true);
                        }
                        break;
                    case "ops":
                        await ReceiveOpsAsync(message["ops"] as JsonArray, ct);
                        break;
                }
            }
        }
        finally
        {
            _greeted.TrySetResult();
        }
    }

    private async Task ReceiveOpsAsync(JsonArray? list, CancellationToken ct)
    {
        if (list is null) return;
        var ops = new List<SyncOp>();
        foreach (var node in list)
        {
            try { if (node is not null) ops.Add(SyncOp.FromJson(node)); }
            catch (FormatException) { /* a malformed operation is skipped */ }
        }
        var gap = false;
        var applied = host.Locked(doc =>
        {
            var done = new List<SyncOp>();
            foreach (var op in ops.OrderBy(o => o.Time))
            {
                switch (doc.Apply(op))
                {
                    case ApplyResult.Applied: done.Add(op); break;
                    case ApplyResult.Gap: gap = true; break;
                }
            }
            return done;
        });
        lock (_vectorLock)
        {
            // The peer has what it sent.
            if (_peerVector is not null)
                foreach (var op in ops)
                    if (op.Seq > _peerVector.GetValueOrDefault(op.Author)) _peerVector[op.Author] = op.Seq;
        }
        if (applied.Count > 0) host.Received(applied, snapshot: false);
        if (gap)
        {
            var vector = host.Locked(doc => SyncDocument.VectorJson(doc.Vector));
            await channel.SendAsync(new JsonObject { ["type"] = "want", ["vector"] = vector }, ct);
        }
    }

    /// <summary>Sends the other side whatever this one has that it lacks.</summary>
    public async Task PushAsync(CancellationToken ct)
    {
        await _pushing.WaitAsync(ct);
        try
        {
            Dictionary<string, long> peer;
            lock (_vectorLock)
            {
                if (_peerVector is null) return;
                peer = new Dictionary<string, long>(_peerVector);
            }
            var (snapshot, ops, vector) = host.Locked(doc =>
            {
                if (!doc.IsAheadOf(peer)) return ((JsonObject?)null, (IReadOnlyList<SyncOp>)Array.Empty<SyncOp>(), (Dictionary<string, long>?)null);
                JsonObject? state = null;
                var missing = doc.MissingFor(peer);
                if (missing is null)
                {
                    state = doc.Snapshot();
                    missing = doc.MissingFor(SyncDocument.ReadVector(state["vector"])) ?? Array.Empty<SyncOp>();
                }
                return (state, missing, new Dictionary<string, long>(doc.Vector));
            });
            if (vector is null) return;
            if (snapshot is not null)
                await channel.SendAsync(new JsonObject { ["type"] = "snapshot", ["state"] = snapshot }, ct);
            for (var at = 0; at < ops.Count; at += Batch)
            {
                var batch = new JsonArray(ops.Skip(at).Take(Batch).Select(op => (JsonNode)op.ToJson()).ToArray());
                await channel.SendAsync(new JsonObject { ["type"] = "ops", ["ops"] = batch }, ct);
            }
            lock (_vectorLock)
            {
                foreach (var (author, seq) in vector)
                    if (seq > _peerVector!.GetValueOrDefault(author)) _peerVector[author] = seq;
            }
        }
        finally { _pushing.Release(); }
    }
}
