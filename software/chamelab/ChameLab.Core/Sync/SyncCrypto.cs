using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace ChameLab.Sync;

/// <summary>
/// The primitives sync needs, all from BouncyCastle so they behave the same on every platform,
/// WebAssembly included (where <see cref="System.Security.Cryptography"/> lacks most of them).
/// </summary>
public static class SyncCrypto
{
    public const int KeySize = 32;

    public static byte[] Random(int length) => RandomNumberGenerator.GetBytes(length);

    public static byte[] Sha256(ReadOnlySpan<byte> data)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(data);
        var hash = new byte[32];
        digest.DoFinal(hash);
        return hash;
    }

    public static byte[] HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var hmac = new HMac(new Sha256Digest());
        hmac.Init(new KeyParameter(key));
        hmac.BlockUpdate(data);
        var mac = new byte[32];
        hmac.DoFinal(mac, 0);
        return mac;
    }

    /// <summary>HKDF-SHA256 (RFC 5869).</summary>
    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length) =>
        Hkdf(ikm, salt, Encoding.UTF8.GetBytes(info), length);

    /// <inheritdoc cref="Hkdf(ReadOnlySpan{byte}, ReadOnlySpan{byte}, string, int)"/>
    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        hkdf.Init(new HkdfParameters(ikm.ToArray(), salt.ToArray(), info.ToArray()));
        var output = new byte[length];
        hkdf.GenerateBytes(output, 0, length);
        return output;
    }

    /// <summary>X25519 (RFC 7748); throws when the result is all zeros (a small-order point).</summary>
    public static byte[] Dh(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        var shared = new byte[X25519.PointSize];
        if (!X25519.CalculateAgreement(privateKey.ToArray(), 0, publicKey.ToArray(), 0, shared, 0))
            throw new CryptographicException("Invalid X25519 public key.");
        return shared;
    }

    public static byte[] PublicKey(ReadOnlySpan<byte> privateKey)
    {
        var pub = new byte[X25519.PointSize];
        X25519.ScalarMultBase(privateKey.ToArray(), 0, pub, 0);
        return pub;
    }

    /// <summary>Constant-time comparison.</summary>
    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>Base64url without padding, the encoding of every binary value in sync's JSON.</summary>
    public static string B64(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}

/// <summary>An X25519 key pair.</summary>
public sealed class KeyPair
{
    public byte[] Private { get; }
    public byte[] Public { get; }

    public KeyPair(byte[] privateKey)
    {
        if (privateKey.Length != SyncCrypto.KeySize) throw new ArgumentException("X25519 keys are 32 bytes.");
        Private = privateKey;
        Public = SyncCrypto.PublicKey(privateKey);
    }

    public static KeyPair Generate() => new(SyncCrypto.Random(SyncCrypto.KeySize));
}
