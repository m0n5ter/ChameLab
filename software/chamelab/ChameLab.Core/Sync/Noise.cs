using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;

namespace ChameLab.Sync;

/// <summary>
/// One direction of a Noise channel: ChaCha20-Poly1305 with a counter nonce (Noise spec §5.1).
/// </summary>
public sealed class CipherState
{
    public const int TagSize = 16;
    private byte[]? _key;
    private ulong _nonce;

    internal CipherState(byte[]? key = null) => _key = key;

    public bool HasKey => _key is not null;

    internal void InitializeKey(byte[] key)
    {
        _key = key;
        _nonce = 0;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (_key is null) return plaintext.ToArray();
        return Seal(true, ad, plaintext);
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the message was tampered with.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (_key is null) return ciphertext.ToArray();
        if (ciphertext.Length < TagSize) throw new CryptographicException("Noise message too short.");
        return Seal(false, ad, ciphertext);
    }

    private byte[] Seal(bool encrypt, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> input)
    {
        if (_nonce == ulong.MaxValue) throw new CryptographicException("Noise nonce exhausted.");
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), _nonce);
        var aead = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        aead.Init(encrypt, new AeadParameters(new KeyParameter(_key!), TagSize * 8, nonce, ad.ToArray()));
        var output = new byte[aead.GetOutputSize(input.Length)];
        try
        {
            var n = aead.ProcessBytes(input.ToArray(), 0, input.Length, output, 0);
            n += aead.DoFinal(output, n);
            _nonce++;
            return n == output.Length ? output : output[..n];
        }
        catch (InvalidCipherTextException ex)
        {
            throw new CryptographicException("Noise message failed authentication.", ex);
        }
    }
}

public enum NoisePattern { XX }

/// <summary>
/// A Noise handshake, <c>Noise_XX_25519_ChaChaPoly_SHA256</c> (revision 34): both sides prove
/// their static keys, and each learns the other's. Call <see cref="WriteMessage"/> and
/// <see cref="ReadMessage"/> in turn (the initiator writes first) until <see cref="IsComplete"/>,
/// then <see cref="Split"/>.
/// </summary>
public sealed class NoiseHandshake
{
    private const int DhLen = 32;
    private const int HashLen = 32;

    // XX: -> e ; <- e, ee, s, es ; -> s, se
    private static readonly string[][] Xx = [["e"], ["e", "ee", "s", "es"], ["s", "se"]];

    private readonly bool _initiator;
    private readonly KeyPair _s;
    private KeyPair? _e;
    private byte[]? _re;
    private readonly CipherState _cipher = new();
    private byte[] _ck;
    private byte[] _h;
    private readonly string[][] _messages;
    private int _index;

    public NoiseHandshake(bool initiator, KeyPair staticKey, ReadOnlySpan<byte> prologue = default,
        NoisePattern pattern = NoisePattern.XX, KeyPair? ephemeral = null)
    {
        _initiator = initiator;
        _s = staticKey;
        _e = ephemeral;
        _messages = pattern switch { NoisePattern.XX => Xx, _ => throw new ArgumentOutOfRangeException(nameof(pattern)) };
        var name = Encoding.ASCII.GetBytes($"Noise_{pattern}_25519_ChaChaPoly_SHA256");
        _h = name.Length <= HashLen ? Pad(name) : SyncCrypto.Sha256(name);
        _ck = _h;
        MixHash(prologue);
    }

    /// <summary>The other side's static key, known once it has sent it.</summary>
    public byte[]? RemoteStatic { get; private set; }

    public bool IsComplete => _index >= _messages.Length;

    /// <summary>Whether it is this side's turn to write.</summary>
    public bool IsMyTurn => !IsComplete && (_index % 2 == 0) == _initiator;

    /// <summary>A value both sides share once complete and nobody else can know (channel binding).</summary>
    public byte[] HandshakeHash => _h;

    private static byte[] Pad(byte[] name)
    {
        var h = new byte[HashLen];
        name.CopyTo(h, 0);
        return h;
    }

