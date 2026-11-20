using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Sync;

/// <summary>Whole packets both ways between two devices: through a relay, or a TCP connection.</summary>
public interface IPacketLink : IAsyncDisposable
{
    /// <summary>"relay" or "lan": how the other device is reached.</summary>
    string Kind { get; }

    Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct);

    /// <summary>The next packet, or null once the link is closed.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
}

/// <summary>Packets over a byte stream (TCP), each preceded by its length (4 bytes, big endian).</summary>
public sealed class StreamPacketLink(Stream stream, string kind = "lan") : IPacketLink
{
    public const int MaxPacket = 1 << 20;
    private readonly SemaphoreSlim _send = new(1, 1);

    public string Kind => kind;

    public async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, packet.Length);
        await _send.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(packet, ct);
            await stream.FlushAsync(ct);
        }
        finally { _send.Release(); }
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(header, ct)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxPacket) throw new IOException("Packet too large.");
        var packet = new byte[length];
        return await ReadExactlyAsync(packet, ct) ? packet : null;
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(at), ct);
            if (n == 0) return false;
            at += n;
        }
        return true;
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

/// <summary>
/// An authenticated, encrypted channel over a packet link: a Noise XX handshake, then JSON
/// messages of any size (split over Noise's 64 KiB packets). Each side learns the other's static
/// key; whether to trust it is the caller's decision.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    /// <summary>Noise transport messages are at most 65535 bytes, the tag and a flag byte included.</summary>
    private const int Chunk = 65535 - CipherState.TagSize - 1;
    private const int MaxMessage = 64 << 20;

    private readonly IPacketLink _link;
    private readonly CipherState _send;
    private readonly CipherState _receive;
    private readonly SemaphoreSlim _sending = new(1, 1);

    private SecureChannel(IPacketLink link, CipherState send, CipherState receive, byte[] remoteKey, byte[] hash, JsonObject? hello)
    {
        _link = link;
        _send = send;
        _receive = receive;
        RemoteKey = remoteKey;
        HandshakeHash = hash;
        Hello = hello;
    }

    public IPacketLink Link => _link;

    /// <summary>The other side's static public key.</summary>
    public byte[] RemoteKey { get; }

    /// <summary>The same on both sides and on no other channel: what short authentication strings come from.</summary>
    public byte[] HandshakeHash { get; }

    /// <summary>What the initiator said in its first (unencrypted) message: what the connection is for.</summary>
    public JsonObject? Hello { get; }

    private static readonly byte[] Prologue = Encoding.ASCII.GetBytes("chamelab-sync/1");

    /// <summary>Runs the handshake as the side that opened the link; <paramref name="hello"/> says what for (sent in the clear).</summary>
    public static Task<SecureChannel> ConnectAsync(IPacketLink link, KeyPair key, JsonObject hello, CancellationToken ct) =>
        HandshakeAsync(link, key, true, hello, ct);

    /// <summary>Runs the handshake as the side that was reached.</summary>
    public static Task<SecureChannel> AcceptAsync(IPacketLink link, KeyPair key, CancellationToken ct) =>
        HandshakeAsync(link, key, false, null, ct);

    private static async Task<SecureChannel> HandshakeAsync(IPacketLink link, KeyPair key, bool initiator, JsonObject? hello, CancellationToken ct)
    {
        var noise = new NoiseHandshake(initiator, key, Prologue);
        var said = hello;
        while (!noise.IsComplete)
        {
            if (noise.IsMyTurn)
            {
                // The hello rides in the initiator's first message; nothing else carries a payload.
                var payload = hello is not null ? Encoding.UTF8.GetBytes(hello.ToJsonString()) : Array.Empty<byte>();
                await link.SendAsync(noise.WriteMessage(payload), ct);
                hello = null;
            }
            else
            {
                var packet = await link.ReceiveAsync(ct) ?? throw new IOException("The other device left during the handshake.");
                var payload = noise.ReadMessage(packet);
                if (!initiator && said is null && payload.Length > 0)
                {
                    try { said = JsonNode.Parse(payload) as JsonObject; }
                    catch (System.Text.Json.JsonException) { throw new CryptographicException("Malformed handshake."); }
                }
            }
        }
        var (send, receive) = noise.Split();
        return new SecureChannel(link, send, receive, noise.RemoteStatic!, noise.HandshakeHash, said);
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sending.WaitAsync(ct);
        try
        {
            for (var at = 0; ; at += Chunk)
            {
                var length = Math.Min(Chunk, bytes.Length - at);
                var last = at + length >= bytes.Length;
                var plain = new byte[length + 1];
                plain[0] = last ? (byte)1 : (byte)0;
                bytes.AsSpan(at, length).CopyTo(plain.AsSpan(1));
                await _link.SendAsync(_send.Encrypt([], plain), ct);
                if (last) break;
            }
        }
        finally { _sending.Release(); }
    }

    /// <summary>The next message, or null once the link closed.</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken ct)
    {
        var message = new MemoryStream();
        while (true)
        {
            var packet = await _link.ReceiveAsync(ct);
            if (packet is null) return null;
            var plain = _receive.Decrypt([], packet);
            if (plain.Length == 0) throw new CryptographicException("Malformed message.");
            message.Write(plain, 1, plain.Length - 1);
            if (message.Length > MaxMessage) throw new IOException("Message too large.");
            if (plain[0] == 1) break;
        }
        return JsonNode.Parse(message.ToArray()) as JsonObject ?? throw new IOException("Malformed message.");
    }

    /// <summary>
    /// Six digits both screens show for a channel set up with a typed code: they match only when
    /// nobody (the relay included) sits in the middle.
    /// </summary>
    public string ShortAuthenticationString()
    {
        var mac = SyncCrypto.HmacSha256(HandshakeHash, Encoding.ASCII.GetBytes("chamelab-sync/sas"));
        var n = BinaryPrimitives.ReadUInt32BigEndian(mac) % 1_000_000;
        return $"{n / 1000:D3} {n % 1000:D3}";
    }

    public ValueTask DisposeAsync() => _link.DisposeAsync();
}

/// <summary>An in-memory pair of links, for tests and for connecting to oneself.</summary>
public sealed class MemoryPacketLink : IPacketLink
{
    private readonly System.Threading.Channels.Channel<byte[]> _in;
    private readonly System.Threading.Channels.Channel<byte[]> _out;

    private MemoryPacketLink(System.Threading.Channels.Channel<byte[]> inbound, System.Threading.Channels.Channel<byte[]> outbound, string kind)
    {
        _in = inbound;
        _out = outbound;
        Kind = kind;
    }

    public string Kind { get; }

    public static (MemoryPacketLink A, MemoryPacketLink B) Pair(string kind = "lan")
    {
        var ab = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var ba = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        return (new MemoryPacketLink(ba, ab, kind), new MemoryPacketLink(ab, ba, kind));
    }

    public async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        if (!_out.Writer.TryWrite(packet.ToArray())) throw new IOException("Link closed.");
        await Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _in.Reader.ReadAsync(ct);
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _out.Writer.TryComplete();
        _in.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
