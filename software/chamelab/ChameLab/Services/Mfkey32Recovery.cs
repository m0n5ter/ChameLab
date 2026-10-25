using System;
using System.Buffers.Binary;
using ChameLab.Models;
using Crapto1Sharp;

namespace ChameLab.Services;

/// <summary>
/// Recovers a MIFARE Classic sector key from two captured reader authentications
/// (mfkey32, Moebius two-different-nonce variant) using the crapto1 port.
/// This replaces copying an <c>mfkey32v2</c> command to run elsewhere.
/// </summary>
public static class Mfkey32Recovery
{
    /// <summary>
    /// Computes the sector key from two distinct <paramref name="a"/>/<paramref name="b"/>
    /// auth exchanges on the same block and key type. Returns the 48-bit key, or
    /// <c>null</c> when no (or more than one) candidate validates — capture more nonces then.
    /// </summary>
    public static ulong? Recover(DetectionLog a, DetectionLog b)
    {
        var uid = ToUInt32(a.Uid);
        var nt0 = ToUInt32(a.Nt);
        var nr0 = ToUInt32(a.Nr);
        var ar0 = ToUInt32(a.Ar);
        var nt1 = ToUInt32(b.Nt);
        var nr1 = ToUInt32(b.Nr);
        var ar1 = ToUInt32(b.Ar);

        var key = MfKey.MfKey32(uid, nt0, nr0, ar0, nt1, nr1, ar1);
        return key == ulong.MaxValue ? null : key;
    }

    /// <summary>Reads a 4-byte big-endian word (MSB first), matching the device's hex order.</summary>
    private static uint ToUInt32(byte[] value)
    {
        if (value.Length == 4)
            return BinaryPrimitives.ReadUInt32BigEndian(value);

        // Be forgiving about length: right-align into 4 bytes (big-endian).
        Span<byte> buf = stackalloc byte[4];
        var n = Math.Min(value.Length, 4);
        value.AsSpan(value.Length - n).CopyTo(buf[(4 - n)..]);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }
}