    public byte[] WriteMessage(ReadOnlySpan<byte> payload)
    {
        if (!IsMyTurn) throw new InvalidOperationException("Not this side's turn to write.");
        var output = new List<byte>();
        foreach (var token in _messages[_index])
        {
            switch (token)
            {
                case "e":
                    _e ??= KeyPair.Generate();
                    output.AddRange(_e.Public);
                    MixHash(_e.Public);
                    break;
                case "s":
                    output.AddRange(EncryptAndHash(_s.Public));
                    break;
                default:
                    Token(token);
                    break;
            }
        }
        output.AddRange(EncryptAndHash(payload));
        _index++;
        return output.ToArray();
    }

    /// <summary>The payload of the other side's message; throws <see cref="CryptographicException"/> when it is not genuine.</summary>
    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        if (IsComplete || IsMyTurn) throw new InvalidOperationException("Not this side's turn to read.");
        var at = 0;
        foreach (var token in _messages[_index])
        {
            switch (token)
            {
                case "e":
                    if (message.Length - at < DhLen) throw new CryptographicException("Noise message too short.");
                    _re = message.Slice(at, DhLen).ToArray();
                    at += DhLen;
                    MixHash(_re);
                    break;
                case "s":
                    var len = _cipher.HasKey ? DhLen + CipherState.TagSize : DhLen;
                    if (message.Length - at < len) throw new CryptographicException("Noise message too short.");
                    RemoteStatic = DecryptAndHash(message.Slice(at, len));
                    at += len;
                    break;
                default:
                    Token(token);
                    break;
            }
        }
        var payload = DecryptAndHash(message[at..]);
        _index++;
        return payload;
    }

    private void Token(string token)
    {
        switch (token)
        {
            case "ee": MixKey(SyncCrypto.Dh(_e!.Private, _re!)); break;
            case "es": MixKey(_initiator ? SyncCrypto.Dh(_e!.Private, RemoteStatic!) : SyncCrypto.Dh(_s.Private, _re!)); break;
            case "se": MixKey(_initiator ? SyncCrypto.Dh(_s.Private, _re!) : SyncCrypto.Dh(_e!.Private, RemoteStatic!)); break;
            case "ss": MixKey(SyncCrypto.Dh(_s.Private, RemoteStatic!)); break;
            default: throw new InvalidOperationException($"Unknown Noise token {token}.");
        }
    }

    /// <summary>The two transport ciphers: this side's sending one, then its receiving one.</summary>
    public (CipherState Send, CipherState Receive) Split()
    {
        if (!IsComplete) throw new InvalidOperationException("Handshake not complete.");
        var (k1, k2) = Hkdf2(_ck, ReadOnlySpan<byte>.Empty);
        var c1 = new CipherState(k1[..32]);
        var c2 = new CipherState(k2[..32]);
        return _initiator ? (c1, c2) : (c2, c1);
    }

    private void MixHash(ReadOnlySpan<byte> data)
    {
        var buffer = new byte[_h.Length + data.Length];
        _h.CopyTo(buffer, 0);
        data.CopyTo(buffer.AsSpan(_h.Length));
        _h = SyncCrypto.Sha256(buffer);
    }

    private void MixKey(ReadOnlySpan<byte> ikm)
    {
        var (ck, k) = Hkdf2(_ck, ikm);
        _ck = ck;
        _cipher.InitializeKey(k[..32]);
    }

    private byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ct = _cipher.Encrypt(_h, plaintext);
        MixHash(ct);
        return ct;
    }

    private byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var pt = _cipher.Decrypt(_h, ciphertext);
        MixHash(ciphertext);
        return pt;
    }

    /// <summary>Noise's HKDF with two outputs.</summary>
    private static (byte[], byte[]) Hkdf2(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> ikm)
    {
        var temp = SyncCrypto.HmacSha256(chainingKey, ikm);
        var out1 = SyncCrypto.HmacSha256(temp, [0x01]);
        var input2 = new byte[out1.Length + 1];
        out1.CopyTo(input2, 0);
        input2[^1] = 0x02;
        var out2 = SyncCrypto.HmacSha256(temp, input2);
        return (out1, out2);
    }
}
