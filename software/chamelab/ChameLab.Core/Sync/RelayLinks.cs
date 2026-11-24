using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Transport;

namespace ChameLab.Sync;

/// <summary>A device met in a relay room, and whether this side opens the conversation.</summary>
public sealed record RoomArrival(IPacketLink Link, bool Initiate);

/// <summary>
/// This device in its group's room on a relay (see <c>ChameLab.Relay/RelayRooms.cs</c>): one
/// WebSocket carrying a packet link to every other device in the room.
/// </summary>
public sealed class RelayRoom : IAsyncDisposable
{
    private const byte Message = 1, Joined = 2, Left = 3, Present = 4;

    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<ushort, PeerLink> _peers = new();
    private readonly Channel<RoomArrival> _arrivals = Channel.CreateUnbounded<RoomArrival>();
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader;

    private RelayRoom(ClientWebSocket ws, RelayServer server)
    {
        _ws = ws;
        Server = server;
    }

    public RelayServer Server { get; }

    /// <summary>Devices as they turn up; completes when the room connection ends.</summary>
    public ChannelReader<RoomArrival> Arrivals => _arrivals.Reader;

    public static Uri Url(Uri relay, string roomId) => new($"{relay.AbsoluteUri.TrimEnd('/')}/room/{roomId}");

    public static async Task<RelayRoom> JoinAsync(RelayServer server, string roomId, CancellationToken ct)
    {
        var ws = RelayEndpoint.CreateSocket(server);
        try
        {
            await ws.ConnectAsync(Url(server.Url, roomId), ct);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        var room = new RelayRoom(ws, server);
        room._reader = Task.Run(room.ReadAsync);
        return room;
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[128 * 1024 + 8];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var length = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (length == buffer.Length) throw new IOException("Relay frame too large.");
                    r = await _ws.ReceiveAsync(buffer.AsMemory(length), _stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    length += r.Count;
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Binary || length < 3) continue;
                var peer = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(1));
                switch (buffer[0])
                {
                    case Message:
                        if (_peers.TryGetValue(peer, out var link)) link.Deliver(buffer.AsSpan(3, length - 3).ToArray());
                        break;
                    case Joined:
                    case Present:
                        var added = new PeerLink(this, peer);
                        if (_peers.TryAdd(peer, added))
                            _arrivals.Writer.TryWrite(new RoomArrival(added, Initiate: buffer[0] == Present));
                        break;
                    case Left:
                        if (_peers.TryRemove(peer, out var gone)) gone.End();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The room connection is gone; the owner reconnects.
        }
        finally
        {
            foreach (var link in _peers.Values) link.End();
            _peers.Clear();
            _arrivals.Writer.TryComplete();
        }
    }

