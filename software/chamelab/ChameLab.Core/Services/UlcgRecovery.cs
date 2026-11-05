using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace ChameLab.Services;

public enum UlcNonceGenerator { Unknown, Giantec, Uscuid }

/// <summary>
/// Managed counterpart of mfulc_des_brute (noproto/doegox, 2025). Each stage searches
/// the 28 effective DES bits in one four-byte key segment. DES parity bits are zero.
/// </summary>
public static class UlcgRecovery
{
    public static UlcNonceGenerator Detect(byte[] zeroKeyNonce)
    {
        if (zeroKeyNonce.Length != 8) throw new ArgumentException("Nonce must contain eight bytes.");
        var des = new DesEngine(); des.Init(false, new KeyParameter(new byte[8]));
        var plain = new byte[8]; des.ProcessBlock(zeroKeyNonce, 0, plain, 0);
        return Valid(plain, UlcNonceGenerator.Giantec) ? UlcNonceGenerator.Giantec
            : Valid(plain, UlcNonceGenerator.Uscuid) ? UlcNonceGenerator.Uscuid : UlcNonceGenerator.Unknown;
    }

    public static bool Valid(ReadOnlySpan<byte> plaintext, UlcNonceGenerator generator)
    {
        if (plaintext.Length != 8) return false;
        if (generator == UlcNonceGenerator.Giantec)
        {
            var x = BinaryPrimitives.ReadUInt64BigEndian(plaintext); var seed = (ushort)(x >> 48);
            for (var shift = 32; shift >= 0; shift -= 16)
            {
                seed = (ushort)((seed << 15) | ((seed >> 1) ^ ((seed >> 3 ^ seed >> 4 ^ seed >> 6) & 1)));
                if (seed != (ushort)(x >> shift)) return false;
            }
            return true;
        }
        if (generator == UlcNonceGenerator.Uscuid)
        {
            var x = BinaryPrimitives.ReadUInt64BigEndian(plaintext); var seed = (ushort)x;
            for (var shift = 16; shift <= 48; shift += 16)
            {
                for (var i = 0; i < 16; i++) seed = (ushort)(seed >> 1 | (seed ^ seed >> 2 ^ seed >> 3 ^ seed >> 5) << 15);
                if (seed != (ushort)(x >> shift)) return false;
            }
            return true;
        }
        return false;
    }

    public static byte[]? RecoverSegment(byte[] encryptedNonce, byte[] baseKey, int segment,
        UlcNonceGenerator generator, int threads, CancellationToken ct = default,
        uint startIndex = 0, uint endIndex = 1U << 28)
    {
        if (encryptedNonce.Length != 8 || baseKey.Length != 16) throw new ArgumentException("Invalid nonce/key length.");
        if (segment is < 0 or > 3 || generator == UlcNonceGenerator.Unknown || threads < 1 || endIndex > 1U << 28 || startIndex > endIndex)
            throw new ArgumentOutOfRangeException(nameof(segment));
        byte[]? found = null;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, (worker, state) =>
        {
            var key = (byte[])baseKey.Clone(); var output = new byte[8];
            var engine = new DesEdeEngine();
            for (var index = (ulong)startIndex + (uint)worker; index < endIndex; index += (uint)threads)
            {
                if ((index / (uint)threads & 255) == 0) ct.ThrowIfCancellationRequested();
                if (state.IsStopped) break;
                for (var b = 0; b < 4; b++) key[segment * 4 + b] = (byte)(((index >> (7 * b)) & 127) << 1);
                engine.Init(false, new KeyParameter(key)); engine.ProcessBlock(encryptedNonce, 0, output, 0);
                if (!Valid(output, generator)) continue;
                Interlocked.CompareExchange(ref found, (byte[])key.Clone(), null); state.Stop(); break;
            }
        });
        return found;
    }

    public static async Task<byte[]> RecoverAsync(IReadOnlyDictionary<string, string> challenges, int threads,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var decoded = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "challenge_0", "challenge_25", "challenge_50", "challenge_75", "challenge_100" })
        {
            if (!challenges.TryGetValue(name, out var value)) throw new ArgumentException("Missing " + name);
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 8) throw new ArgumentException(name + " must contain eight bytes.");
            decoded[name] = bytes;
        }
        var generator = Detect(decoded["challenge_0"]);
        if (generator == UlcNonceGenerator.Unknown) throw new InvalidOperationException("Could not identify the nonce generator.");
        progress?.Report("Nonce generator: " + generator);
        var key = new byte[16];
        foreach (var (segment, percent) in new[] { (1, 25), (0, 50), (3, 75), (2, 100) })
        {
            progress?.Report($"Recovering key segment {segment + 1}/4 (28-bit search).");
            key = await Task.Run(() => RecoverSegment(decoded["challenge_" + percent], key, segment, generator, threads, ct), ct)
                ?? throw new InvalidOperationException($"No key found for segment {segment + 1}.");
            progress?.Report($"Segment {segment + 1}: {Convert.ToHexString(key.AsSpan(segment * 4, 4))}");
        }
        return key;
    }
}
