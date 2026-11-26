using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Crypto;

namespace ChameLab.Services;

/// <summary>
/// Recovers a MIFARE Classic sector key from two captured reader authentications
/// (mfkey32, Moebius two-different-nonce variant) using the crapto1 port.
/// This replaces copying an <c>mfkey32v2</c> command to run elsewhere.
/// </summary>
public static class Mfkey32Recovery
{
    /// <summary>
    /// Recovers every distinct reader key in a collection, including different keys for the same block.
    /// Exchanges explained by a recovered key are skipped while the remaining exchanges are paired.
    /// </summary>
    public static async Task<IReadOnlyList<(DetectionLog Log, ulong Key)>> RecoverAllAsync(
        IReadOnlyList<DetectionLog> logs, IKeyRecoveryEngine engine, CancellationToken ct = default,
        ISet<string>? attempted = null)
    {
        var found = new List<(DetectionLog, ulong)>();
        foreach (var group in logs.Where(l => !l.IsNested)
                     .GroupBy(l => (l.UidHex, l.Block, l.KeyType)))
        {
            var records = group.DistinctBy(l => (l.NtHex, l.NrHex, l.ArHex)).ToArray();
            var covered = new bool[records.Length];
            var keys = new HashSet<ulong>();
            for (var i = 0; i < records.Length; i++)
            {
                if (covered[i]) continue;
                for (var j = i + 1; j < records.Length; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (covered[j]) continue;
                    var a = records[i];
                    var b = records[j];
                    if (attempted is not null && !attempted.Add(
                            $"{a.UidHex}:{a.Block}:{a.KeyChar}:{a.NtHex}{a.NrHex}{a.ArHex}:{b.NtHex}{b.NrHex}{b.ArHex}"))
                        continue;
                    var recovered = await engine.RecoverAsync(new(KeyRecoveryOperation.Mfkey32)
                        { Logs = [a, b] }, ct);
                    ct.ThrowIfCancellationRequested();
                    if (recovered.Count == 0) continue;
                    var key = recovered[0];
                    if (keys.Add(key)) found.Add((a, key));
                    covered[i] = covered[j] = true;
                    for (var k = 0; k < records.Length; k++)
                        if (!covered[k] && Matches(records[k], key)) covered[k] = true;
                    break;
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return found;
    }

    private static bool Matches(DetectionLog log, ulong key)
    {
        if (log.Uid.Length is not (4 or 7 or 10) || log.Nt.Length != 4
            || log.Nr.Length != 4 || log.Ar.Length != 4)
            return false;
        var nt = ToUInt32(log.Nt);
        var state = new Crypto1State(key);
        state.Crypto1Word(ToUInt32(log.Uid) ^ nt);
        state.Crypto1Word(ToUInt32(log.Nr), true);
        return ToUInt32(log.Ar) == (state.Crypto1Word() ^ Crypto1.PrngSuccessor(nt, 64));
    }

    /// <summary>
    /// Computes the sector key from two distinct <paramref name="a"/>/<paramref name="b"/>
    /// auth exchanges on the same block and key type. Returns the 48-bit key, or
    /// <c>null</c> when no (or more than one) candidate validates; capture more nonces then.
    /// </summary>
    public static ulong? Recover(DetectionLog a, DetectionLog b)
    {
        // The C command receives words for one card/authentication type. Reject
        // malformed or unrelated records rather than silently padding/truncating them.
        if (a.IsNested || b.IsNested || a.Block != b.Block || a.KeyType != b.KeyType
            || !a.Uid.AsSpan().SequenceEqual(b.Uid) || a.Uid.Length is not (4 or 7 or 10)
            || a.Nt.Length != 4 || a.Nr.Length != 4 || a.Ar.Length != 4
            || b.Nt.Length != 4 || b.Nr.Length != 4 || b.Ar.Length != 4)
            return null;
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

    /// <summary>
    /// Recovers the key of a sniffed authentication group: mfkey64 from an exchange the card answered (its
    /// encrypted <c>at</c> pins the key down from one run), else mfkey32 from two exchanges. Null when there is
    /// neither, or no pair gives a unique key.
    /// </summary>
    public static (ulong Key, string Method)? Recover(HfAuthGroup group)
    {
        if (group.Exchanges.FirstOrDefault(e => e.AtEnc is not null) is { AtEnc: { } at } full)
            return (MfKey.MfKey64(group.Uid, full.Nt, full.NrEnc, full.ArEnc, at), "mfkey64");
        var list = group.Exchanges;
        for (var i = 0; i < list.Count; i++)
        for (var j = i + 1; j < list.Count; j++)
        {
            if (list[i].Nt == list[j].Nt && list[i].NrEnc == list[j].NrEnc) continue;
            var key = MfKey.MfKey32(group.Uid, list[i].Nt, list[i].NrEnc, list[i].ArEnc,
                list[j].Nt, list[j].NrEnc, list[j].ArEnc);
            if (key != ulong.MaxValue) return (key, "mfkey32");
        }
        return null;
    }

    /// <summary>Reads a 4-byte big-endian word (MSB first), matching the device's hex order.</summary>
    private static uint ToUInt32(byte[] value)
    {
        // ISO14443 authentication uses the last cascade level (last four UID bytes).
        return BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(value.Length - 4));
    }
}
