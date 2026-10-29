using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

public enum HfDirection { Reader, Card }

/// <summary>
/// One ISO14443-A frame of a capture, parity bits removed. <see cref="Bits"/> is under 8 for a short
/// frame (REQA, WUPA: 7 bits) and a multiple of 8 otherwise.
/// </summary>
public sealed record HfFrame(HfDirection Direction, int Bits, byte[] Data)
{
    public bool IsReader => Direction == HfDirection.Reader;
    public bool IsShort => Bits is > 0 and < 8;
    public string Hex => Convert.ToHexString(Data);

    /// <summary>Whether the last two bytes are the CRC_A of the rest; null for frames too short to carry one.</summary>
    public bool? CrcOk => Data.Length < 3 || IsShort ? null : HfSniff.CrcA(Data.AsSpan(0, Data.Length - 2)) ==
        (ushort)(Data[^2] | Data[^1] << 8);
}

/// <summary>A frame with what it means: the command or answer, or that it is encrypted.</summary>
public sealed record HfAnnotatedFrame(int Index, HfFrame Frame, string Label, bool IsEncrypted);

/// <summary>
/// One MIFARE Classic authentication seen in full: the card nonce, the reader's encrypted nonce and answer,
/// and (when the card accepted the reader) its encrypted answer. Words are big-endian, as the frames carry them.
/// </summary>
public sealed record HfAuthExchange(uint Uid, int Block, MfKeyType KeyType, uint Nt, uint NrEnc, uint ArEnc, uint? AtEnc)
{
    public char KeyChar => KeyType == MfKeyType.A ? 'A' : 'B';
}

/// <summary>Authentications on the same card, block and key: two of them are what mfkey32 needs.</summary>
public sealed record HfAuthGroup(uint Uid, int Block, MfKeyType KeyType, IReadOnlyList<HfAuthExchange> Exchanges)
{
    public char KeyChar => KeyType == MfKeyType.A ? 'A' : 'B';
    public int Sector => MifareClassic.SectorOfBlock(Block);
}

/// <summary>What the capture tells about the card and the reader.</summary>
public sealed record HfSniffSummary(
    int ReaderFrames, int CardFrames, byte[]? Uid, byte[]? Atqa, byte? Sak, bool Rats,
    IReadOnlyList<string> Aids, IReadOnlyList<(int Block, MfKeyType KeyType)> Auths,
    bool Arqc, bool Tc, bool Halted);

/// <summary>
/// Decoding of an ISO14443-A capture (<c>HF14A_SNIFF</c>), after ChameleonUltraGUI's HF sniffing tool. The device
/// packs frames as <c>[bits:be16][bytes…]</c>, bit 15 set for its own (card) answers; frames of whole bytes carry
/// a parity bit after each byte (9 bits per byte, least significant bit first). Captures are also read from
/// and written to Proxmark3 <c>.trace</c> files.
/// </summary>
public static class HfSniff
{
    // ---- Chameleon buffer ----------------------------------------------

    public static List<HfFrame> ParseChameleon(ReadOnlySpan<byte> buffer)
    {
        var frames = new List<HfFrame>();
        var o = 0;
        while (o + 2 <= buffer.Length)
        {
            var header = BinaryPrimitives.ReadUInt16BigEndian(buffer[o..]);
            o += 2;
            var rawBits = header & 0x7FFF;
            if (rawBits == 0) break;
            var rawBytes = (rawBits + 7) / 8;
            if (o + rawBytes > buffer.Length) break;
            var raw = buffer.Slice(o, rawBytes);
            o += rawBytes;
            var direction = (header & 0x8000) != 0 ? HfDirection.Card : HfDirection.Reader;
            frames.Add(rawBits >= 9 && rawBits % 9 == 0
                ? new HfFrame(direction, rawBits / 9 * 8, StripParity(raw, rawBits / 9))
                : new HfFrame(direction, rawBits, raw.ToArray()));
        }
        return frames;
    }

    /// <summary>Takes the data bits out of a frame sent with a parity bit after each byte.</summary>
    private static byte[] StripParity(ReadOnlySpan<byte> raw, int count)
    {
        var data = new byte[count];
        for (var i = 0; i < count; i++)
        for (var bit = 0; bit < 8; bit++)
        {
            var at = i * 9 + bit;
            if ((raw[at / 8] >> (at % 8) & 1) != 0) data[i] |= (byte)(1 << bit);
        }
        return data;
    }

    // ---- Proxmark3 trace -------------------------------------------------

