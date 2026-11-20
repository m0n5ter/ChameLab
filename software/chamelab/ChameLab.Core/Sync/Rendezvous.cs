using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Transport;

namespace ChameLab.Sync;

/// <summary>
/// What one device shows another to connect once: the relay code to type
/// (<c>DE1-ABCD-1234</c>) and, in the QR form, what makes typing-free connections safe and fast:
/// a hash of the showing device's key, a secret the relay never sees, and its local addresses.
/// </summary>
public sealed record PairingCode(
    ShareCode? Relay,
    byte[]? KeyHash,
    byte[]? Secret,
    IReadOnlyList<IPEndPoint> Lan)
{
    public const string QrPrefix = "chamelab-sync:";

    /// <summary>Reads a scanned QR or a typed code; null when it is neither.</summary>
    public static PairingCode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.StartsWith(QrPrefix, StringComparison.OrdinalIgnoreCase))
            return ShareCode.Parse(text) is { } typed ? new PairingCode(typed, null, null, Array.Empty<IPEndPoint>()) : null;
        try
        {
            if (JsonNode.Parse(SyncCrypto.FromB64(text[QrPrefix.Length..])) is not JsonObject o || (int?)o["v"] != 1) return null;
            var lan = new List<IPEndPoint>();
            if (o["lan"] is JsonArray endpoints)
                foreach (var e in endpoints)
                    if (IPEndPoint.TryParse((string?)e ?? "", out var endpoint) && endpoint.Port > 0) lan.Add(endpoint);
            return new PairingCode(
                ShareCode.Parse((string?)o["code"]),
                (string?)o["key"] is { } key ? SyncCrypto.FromB64(key) : null,
                (string?)o["secret"] is { } secret ? SyncCrypto.FromB64(secret) : null,
                lan);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public string ToQr()
    {
        var o = new JsonObject { ["v"] = 1 };
        if (Relay is { } relay) o["code"] = relay.ToString();
        if (KeyHash is not null) o["key"] = SyncCrypto.B64(KeyHash);
        if (Secret is not null) o["secret"] = SyncCrypto.B64(Secret);
        if (Lan.Count > 0) o["lan"] = new JsonArray(Lan.Select(e => (JsonNode)e.ToString()).ToArray());
        return QrPrefix + SyncCrypto.B64(Encoding.UTF8.GetBytes(o.ToJsonString()));
    }

    public static byte[] HashKey(byte[] publicKey) => SyncCrypto.Sha256(publicKey)[..16];

    /// <summary>What the connecting side sends to show it read the QR (not just the relay code).</summary>
    public static string Proof(byte[] secret, SecureChannel channel) =>
        SyncCrypto.B64(SyncCrypto.HmacSha256(secret, channel.HandshakeHash));
}

