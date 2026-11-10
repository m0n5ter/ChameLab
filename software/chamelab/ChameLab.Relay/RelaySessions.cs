using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace ChameLab.Relay;

/// <summary>Session codes: Crockford base32, so look-alike letters typed by hand still match.</summary>
internal static class RelayCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// 8 characters, 40 bits: at the per-IP join limit, guessing one live code among hundreds
    /// would take millions of years. The client puts the relay's region in front (TR1-ABCD-1234).
    /// </summary>
    public const int Length = 8;

    public static string New() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    /// <summary>The code as shown to people: two groups of four.</summary>
    public static string Format(string code) => $"{code[..4]}-{code[4..]}";

    /// <summary>The canonical code for what someone typed, or null when it can't be one.</summary>
    public static string? Normalize(string? text)
    {
        if (text is null) return null;
        var code = new StringBuilder(Length);
        foreach (var typed in text)
        {
            if (typed is '-' or ' ') continue;
            var c = char.ToUpperInvariant(typed) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (Alphabet.IndexOf(c) < 0 || code.Length == Length) return null;
            code.Append(c);
        }
        return code.Length == Length ? code.ToString() : null;
    }
}

/// <summary>Close reasons the connecting side turns into a message.</summary>
internal static class RelayCloseReason
{
    public const string InvalidCode = "invalid-code";
    public const string Busy = "busy";
    public const string HostLeft = "host-left";
}

/// <summary>Live shares by code, and the two byte pumps of each.</summary>
internal sealed class RelaySessions(RelayOptions options, ILogger<RelaySessions> log)
{
    private const int BufferSize = 8 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public int Count => _sessions.Count;

    public async Task ShareAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (_sessions.Count >= options.MaxSessions)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var ct = ctx.RequestAborted;
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new Session(ws);
        string code;
        do code = RelayCode.New(); while (!_sessions.TryAdd(code, session));
        log.LogInformation("Share opened ({Count} active)", _sessions.Count);
        try
        {
            await session.SendToHostAsync(Message("code", RelayCode.Format(code)), ct);
            await session.PumpHostAsync(ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The sharing side went away.
        }
        finally
        {
            _sessions.TryRemove(code, out _);
            await session.EndAsync();
            log.LogInformation("Share closed ({Count} active)", _sessions.Count);
        }
    }

    public async Task JoinAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

        var ct = ctx.RequestAborted;
        var code = RelayCode.Normalize(ctx.Request.RouteValues["code"] as string);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        if (code is null || !_sessions.TryGetValue(code, out var session))
        {
            await RejectAsync(ws, RelayCloseReason.InvalidCode);
            return;
        }
        if (!session.TryAttach(ws))
        {
            await RejectAsync(ws, RelayCloseReason.Busy);
            return;
        }

        try
        {
            await session.SendToPeerAsync(ws, Message("joined"), ct);
            await session.TrySendToHostAsync(Message("peer-joined"));
            await session.PumpPeerAsync(ws, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The connecting side (or the sharing side under it) went away.
        }
        finally
        {
            if (session.Detach(ws)) await session.TrySendToHostAsync(Message("peer-left"));
        }
    }

    private static async Task RejectAsync(WebSocket ws, string reason)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { /* already gone */ }
    }

    /// <summary>A control message; the values come from fixed alphabets, so nothing needs escaping.</summary>
    private static string Message(string type, string? code = null) =>
        code is null ? $$"""{"type":"{{type}}"}""" : $$"""{"type":"{{type}}","code":"{{code}}"}""";

    /// <summary>
    /// One shared device: the sharing socket for its whole life, and at most one connecting socket
    /// at a time. Each socket takes one send at a time, so sends to it go through its lock.
    /// </summary>
    private sealed class Session(WebSocket host)
    {
        private readonly SemaphoreSlim _hostSend = new(1, 1);
        private readonly SemaphoreSlim _peerSend = new(1, 1);
        private WebSocket? _peer;

        public bool TryAttach(WebSocket peer) => Interlocked.CompareExchange(ref _peer, peer, null) is null;

        /// <summary>False when the peer was already detached (the share ended under it).</summary>
        public bool Detach(WebSocket peer) => ReferenceEquals(Interlocked.CompareExchange(ref _peer, null, peer), peer);

        public Task SendToHostAsync(string text, CancellationToken ct) =>
            SendAsync(host, _hostSend, Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

        public async Task TrySendToHostAsync(string text)
        {
            try { await SendToHostAsync(text, CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException) { }
        }

        public Task SendToPeerAsync(WebSocket peer, string text, CancellationToken ct) =>
            SendAsync(peer, _peerSend, Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

        /// <summary>Device → connecting side. Bytes that arrive while nobody is connected are dropped.</summary>
        public async Task PumpHostAsync(CancellationToken ct)
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var r = await host.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await AnswerCloseAsync(host, _hostSend);
                    return;
                }
                if (r.MessageType != WebSocketMessageType.Binary || r.Count == 0) continue;
                if (Volatile.Read(ref _peer) is not { } peer) continue;
                try { await SendAsync(peer, _peerSend, buffer.AsMemory(0, r.Count), WebSocketMessageType.Binary, ct); }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
                {
                    // The connecting side dropped; its own pump notices and detaches.
                }
            }
        }

        /// <summary>Connecting side → device. Ends when that side leaves; throws when the share is gone.</summary>
        public async Task PumpPeerAsync(WebSocket peer, CancellationToken ct)
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var r = await peer.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await AnswerCloseAsync(peer, _peerSend);
                    return;
                }
                if (r.MessageType != WebSocketMessageType.Binary || r.Count == 0) continue;
                await SendAsync(host, _hostSend, buffer.AsMemory(0, r.Count), WebSocketMessageType.Binary, ct);
            }
        }

        /// <summary>The share ended: tells the connecting side why, so it can say so.</summary>
        public async Task EndAsync()
        {
            if (Interlocked.Exchange(ref _peer, null) is not { } peer) return;
            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await _peerSend.WaitAsync(timeout.Token);
                try
                {
                    await peer.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, RelayCloseReason.HostLeft,
                        timeout.Token);
                }
                finally { _peerSend.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                // The connecting side is gone too, or stopped answering: its pump ends on its own.
            }
        }

        /// <summary>Completes a close the other side started (if this side hasn't closed already).</summary>
        private static async Task AnswerCloseAsync(WebSocket ws, SemaphoreSlim sendLock)
        {
            await sendLock.WaitAsync();
            try
            {
                if (ws.State == WebSocketState.CloseReceived)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException) { }
            finally { sendLock.Release(); }
        }

        private static async Task SendAsync(WebSocket ws, SemaphoreSlim sendLock, ReadOnlyMemory<byte> data,
            WebSocketMessageType type, CancellationToken ct)
        {
            await sendLock.WaitAsync(ct);
            try { await ws.SendAsync(data, type, endOfMessage: true, ct); }
            finally { sendLock.Release(); }
        }
    }
}