    /// <summary>
    /// Reads a Proxmark3 trace: records of <c>timestamp:le32, duration:le16, length:le16</c> (bit 15 set for a
    /// tag answer), the bytes, then their parity bits. A one-byte frame whose parity byte is 1–7 is a short
    /// frame of that many bits.
    /// </summary>
    public static List<HfFrame> ParseTrace(ReadOnlySpan<byte> trace)
    {
        var frames = new List<HfFrame>();
        var o = 0;
        while (o + 8 <= trace.Length)
        {
            var field = BinaryPrimitives.ReadUInt16LittleEndian(trace[(o + 6)..]);
            var length = field & 0x7FFF;
            var parityLength = length == 0 ? 1 : (length - 1) / 8 + 1;
            if (o + 8 + length + parityLength > trace.Length) break;
            var data = trace.Slice(o + 8, length).ToArray();
            var parity = trace[o + 8 + length];
            o += 8 + length + parityLength;
            var bits = length == 1 && parity is >= 1 and <= 7 ? parity : length * 8;
            frames.Add(new HfFrame((field & 0x8000) != 0 ? HfDirection.Card : HfDirection.Reader, bits, data));
        }
        return frames;
    }

    /// <summary>Writes frames as a Proxmark3 trace with computed parity and timestamps spaced by frame length.</summary>
    public static byte[] BuildTrace(IEnumerable<HfFrame> frames)
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        uint timestamp = 0;
        foreach (var f in frames)
        {
            var length = f.Data.Length;
            var duration = (ushort)Math.Min((length * 9 + 1) * 128, 0xFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(header, timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], duration);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)(length & 0x7FFF | (f.IsReader ? 0 : 0x8000)));
            ms.Write(header);
            ms.Write(f.Data);
            var parity = new byte[length == 0 ? 1 : (length - 1) / 8 + 1];
            if (f.IsShort) parity[0] = (byte)f.Bits;
            else
                for (var i = 0; i < length; i++)
                    if (OddParityBit(f.Data[i]) != 0) parity[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            ms.Write(parity);
            timestamp += duration;
        }
        return ms.ToArray();
    }

    private static int OddParityBit(byte b) => (System.Numerics.BitOperations.PopCount(b) & 1) ^ 1;

    // ---- Annotation ------------------------------------------------------

    /// <summary>
    /// Names each frame, card answers by the command they answer. Once a MIFARE Classic authentication
    /// completes, the rest of the session is encrypted and marked so, until the reader starts over with
    /// REQA / WUPA or authenticates in the clear again.
    /// </summary>
    public static List<HfAnnotatedFrame> Annotate(IReadOnlyList<HfFrame> frames)
    {
        var result = new List<HfAnnotatedFrame>(frames.Count);
        var encrypted = false;
        // 0: none, 1: AUTH sent (card nonce next), 2: card nonce sent (reader's nr || ar next), 3: card's at next.
        var auth = 0;
        HfFrame? command = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var d = f.Data;
            string label;
            var isEncrypted = false;
            if (auth == 1 && !f.IsReader && d.Length == 4)
            {
                label = $"Card nonce nt = {f.Hex}";
                auth = 2;
            }
            else if (auth == 2 && f.IsReader && d.Length == 8)
            {
                label = $"Reader answer, encrypted: nr = {Convert.ToHexString(d, 0, 4)}, ar = {Convert.ToHexString(d, 4, 4)}";
                auth = 3;
                encrypted = true;
            }
            else if (auth == 3 && !f.IsReader && d.Length == 4)
            {
                label = $"Card answer, encrypted: at = {f.Hex} (the reader's key was accepted)";
                auth = 0;
            }
            else
            {
                auth = 0;
                if (f.IsShort || IsAuth(f)) encrypted = false;
                if (encrypted)
                {
                    isEncrypted = true;
                    label = f.IsReader && d.Length == 4 ? "Encrypted command (or nested AUTH)" : "Encrypted";
                }
                else
                {
                    label = f.IsReader ? ReaderLabel(f) : CardLabel(f, command);
                    if (IsAuth(f)) auth = 1;
                }
            }
            if (f.IsReader) command = f;
            result.Add(new HfAnnotatedFrame(i, f, label, isEncrypted));
        }
        return result;
    }

    private static bool IsAuth(HfFrame f) =>
        f.IsReader && f.Data.Length == 4 && f.Data[0] is 0x60 or 0x61 && f.CrcOk == true;

    private static bool IsSelect(HfFrame? f) =>
        f is { IsReader: true, Data: [0x93 or 0x95 or 0x97, 0x70, ..] };

    private static string ReaderLabel(HfFrame f)
    {
        var d = f.Data;
        if (d.Length == 0) return "Empty";
        var b0 = d[0];
        if (f.IsShort)
            return b0 switch
            {
                0x26 => "REQA",
                0x52 => "WUPA",
                0x40 => "Magic WUPC1 (Gen1a)",
                _ => $"Short frame {b0:X2} ({f.Bits} bits)",
            };

        switch (b0)
        {
            case 0x93 or 0x95 or 0x97:
                var level = b0 == 0x93 ? 1 : b0 == 0x95 ? 2 : 3;
                if (d.Length >= 7 && d[1] == 0x70)
                    return d[2] == 0x88 ? $"SELECT CL{level}: UID part {Convert.ToHexString(d, 3, 3)} (cascade tag)"
                        : $"SELECT CL{level}: UID {Convert.ToHexString(d, 2, 4)}";
                return $"ANTICOLLISION CL{level}";
            case 0x50 when d.Length == 4 && d[1] == 0x00: return "HALT";
            case 0xE0 when d.Length >= 2: return $"RATS (FSDI {d[1] >> 4}, CID {d[1] & 0x0F})";
            case 0x60 or 0x61 when d.Length == 4:
                return $"AUTH key {(b0 == 0x60 ? 'A' : 'B')}, block {d[1]} (sector {MifareClassic.SectorOfBlock(d[1])})";
            case 0x60 when d.Length == 3: return "GET_VERSION (Ultralight / NTAG)";
            case 0x30 when d.Length == 4: return $"READ block / page {d[1]}";
            case 0xA0 when d.Length == 4: return $"WRITE block {d[1]}";
            case 0xA2 when d.Length == 8: return $"WRITE page {d[1]} (Ultralight)";
            case 0x1B when d.Length == 7: return "PWD_AUTH (Ultralight / NTAG)";
            case 0x3C when d.Length == 4: return "READ_SIG (Ultralight / NTAG)";
            case 0x43 when d.Length == 1: return "Magic WUPC2 (Gen1a)";
            case 0xC2 when d.Length == 3: return "S(DESELECT)";
            case 0xF2 when d.Length >= 3: return "S(WTX)";
            case >= 0xD0 and <= 0xDF when d.Length == 5: return "PPS";
        }

        // ISO14443-4 I-block: PCB, optional CID, then an APDU.
        if ((b0 & 0xE2) == 0x02 && d.Length >= 3)
        {
            var at = (b0 & 0x08) != 0 ? 2 : 1;
            var end = d.Length - (f.CrcOk == true ? 2 : 0);
            return end > at ? Apdu(d.AsSpan(at, end - at)) : "I-block";
        }
        if ((b0 & 0xE6) == 0xA2) return (b0 & 0x10) != 0 ? "R(NAK)" : "R(ACK)";
        return f.CrcOk == true ? "Command" : "Unknown command (no valid CRC)";
    }

    /// <summary>
    /// Names a card answer from the command before it. The Chameleon's own answers come without CRC (its radio
    /// adds it); answers from a Proxmark3 trace carry one.
    /// </summary>
    private static string CardLabel(HfFrame f, HfFrame? command)
    {
        var d = f.Data;
        if (d.Length == 0) return "Empty";
        var body = f.CrcOk == true ? d.AsSpan(0, d.Length - 2) : d;
        if (f.Bits == 4) return d[0] == 0x0A ? "ACK" : $"NAK {d[0]:X1}";
        if (command is { IsShort: true } && d.Length == 2) return $"ATQA {d[1]:X2}{d[0]:X2}";
        if (command is { Data: [0x93 or 0x95 or 0x97, ..] } && !IsSelect(command) && d.Length == 5)
        {
            var bcc = (d[0] ^ d[1] ^ d[2] ^ d[3]) == d[4] ? "BCC ok" : "BCC wrong";
            return d[0] == 0x88 ? $"UID part {Convert.ToHexString(d, 1, 3)} (cascade tag), {bcc}"
                : $"UID {Convert.ToHexString(d, 0, 4)}, {bcc}";
        }
        if (IsSelect(command) && body.Length == 1)
            return SakType(body[0]) is { } type ? $"SAK {body[0]:X2}: {type}" : $"SAK {body[0]:X2}";
        if (command is { Data: [0xE0, ..] }) return "ATS";
        if (command is { Data: [0x30, _, _, _] } && body.Length == 16) return "Data (16 bytes)";
        if (command is { Data: [0x60, _, _] } && body.Length == 8) return "Version";
        if (command is { Data: [0x3C, ..] } && body.Length == 32) return "Signature";
        if (command is { Data: [var pcb, ..] } && (pcb & 0xE2) == 0x02 && body.Length >= 3)
        {
            var sw = Sw(body[^2], body[^1]);
            return $"Response {body[^2]:X2}{body[^1]:X2}" + (sw is null ? "" : $": {sw}");
        }
        return "Card answer";
    }

    private static string Apdu(ReadOnlySpan<byte> a)
    {
        if (a.Length < 4) return "I-block";
        byte cla = a[0], ins = a[1], p1 = a[2], p2 = a[3];
        if (cla == 0x00 && ins == 0xA4)
        {
            if (a.Length > 5 && a.Length >= 5 + a[4] && p1 == 0x04)
            {
                var aid = Convert.ToHexString(a.Slice(5, a[4]));
                return AidName(aid) is { } name ? $"SELECT AID {aid} ({name})" : $"SELECT AID {aid}";
            }
            return "SELECT FILE";
        }
        return (cla, ins) switch
        {
            (0x00, 0xB0) => $"READ BINARY offset {(p1 << 8) | p2}",
            (0x00, 0xB2) => $"READ RECORD {p1}, SFI {p2 >> 3}",
            (0x00, 0xD6) => "UPDATE BINARY",
            (0x00, 0x20) => "VERIFY PIN",
            (0x00, 0x84) => "GET CHALLENGE",
            (0x00, 0x88) => "INTERNAL AUTHENTICATE",
            (0x00, 0x82) => "EXTERNAL AUTHENTICATE",
            (0x80, 0xA8) => "GET PROCESSING OPTIONS (EMV)",
            (0x80, 0xCA) => $"GET DATA {p1:X2}{p2:X2}" + (((p1 << 8) | p2) switch
            {
                0x9F36 => " (ATC)",
                0x9F13 => " (last online ATC)",
                0x9F17 => " (PIN try counter)",
                0x9F4F => " (log format)",
                _ => "",
            }),
            (0x80, 0xAE) => "GENERATE AC (EMV), requesting " + (p1 & 0xC0) switch
            {
                0x00 => "AAC",
                0x40 => "TC",
                0x80 => "ARQC",
                _ => "RFU",
            },
            (0x90, _) => $"DESFire command {ins:X2}",
            _ => $"APDU {cla:X2} {ins:X2} {p1:X2} {p2:X2}",
        };
    }

    private static string? AidName(string aid) => aid switch
    {
        "325041592E5359532E4444463031" => "PPSE",
        "315041592E5359532E4444463031" => "PSE",
        "A0000000031010" => "Visa",
        "A0000000032010" => "Visa Electron",
        "A0000000038010" => "Visa Plus",
        "A0000000041010" => "Mastercard",
        "A0000000043060" => "Maestro",
        "A00000002501" => "American Express",
        "A0000006581010" => "Mir",
        "A0000003330101" => "UnionPay",
        "D2760000850101" => "NDEF",
        "D2760000850100" => "NDEF (v1)",
        _ => null,
    };

    private static string? SakType(byte sak) => sak switch
    {
        0x00 => "Ultralight / NTAG",
        0x04 => "UID not complete",
        0x08 => "MIFARE Classic 1K",
        0x09 => "MIFARE Mini",
        0x10 => "MIFARE Plus 2K",
        0x11 => "MIFARE Plus 4K",
        0x18 => "MIFARE Classic 4K",
        0x19 => "MIFARE Classic 2K",
        0x20 => "ISO14443-4 (DESFire, Plus SL3, EMV…)",
        0x28 => "SmartMX with MIFARE Classic 1K",
        0x38 => "SmartMX with MIFARE Classic 4K",
        _ => null,
    };

    private static string? Sw(byte sw1, byte sw2) => ((sw1 << 8) | sw2) switch
    {
        0x9000 => "OK",
        0x6700 => "wrong length",
        0x6982 => "security status not satisfied",
        0x6985 => "conditions of use not satisfied",
        0x6A82 => "file not found",
        0x6A83 => "record not found",
        0x6A86 => "incorrect P1-P2",
        0x6D00 => "instruction not supported",
        0x6E00 => "class not supported",
        _ when sw1 == 0x61 => $"{sw2} bytes available",
        _ when sw1 == 0x6C => $"wrong Le, use {sw2}",
        _ when sw1 == 0x91 => "DESFire status",
        _ => null,
    };

    // ---- Summary -----------------------------------------------------------

    public static HfSniffSummary Summarize(IReadOnlyList<HfFrame> frames)
    {
        var uid = new List<byte>();
        byte[]? atqa = null;
        byte? sak = null;
        bool rats = false, arqc = false, tc = false, halted = false;
        var aids = new List<string>();
        var auths = new List<(int, MfKeyType)>();
        foreach (var (f, i) in frames.Select((f, i) => (f, i)))
        {
            var d = f.Data;
            if (d.Length == 0) continue;
            if (!f.IsReader)
            {
                if (d.Length == 2 && i > 0 && frames[i - 1].IsShort) atqa = [d[1], d[0]];
                if (i > 0 && IsSelect(frames[i - 1]) && (d.Length == 1 || d.Length == 3 && f.CrcOk == true))
                    sak = d[0];
                continue;
            }
            switch (d[0])
            {
                case 0x93 or 0x95 or 0x97 when d.Length >= 7 && d[1] == 0x70:
                    if (d[0] == 0x93) uid.Clear();
                    uid.AddRange(d[2] == 0x88 ? d[3..6] : d[2..6]);
                    break;
                case 0xE0 when d.Length >= 2:
                    rats = true;
                    break;
                case 0x50 when d.Length == 4 && d[1] == 0:
                    halted = true;
                    break;
            }
            if (IsAuth(f) && !auths.Contains((d[1], d[0] == 0x60 ? MfKeyType.A : MfKeyType.B)))
                auths.Add((d[1], d[0] == 0x60 ? MfKeyType.A : MfKeyType.B));
            var label = ReaderLabel(f);
            if (label.StartsWith("SELECT AID ") && !aids.Contains(label[11..])) aids.Add(label[11..]);
            if (label.EndsWith("requesting ARQC")) arqc = true;
            if (label.EndsWith("requesting TC")) tc = true;
        }
        return new HfSniffSummary(frames.Count(f => f.IsReader), frames.Count(f => !f.IsReader),
            uid.Count > 0 ? uid.ToArray() : null, atqa, sak, rats, aids, auths, arqc, tc, halted);
    }

    // ---- Authentications ---------------------------------------------------

    /// <summary>
    /// The plain authentications: AUTH, the card nonce, the reader's nr || ar and the card's at if it followed.
    /// The UID is the last four bytes of the one selected before, the part Crypto1 is keyed with.
    /// </summary>
    public static List<HfAuthExchange> AuthExchanges(IReadOnlyList<HfFrame> frames)
    {
        var result = new List<HfAuthExchange>();
        uint uid = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f is { IsReader: true, Data: [0x93 or 0x95 or 0x97, 0x70, var u0, var u1, var u2, var u3, ..] } && u0 != 0x88)
                uid = (uint)(u0 << 24 | u1 << 16 | u2 << 8 | u3);
            if (!IsAuth(f) || i + 2 >= frames.Count) continue;
            if (frames[i + 1] is not { IsReader: false, Data.Length: 4 } nt
                || frames[i + 2] is not { IsReader: true, Data.Length: 8 } nrAr) continue;
            uint? at = i + 3 < frames.Count && frames[i + 3] is { IsReader: false, Data.Length: 4 } a
                ? BinaryPrimitives.ReadUInt32BigEndian(a.Data) : null;
            result.Add(new HfAuthExchange(uid, f.Data[1], f.Data[0] == 0x60 ? MfKeyType.A : MfKeyType.B,
                BinaryPrimitives.ReadUInt32BigEndian(nt.Data),
                BinaryPrimitives.ReadUInt32BigEndian(nrAr.Data),
                BinaryPrimitives.ReadUInt32BigEndian(nrAr.Data.AsSpan(4)), at));
        }
        return result;
    }

    public static List<HfAuthGroup> GroupAuths(IEnumerable<HfAuthExchange> exchanges) =>
        exchanges.GroupBy(e => (e.Uid, e.Block, e.KeyType))
            .Select(g => new HfAuthGroup(g.Key.Uid, g.Key.Block, g.Key.KeyType, g.ToList()))
            .ToList();

    // ---- CRC_A ---------------------------------------------------------------

    /// <summary>ISO14443-A CRC (CRC_A): initial value 0x6363, reflected polynomial 0x8408.</summary>
    public static ushort CrcA(ReadOnlySpan<byte> data)
    {
        ushort crc = 0x6363;
        foreach (var b in data)
        {
            var ch = (byte)(b ^ (byte)crc);
            ch ^= (byte)(ch << 4);
            crc = (ushort)((crc >> 8) ^ (ch << 8) ^ (ch << 3) ^ (ch >> 4));
        }
        return crc;
    }
}