/// <summary>
/// The showing side of a one-time connection (adding a device, sending cards): a share on the
/// fastest relay and, where the platform allows, a listener on the local network. The first
/// device to complete a handshake on either, for the same <see cref="Purpose"/>, is the one.
/// </summary>
public sealed class Rendezvous : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly KeyPair _key;
    private readonly byte[] _secret = SyncCrypto.Random(16);
    private readonly RelayPipe? _pipe;
    private readonly LanListener? _lan;
    private readonly TaskCompletionSource<SecureChannel> _channel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();

    private Rendezvous(string purpose, KeyPair key, RelayPipe? pipe, ShareCode? relay, bool lan)
    {
        Purpose = purpose;
        _key = key;
        _pipe = pipe;
        if (lan)
        {
            try { _lan = new LanListener(link => AcceptAsync(link)); }
            catch (System.Net.Sockets.SocketException) { _lan = null; }
        }
        var endpoints = _lan?.Endpoints.Select(IPEndPoint.Parse).ToList() ?? new List<IPEndPoint>();
        Code = new PairingCode(relay, PairingCode.HashKey(key.Public), _secret, endpoints);
    }

    public string Purpose { get; }
    public PairingCode Code { get; }

    /// <summary>The code to type on the other device; null when no relay could be reached.</summary>
    public string? TypedCode => Code.Relay?.ToString();

    public string Qr => Code.ToQr();

    /// <summary>Opens the share and the listener; throws when neither the relay nor the local network can be used.</summary>
    public static async Task<Rendezvous> OpenAsync(string purpose, KeyPair key, IReadOnlyList<RelayServer> relays, CancellationToken ct)
    {
        RelayPipe? pipe = null;
        ShareCode? code = null;
        Exception? relayError = null;
        try
        {
            var server = await RelayEndpoint.PickFastestAsync(relays, ct);
            (pipe, code) = await RelayPipe.ShareAsync(server, ct);
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or System.Net.Http.HttpRequestException)
        {
            relayError = ex;
        }
        var rendezvous = new Rendezvous(purpose, key, pipe, code, lan: !OperatingSystem.IsBrowser());
        if (pipe is null && rendezvous._lan is null)
            throw new IOException(relayError?.Message ?? Strings.Relay_NoServers);
        if (pipe is not null) _ = rendezvous.RelayAsync(pipe);
        return rendezvous;
    }

    private async Task RelayAsync(RelayPipe pipe)
    {
        try
        {
            await pipe.WaitForPeerAsync(_stop.Token);
            await AcceptAsync(new StreamPacketLink(pipe, "relay"));
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            if (_lan is null) _channel.TrySetException(ex);
        }
    }

    private async Task AcceptAsync(IPacketLink link)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(HandshakeTimeout);
            var channel = await SecureChannel.AcceptAsync(link, _key, timeout.Token);
            if ((string?)channel.Hello?["p"] != Purpose || !_channel.TrySetResult(channel))
                await channel.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or OperationCanceledException
                                       or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException)
        {
            await link.DisposeAsync();
        }
    }

    /// <summary>The other device, once it connected and completed the handshake.</summary>
    public async Task<SecureChannel> WaitAsync(CancellationToken ct) => await _channel.Task.WaitAsync(ct);

    /// <summary>Whether the other side proved it read the QR (then no digits need comparing).</summary>
    public bool IsProven(SecureChannel channel, JsonObject message) =>
        (string?)message["proof"] is { } proof
        && SyncCrypto.Equal(Encoding.ASCII.GetBytes(proof), Encoding.ASCII.GetBytes(PairingCode.Proof(_secret, channel)));

    /// <summary>
    /// The connecting side: reaches the showing device over the local network when the QR lists
    /// addresses (and one answers), else through the relay. Returns the channel and whether the
    /// showing device is verified by the QR's key hash (when not, the six digits must be compared).
    /// </summary>
    public static async Task<(SecureChannel Channel, bool Verified)> ConnectAsync(
        PairingCode code, string purpose, KeyPair key, IReadOnlyList<RelayServer> relays, CancellationToken ct)
    {
        IPacketLink? link = null;
        if (code.Lan.Count > 0 && !OperatingSystem.IsBrowser())
            link = await LanListener.ConnectAsync(code.Lan, TimeSpan.FromSeconds(2), ct);
        if (link is null)
        {
            if (code.Relay is not { } relay) throw new IOException(Strings.Sync_DeviceNotReachable);
            var server = RelayEndpoint.Find(relay.Region, relays)
                         ?? throw new FormatException(string.Format(Strings.Relay_UnknownRegion, relay.Region));
            link = new StreamPacketLink(await RelayPipe.JoinAsync(server, relay.Code, ct), "relay");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        SecureChannel channel;
        try
        {
            channel = await SecureChannel.ConnectAsync(link, key, new JsonObject { ["p"] = purpose }, timeout.Token);
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
        if (code.KeyHash is { } hash && !SyncCrypto.Equal(hash, PairingCode.HashKey(channel.RemoteKey)))
        {
            await channel.DisposeAsync();
            throw new CryptographicException(Strings.Sync_WrongDevice);
        }
        return (channel, code.KeyHash is not null);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _lan?.Dispose();
        _channel.TrySetCanceled();
        // The pipe stays open when it carries the channel the caller now owns.
        var carries = _channel.Task.IsCompletedSuccessfully && _channel.Task.Result.Link.Kind == "relay";
        if (_pipe is not null && !carries) await _pipe.DisposeAsync();
    }
}