    private async Task SendAsync(ushort peer, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, peer);
        payload.Span.CopyTo(frame.AsSpan(2));
        await _send.WaitAsync(ct);
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct); }
        finally { _send.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        if (_reader is not null) await _reader;
        _ws.Dispose();
    }

    private sealed class PeerLink(RelayRoom room, ushort peer) : IPacketLink
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();

        public string Kind => "relay";

        public void Deliver(byte[] packet) => _inbound.Writer.TryWrite(packet);

        public void End() => _inbound.Writer.TryComplete();

        public Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct) => room.SendAsync(peer, packet, ct);

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            try { return await _inbound.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask DisposeAsync()
        {
            End();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A byte stream through the relay's <c>/share</c> and <c>/join</c> endpoints (the ones shared
/// devices use), for pairing and sending cards with a one-time code. The relay may split or merge
/// binary messages, so this is a stream; <see cref="StreamPacketLink"/> frames packets on it.
/// </summary>
public sealed class RelayPipe : Stream
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly TaskCompletionSource _peer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(WebSocketMessageType Type, byte[] Data)> _inbound =
        Channel.CreateBounded<(WebSocketMessageType, byte[])>(16);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingAt;
    private bool _ended;

    private RelayPipe(ClientWebSocket ws)
    {
        _ws = ws;
        // Receive even while the user compares digits or approves the new device:
        // ClientWebSocket only answers the relay's keep-alive pings while receiving.
        _reader = ReadSocketAsync();
    }

    private async Task ReadSocketAsync()
    {
        Exception? error = null;
        try
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var r = await _ws.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                // Binary data is a stream; text controls must remain whole messages.
                var data = buffer[..r.Count];
                if (r.MessageType == WebSocketMessageType.Text && !r.EndOfMessage)
                {
                    using var message = new MemoryStream();
                    message.Write(data);
                    do
                    {
                        r = await _ws.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, r.Count);
                        if (message.Length > 4096) throw new IOException("Relay control message too large.");
                    } while (!r.EndOfMessage);
                    data = message.ToArray();
                }
                await _inbound.Writer.WriteAsync((r.MessageType, data), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) error = new IOException(Localization.Strings.Relay_Closed, ex);
        }
        finally { _inbound.Writer.TryComplete(error); }
    }

    private async Task<(WebSocketMessageType Type, byte[] Data)?> ReadMessageAsync(CancellationToken ct)
    {
        try { return await _inbound.Reader.ReadAsync(ct); }
        catch (ChannelClosedException ex)
        {
            if (ex.InnerException is { } error) throw new IOException(Localization.Strings.Relay_Closed, error);
            return null;
        }
    }

    /// <summary>Opens a share: the code the relay handed out, and the pipe (usable once <see cref="WaitForPeerAsync"/> returns).</summary>
    public static async Task<(RelayPipe Pipe, ShareCode Code)> ShareAsync(RelayServer server, CancellationToken ct)
    {
        var ws = RelayEndpoint.CreateSocket(server);
        RelayPipe? pipe = null;
        try
        {
            await ws.ConnectAsync(RelayEndpoint.Share(server.Url), ct);
            pipe = new RelayPipe(ws);
            while (true)
            {
                var text = await pipe.ReadControlAsync(ct) ?? throw new IOException(Localization.Strings.Relay_Closed);
                if (RelayEndpoint.MessageType(text) == "code" && RelayEndpoint.Property(text, "code") is { } code)
                    return (pipe, new ShareCode(server.Region, code.Replace("-", "")));
            }
        }
        catch
        {
            if (pipe is not null) await pipe.DisposeAsync();
            else ws.Dispose();
            throw;
        }
    }

    /// <summary>Waits until someone joins with the code.</summary>
    public async Task WaitForPeerAsync(CancellationToken ct)
    {
        while (!_peer.Task.IsCompleted)
        {
            var text = await ReadControlAsync(ct) ?? throw new IOException(Localization.Strings.Relay_Closed);
            if (RelayEndpoint.MessageType(text) == "peer-joined") _peer.TrySetResult();
        }
    }

    public static async Task<RelayPipe> JoinAsync(RelayServer server, string code, CancellationToken ct)
    {
        var ws = RelayEndpoint.CreateSocket(server);
        RelayPipe? pipe = null;
        try
        {
            await ws.ConnectAsync(RelayEndpoint.Join(server.Url, code), ct);
            pipe = new RelayPipe(ws);
            while (true)
            {
                var text = await pipe.ReadControlAsync(ct)
                           ?? throw new IOException(RelayEndpoint.CloseMessage(ws.CloseStatusDescription));
                if (RelayEndpoint.MessageType(text) == "joined") return pipe;
            }
        }
        catch
        {
            if (pipe is not null) await pipe.DisposeAsync();
            else ws.Dispose();
            throw;
        }
    }

    /// <summary>The next text (control) message, skipping binary ones; null once closed.</summary>
    private async Task<byte[]?> ReadControlAsync(CancellationToken ct)
    {
        while (await ReadMessageAsync(ct) is { } message)
            if (message.Type == WebSocketMessageType.Text) return message.Data;
        return null;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (_pendingAt >= _pending.Length)
        {
            if (_ended) return 0;
            var message = await ReadMessageAsync(ct);
            if (message is null) { _ended = true; return 0; }
            if (message.Value.Type == WebSocketMessageType.Text)
            {
                // Control messages mid-stream: the other side leaving ends the stream.
                if (RelayEndpoint.MessageType(message.Value.Data) == "peer-left") { _ended = true; return 0; }
                continue;
            }
            _pending = message.Value.Data;
            _pendingAt = 0;
        }
        var n = Math.Min(destination.Length, _pending.Length - _pendingAt);
        _pending.AsSpan(_pendingAt, n).CopyTo(destination.Span);
        _pendingAt += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        await _send.WaitAsync(ct);
        try { await _ws.SendAsync(source, WebSocketMessageType.Binary, true, ct); }
        finally { _send.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        _stop.Cancel();
        _ws.Dispose();
        await _reader;
        await base.DisposeAsync();
    }
}
