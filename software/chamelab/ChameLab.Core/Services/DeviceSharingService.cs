using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Transport;

namespace ChameLab.Services;

public enum SharingState
{
    Idle,
    /// <summary>Opening the device and asking the relay for a code.</summary>
    Starting,
    /// <summary>Shared: the code is shown, nobody has joined yet (or the last one left).</summary>
    WaitingForPeer,
    /// <summary>Someone joined with the code and is using the device.</summary>
    PeerConnected,
}

/// <summary>
/// Shares a locally attached device through the relay (ChameLab.Relay): opens the device's
/// transport, gets a code from the relay, and copies raw bytes between the two until stopped.
/// The device stays opened by the share the whole time, so it can't be connected locally then.
/// The computer is kept awake meanwhile: nobody touches it while someone else uses the device,
/// and a sleeping (or display-off standby) computer drops the link to the relay.
/// </summary>
public sealed class DeviceSharingService(DeviceService device, WakeLockService? wakeLock = null)
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _stop;
    private Task? _run;
    private long _bytesToDevice;
    private long _bytesFromDevice;

    public SharingState State { get; private set; }

    /// <summary>The code to give to whoever should connect, e.g. TR1-ABCD-1234.</summary>
    public string? Code { get; private set; }

    /// <summary>Who is connected, as their app introduced itself (user@computer); null when unknown or nobody.</summary>
    public string? PeerName { get; private set; }

    /// <summary>The relay holding the share: the one with the lowest round trip.</summary>
    public RelayServer? Server { get; private set; }

    /// <summary>The device being shared.</summary>
    public TransportCandidate? Candidate { get; private set; }

    /// <summary>Why the last share ended by itself (device unplugged, relay gone); null after Stop.</summary>
    public string? Error { get; private set; }

    /// <summary>Bytes the connecting side sent to the device during this share.</summary>
    public long BytesToDevice => Interlocked.Read(ref _bytesToDevice);

    /// <summary>Bytes the device sent back during this share (to the relay, whether anyone was connected or not).</summary>
    public long BytesFromDevice => Interlocked.Read(ref _bytesFromDevice);

    /// <summary>Raised on any thread whenever <see cref="State"/>, <see cref="Code"/> or <see cref="Error"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Shares <paramref name="candidate"/> through the fastest of <paramref name="servers"/> (default: the known relays).</summary>
    public async Task StartAsync(TransportCandidate candidate, IReadOnlyList<RelayServer>? servers = null,
        CancellationToken ct = default)
    {
        if (State != SharingState.Idle) throw new InvalidOperationException(Strings.Share_AlreadySharing);
        if (candidate.Kind == TransportKind.Remote) throw new InvalidOperationException(Strings.Share_NotLocal);

        Error = null;
        Candidate = candidate;
        Interlocked.Exchange(ref _bytesToDevice, 0);
        Interlocked.Exchange(ref _bytesFromDevice, 0);
        SetState(SharingState.Starting);

        // The share needs the port to itself.
        if (device.State != ConnectionState.Disconnected) await device.DisconnectAsync();

        var transport = device.Scanners.First(s => s.Kind == candidate.Kind).Create(candidate);
        ClientWebSocket? ws = null;
        string code;
        try
        {
            await transport.ConnectAsync(ct);
            var server = await RelayEndpoint.PickFastestAsync(servers ?? RelayEndpoint.Servers, ct);
            ws = RelayEndpoint.CreateSocket(server);
            try { await ws.ConnectAsync(RelayEndpoint.Share(server.Url), ct); }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                throw new IOException(string.Format(Strings.Relay_Unreachable, server.Url.Host), ex);
            }

            var buffer = new byte[1024];
            var r = await ws.ReceiveAsync(buffer, ct);
            var relayCode = r.MessageType == WebSocketMessageType.Text
                ? RelayEndpoint.Property(buffer.AsSpan(0, r.Count), "code")
                : null;
            code = ShareCode.Parse(server.Region + relayCode)?.ToString()
                   ?? throw new IOException(Strings.Relay_UnexpectedReply);
            Server = server;
        }
        catch
        {
            ws?.Dispose();
            await transport.DisposeAsync();
            Candidate = null;
            SetState(SharingState.Idle);
            throw;
        }

        var awake = wakeLock is null ? null : await wakeLock.AcquireAsync();
        Code = code;
        _stop = new CancellationTokenSource();
        SetState(SharingState.WaitingForPeer);
        // Off the caller's (UI) context: the pumps run for as long as the share does.
        var token = _stop.Token;
        var socket = ws!;
        _run = Task.Run(() => RunAsync(transport, socket, awake, token));
    }

    /// <summary>Ends the share: the code stops working and whoever is connected is disconnected.</summary>
    public async Task StopAsync()
    {
        if (_stop is not { } stop || _run is not { } run) return;
        await stop.CancelAsync();
        await run;
    }

    private async Task RunAsync(ITransport transport, ClientWebSocket ws, IAsyncDisposable? awake,
        CancellationToken stop)
    {
        // Only the device pump sends data; the closing handshake takes the same lock so the
        // two never overlap. Sends aren't canceled: canceling a WebSocket send aborts the socket.
        var sendLock = new SemaphoreSlim(1, 1);
        // The relay pump isn't stopped with the share: canceling its receive would abort the socket
        // before the close handshake. It ends on the relay's answering close, or when the socket goes.
        using var end = new CancellationTokenSource();
        using var deviceEnd = CancellationTokenSource.CreateLinkedTokenSource(stop, end.Token);
        var up = PumpDeviceAsync(transport, ws, sendLock, deviceEnd.Token);
        var down = PumpRelayAsync(transport, ws, end.Token);
        var stopped = Task.Delay(Timeout.Infinite, stop);

        var first = await Task.WhenAny(up, down, stopped);
        var error = stop.IsCancellationRequested
            ? null
            : first.Exception?.GetBaseException() is IOException io ? io.Message : Strings.Relay_Closed;
        await deviceEnd.CancelAsync();

        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            await sendLock.WaitAsync(timeout.Token);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            finally { sendLock.Release(); }
            // The relay answers with its own close, which ends the relay pump.
            await Task.WhenAny(down, Task.Delay(Timeout.Infinite, timeout.Token));
        }
        catch { /* the relay is gone already */ }

        await end.CancelAsync();
        ws.Dispose();
        await transport.DisposeAsync();
        try { await Task.WhenAll(up, down); }
        catch { /* reported through Error */ }
        if (awake is not null) await awake.DisposeAsync();

        if (error is not null) Console.WriteLine($"[Share] {Candidate?.Id} ended: {error}");
        _stop?.Dispose();
        _stop = null;
        _run = null;
        Code = null;
        PeerName = null;
        Server = null;
        Candidate = null;
        Error = error;
        SetState(SharingState.Idle);
    }

    /// <summary>Device → relay. Throws an <see cref="IOException"/> saying which side failed.</summary>
    private async Task PumpDeviceAsync(ITransport transport, ClientWebSocket ws, SemaphoreSlim sendLock,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await transport.ReadAsync(buffer, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new IOException(Strings.Share_DeviceLost, ex);
            }
            if (n <= 0) continue;

            await sendLock.WaitAsync(ct);
            Interlocked.Add(ref _bytesFromDevice, n);
            try { await ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                throw new IOException(Strings.Relay_Closed, ex);
            }
            finally { sendLock.Release(); }
        }
    }

    /// <summary>Relay → device, plus the relay's notices about who is connected.</summary>
    private async Task PumpRelayAsync(ITransport transport, ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        while (!ct.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, ct); }
            catch (Exception ex) when (ex is WebSocketException && !ct.IsCancellationRequested)
            {
                throw new IOException(Strings.Relay_Closed, ex);
            }

            switch (r.MessageType)
            {
                case WebSocketMessageType.Close:
                    throw new IOException(Strings.Relay_Closed);
                case WebSocketMessageType.Text:
                    var message = buffer.AsSpan(0, r.Count);
                    switch (RelayEndpoint.MessageType(message))
                    {
                        case "peer-joined":
                            PeerName = RelayEndpoint.Property(message, "name");
                            SetState(SharingState.PeerConnected);
                            break;
                        case "peer-left":
                            PeerName = null;
                            SetState(SharingState.WaitingForPeer);
                            break;
                    }
                    break;
                case WebSocketMessageType.Binary when r.Count > 0:
                    Interlocked.Add(ref _bytesToDevice, r.Count);
                    try { await transport.WriteAsync(buffer.AsMemory(0, r.Count), ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException(Strings.Share_DeviceLost, ex);
                    }
                    break;
            }
        }
    }

    private void SetState(SharingState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
