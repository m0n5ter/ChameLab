using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Services;

namespace ChameLab.Transport;

/// <summary>
/// A device someone shares through the relay (ChameLab.Relay), joined by the code they were
/// shown. The relay passes the device's raw bytes in binary WebSocket messages; framing stays
/// with <see cref="Protocol.ChameleonConnection"/> as on any other transport.
/// Works in the browser too: ClientWebSocket is available there, unlike TCP sockets.
/// </summary>
public sealed class WebSocketTransport(RelayServer relay, ShareCode code) : ITransport
{
    private ClientWebSocket? _ws;
    // One send at a time. A send is never canceled: canceling a WebSocket send aborts the socket,
    // so a command timeout would take the whole link down. A caller that gives up stops waiting,
    // and the lock is released once the send really ends.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Receives aren't canceled either: that too aborts the socket, and with it the close handshake
    // that tells the relay this side left on purpose. A read that gives up leaves its receive
    // running; the next read (or the close) picks it up.
    private readonly byte[] _received = new byte[8 * 1024];
    private Task<ValueWebSocketReceiveResult>? _receive;
    private int _receivedOffset;
    private int _receivedCount;

    public TransportKind Kind => TransportKind.Remote;
    public string DisplayName => string.Format(Strings.Relay_DisplayName, code);
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var ws = RelayEndpoint.CreateSocket(relay);
        _ws = ws;
        try
        {
            await ws.ConnectAsync(RelayEndpoint.Join(relay.Url, code.Code, RelayEndpoint.ClientName), ct);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            throw new IOException(string.Format(Strings.Relay_Unreachable, relay.Url.Host), ex);
        }

        // The relay confirms the code with "joined", or closes with the reason it was refused.
        // Device bytes can't come first in practice, but would be harmless: skip them.
        var buffer = new byte[1024];
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw ClosedError(ws);
            if (r.MessageType == WebSocketMessageType.Text
                && RelayEndpoint.MessageType(buffer.AsSpan(0, r.Count)) == "joined")
                return;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var ws = _ws ?? throw new InvalidOperationException(Strings.Tcp_NotConnected);
        await _sendLock.WaitAsync(ct);
        Task send;
        try { send = ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).AsTask(); }
        catch
        {
            _sendLock.Release();
            throw;
        }
        _ = send.ContinueWith(_ => _sendLock.Release(), TaskScheduler.Default);
        await send.WaitAsync(ct);
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var ws = _ws ?? throw new InvalidOperationException(Strings.Tcp_NotConnected);
        while (true)
        {
            if (_receivedCount > 0)
            {
                var n = Math.Min(_receivedCount, buffer.Length);
                _received.AsMemory(_receivedOffset, n).CopyTo(buffer);
                _receivedOffset += n;
                _receivedCount -= n;
                return n;
            }

            _receive ??= ws.ReceiveAsync(_received.AsMemory(), CancellationToken.None).AsTask();
            var r = await _receive.WaitAsync(ct);
            _receive = null;
            if (r.MessageType == WebSocketMessageType.Close) throw ClosedError(ws);
            // Text: relay notices meant for the sharing side; nothing for the device stream.
            if (r.MessageType != WebSocketMessageType.Binary) continue;
            _receivedOffset = 0;
            _receivedCount = r.Count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is not { } ws) return;
        _ws = null;
        if (ws.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                // The relay answers with its own close; a receive still running takes it in.
                if (_receive is { } pending) await pending.WaitAsync(timeout.Token);
            }
            catch { /* the link is going away anyway */ }
        }
        ws.Dispose();
    }

    private static IOException ClosedError(WebSocket ws) => new(RelayEndpoint.CloseMessage(ws.CloseStatusDescription));
}

