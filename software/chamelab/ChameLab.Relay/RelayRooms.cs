using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace ChameLab.Relay;

/// <summary>
/// Sync rooms: the devices of one sync group meet in the room named by a secret only they know,
/// and the relay copies their messages between them without reading them (they are encrypted
/// end to end). A room lives while someone is in it; nothing is stored.
/// <para>Frames (binary WebSocket messages):</para>
/// <list type="bullet">
/// <item>relay → device: <c>[kind][peer: u16 big endian][payload]</c>; kind 1 = a message from that
/// peer, 2 = the peer joined (it will open the conversation), 3 = the peer left, 4 = the peer was
/// already there when this device joined (this device opens the conversation).</item>
/// <item>device → relay: <c>[peer: u16 big endian][payload]</c>, delivered to that peer.</item>
/// </list>
/// </summary>
internal sealed partial class RelayRooms(RelayOptions options, ILogger<RelayRooms> log)
{
    public const byte Message = 1;
    public const byte Joined = 2;
    public const byte Left = 3;
    public const byte Present = 4;

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private int _sockets;

    public int Count => _rooms.Count;

    [GeneratedRegex("^[A-Za-z0-9_-]{22,64}$")]
    private static partial Regex RoomId();

    public async Task RoomAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var id = ctx.Request.RouteValues["id"] as string;
        if (id is null || !RoomId().IsMatch(id)) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (Volatile.Read(ref _sockets) >= options.MaxRoomSockets)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var ct = ctx.RequestAborted;
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var member = new Member(ws);
        Room room;
        List<Member>? others = null;
        while (true)
        {
            room = _rooms.GetOrAdd(id, _ => new Room());
            lock (room)
            {
                // A room emptied just now is being removed: take a fresh one.
                if (room.Closed) continue;
                if (room.Members.Count < options.MaxRoomPeers)
                {
                    member.Number = room.Next++;
                    others = room.Members.Values.ToList();
                    room.Members[member.Number] = member;
                }
            }
            break;
        }
        if (others is null)
        {
            await RejectAsync(ws, "busy");
            return;
        }
        Interlocked.Increment(ref _sockets);
        try
        {
            foreach (var other in others)
            {
                await member.TrySendAsync(Frame(Present, other.Number, default));
                await other.TrySendAsync(Frame(Joined, member.Number, default));
            }
            await PumpAsync(room, member, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Gone without a close: network lost, or it stopped answering pings.
        }
        finally
        {
            Interlocked.Decrement(ref _sockets);
            List<Member> left;
            lock (room)
            {
                room.Members.Remove(member.Number);
                left = room.Members.Values.ToList();
                if (left.Count == 0)
                {
                    room.Closed = true;
                    _rooms.TryRemove(KeyValuePair.Create(id, room));
                }
            }
            foreach (var other in left) await other.TrySendAsync(Frame(Left, member.Number, default));
        }
    }

    private async Task PumpAsync(Room room, Member member, CancellationToken ct)
    {
        var buffer = new byte[options.MaxRoomFrame + 2];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (length == buffer.Length)
                {
                    log.LogInformation("Room frame too large; closing");
                    await RejectAsync(member.Socket, "too-large");
                    return;
                }
                r = await member.Socket.ReceiveAsync(buffer.AsMemory(length), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await member.AnswerCloseAsync();
                    return;
                }
                length += r.Count;
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Binary || length < 2) continue;
            var to = BinaryPrimitives.ReadUInt16BigEndian(buffer);
            Member? target;
            lock (room) target = room.Members.GetValueOrDefault(to);
            if (target is null || target == member) continue;
            await target.TrySendAsync(Frame(Message, member.Number, buffer.AsMemory(2, length - 2)));
        }
    }

    private static byte[] Frame(byte kind, ushort peer, ReadOnlyMemory<byte> payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), peer);
        payload.Span.CopyTo(frame.AsSpan(3));
        return frame;
    }

    private static async Task RejectAsync(WebSocket ws, string reason)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { /* already gone */ }
    }

    private sealed class Room
    {
        public readonly Dictionary<ushort, Member> Members = new();
        public ushort Next = 1;
        public bool Closed;
    }

    private sealed class Member(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public WebSocket Socket => socket;
        public ushort Number { get; set; }

        /// <summary>Sends unless the socket is gone; a slow receiver gets 10 seconds per frame.</summary>
        public async Task TrySendAsync(byte[] frame)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _send.WaitAsync(timeout.Token);
                try { await socket.SendAsync(frame, WebSocketMessageType.Binary, true, timeout.Token); }
                finally { _send.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                // Its own pump notices it is gone.
            }
        }

        public async Task AnswerCloseAsync()
        {
            await _send.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException) { }
            finally { _send.Release(); }
        }
    }
}
