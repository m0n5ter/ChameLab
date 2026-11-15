using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ChameLab.Cli;

internal sealed class PacketReader(byte[] data)
{
    private int _offset;
    public byte Byte() => Take(1)[0];
    public int UInt16Little() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public byte[] Take(int length)
    {
        if (length < 0 || length > data.Length - _offset) throw new InvalidDataException("Truncated device response.");
        var result = data[_offset..(_offset + length)]; _offset += length; return result;
    }
    public void End() { if (_offset != data.Length) throw new InvalidDataException("Unexpected trailing data."); }
}

internal sealed record ApduPair(byte[] Command, byte[] Response);
internal sealed record EmvCapture(byte[] Uid, byte[] Atqa, byte Sak, byte[] Ats, List<ApduPair> Pairs)
{
    public static EmvCapture Decode(byte[] bytes)
    {
        var r = new PacketReader(bytes);
        var uid = r.Take(r.Byte()); var atqa = r.Take(2); var sak = r.Byte(); var ats = r.Take(r.Byte());
        var count = r.Byte(); var pairs = new List<ApduPair>();
        for (var i = 0; i < count; i++) { var cmd = r.Take(r.Byte()); var response = r.Take(r.UInt16Little()); pairs.Add(new(cmd, response)); }
        r.End();
        if (uid.Length is not (4 or 7 or 10) || pairs.Count == 0) throw new InvalidDataException("Invalid EMV capture.");
        return new(uid, atqa, sak, ats, pairs);
    }

    public JsonObject ToJson()
    {
        var root = new JsonObject
        {
            ["File"] = new JsonObject { ["Created"] = "ChameLab emv scan" },
            ["Card"] = new JsonObject { ["Contactless"] = new JsonObject
            {
                ["Communication"] = "iso14443-4a", ["UID"] = Convert.ToHexString(Uid),
                // PM3 stores ATQA in display order; the protocol uses little endian wire order.
                ["ATQA"] = Convert.ToHexString(Atqa.Reverse().ToArray()), ["SAK"] = Sak.ToString("X2"), ["ATS"] = Convert.ToHexString(Ats)
            } },
            ["APDUs"] = new JsonArray(Pairs.Select(p => (JsonNode)new JsonObject { ["Command"] = Convert.ToHexString(p.Command), ["Response"] = Convert.ToHexString(p.Response) }).ToArray())
        };
        var app = new JsonObject(); var records = new JsonArray(); var decoded = new JsonObject();
        foreach (var pair in Pairs)
        {
            var c = pair.Command; var r = pair.Response;
            if (c.Length < 4 || r.Length < 2 || r[^2] != 0x90 || r[^1] != 0) continue;
            BerTlv[] tlvs;
            try { tlvs = BerTlv.Read(r[..^2]); } catch (InvalidDataException) { continue; }
            if (tlvs.Length == 0) continue;
            var outer = tlvs[0].ToJson();
            if (c[1] == 0xA4 && c.Length >= 5 + c[4])
            {
                var aid = c[5..(5 + c[4])];
                if (Encoding.ASCII.GetString(aid) == "2PAY.SYS.DDF01") root["PPSE"] = new JsonObject { ["FCITemplate"] = outer };
                else { app["AID"] = Convert.ToHexString(aid); app["FCITemplate"] = outer; }
            }
            else if (c[1] == 0xA8) app["GPO"] = outer;
            else if (c[1] == 0xB2) records.Add((JsonNode)new JsonObject { ["SFI"] = (c[3] >> 3).ToString("X2"), ["RecordNum"] = c[2].ToString("X2"), ["Data"] = outer });
            foreach (var t in tlvs.SelectMany(t => t.Flatten()))
            {
                var hex = Convert.ToHexString(t.Value);
                switch (t.Tag)
                {
                    case "5A": decoded["PAN"] ??= JsonValue.Create(hex.TrimEnd('F')); break;
                    case "57":
                        var split = hex.IndexOf('D');
                        if (split > 0) { decoded["PAN"] = hex[..split]; if (hex.Length >= split + 5) decoded["Expiry"] ??= JsonValue.Create($"20{hex.Substring(split + 1, 2)}/{hex.Substring(split + 3, 2)}"); }
                        break;
                    case "5F24" when hex.Length == 6: decoded["Expiry"] = $"20{hex[..2]}/{hex.Substring(2, 2)}"; break;
                    case "5F20": decoded["CardholderName"] = Encoding.ASCII.GetString(t.Value).Trim(); break;
                    case "5F28": decoded["IssuerCountry"] = hex.TrimStart('0'); break;
                    case "50": decoded["ApplicationLabel"] = Encoding.ASCII.GetString(t.Value).Trim(); break;
                    case "9F12": decoded["ApplicationName"] = Encoding.ASCII.GetString(t.Value).Trim(); break;
                }
            }
        }
        app["Records"] = records; root["Application"] = app; root["Decoded"] = decoded;
        return root;
    }

    public static EmvCapture FromJson(JsonObject root)
    {
        var card = root["Card"]?["Contactless"] ?? throw new InvalidDataException("Missing Card.Contactless.");
        byte[] H(JsonNode node, string name) => CliSession.HexText(node[name]?.GetValue<string>() ?? throw new InvalidDataException("Missing " + name));
        var uid = H(card, "UID"); var atqa = H(card, "ATQA"); var sak = H(card, "SAK"); var ats = H(card, "ATS");
        if (uid.Length is not (4 or 7 or 10) || atqa.Length != 2 || sak.Length != 1) throw new InvalidDataException("Invalid card identity.");
        if (ats.Length > 0)
        {
            if (ats[0] > ats.Length) throw new InvalidDataException("Truncated ATS.");
            ats = ats[..ats[0]];
        }
        var pairs = new List<ApduPair>();
        if (root["APDUs"] is JsonArray raw)
            foreach (var entry in raw) pairs.Add(new(H(entry!, "Command"), H(entry!, "Response")));
        else
        {
            byte[] Response(JsonNode tlv, string tag)
            {
                var value = H(tlv, "value");
                return [.. BerTlv.Encode(tlv["tag"]?.GetValue<string>() ?? tag, value), 0x90, 0];
            }
            if (root["PPSE"]?["FCITemplate"] is { } ppse) pairs.Add(new(CliSession.HexText("00A404000E325041592E5359532E4444463031"), Response(ppse, "6F")));
            if (root["Application"] is { } app)
            {
                if (app["FCITemplate"] is { } fci)
                { var aid = H(app, "AID"); pairs.Add(new([0, 0xA4, 4, 0, checked((byte)aid.Length), .. aid], Response(fci, "6F"))); }
                if (app["GPO"] is { } gpo) pairs.Add(new([0x80, 0xA8, 0, 0], Response(gpo, "77")));
                if (app["Records"] is JsonArray records)
                    foreach (var record in records)
                    {
                        var sfi = int.Parse(record!["SFI"]!.GetValue<string>(), NumberStyles.HexNumber);
                        var number = int.Parse(record["RecordNum"]!.GetValue<string>(), NumberStyles.HexNumber);
                        if (sfi is < 1 or > 30 || number is < 1 or > 255) throw new InvalidDataException("Invalid EMV record address.");
                        pairs.Add(new([0, 0xB2, (byte)number, (byte)((sfi << 3) | 4), 0], Response(record["Data"]!, "70")));
                    }
            }
        }
        if (pairs.Count == 0 || pairs.Any(p => p.Command.Length is < 1 or > 255 || p.Response.Length < 2)) throw new InvalidDataException("Invalid or missing APDU pairs.");
        return new(uid, atqa.Reverse().ToArray(), sak[0], ats, pairs);
    }
}

internal sealed record BerTlv(string Tag, byte[] Value, bool Constructed)
{
    public JsonObject ToJson() => new() { ["tag"] = Tag, ["length"] = Value.Length.ToString("X2"), ["value"] = Convert.ToHexString(Value) };
    public IEnumerable<BerTlv> Flatten(int depth = 0)
    {
        yield return this;
        if (!Constructed || depth >= 16) yield break;
        BerTlv[] children;
        try { children = Read(Value); } catch (InvalidDataException) { yield break; }
        foreach (var child in children) foreach (var item in child.Flatten(depth + 1)) yield return item;
    }
    public static BerTlv[] Read(byte[] bytes)
    {
        var result = new List<BerTlv>(); var i = 0;
        byte Next() => i < bytes.Length ? bytes[i++] : throw new InvalidDataException("Truncated BER-TLV.");
        while (i < bytes.Length)
        {
            var first = Next(); if (first is 0 or 255) continue;
            var tag = new List<byte> { first };
            if ((first & 31) == 31)
            {
                byte b;
                do { if (tag.Count >= 4) throw new InvalidDataException("BER tag is too long."); tag.Add(b = Next()); } while ((b & 128) != 0);
            }
            var length = (int)Next();
            if ((length & 128) != 0)
            {
                var count = length & 127; if (count is < 1 or > 3) throw new InvalidDataException("Unsupported BER length.");
                length = 0; for (var n = 0; n < count; n++) length = (length << 8) | Next();
            }
            if (length > bytes.Length - i) throw new InvalidDataException("Truncated BER value.");
            result.Add(new(Convert.ToHexString(tag.ToArray()), bytes[i..(i + length)], (first & 32) != 0)); i += length;
        }
        return result.ToArray();
    }
    public static byte[] Encode(string tag, byte[] value)
    {
        var n = value.Length;
        byte[] length = n < 128 ? [(byte)n] : n <= 255 ? [0x81, (byte)n] : n <= 65535 ? [0x82, (byte)(n >> 8), (byte)n] : throw new ArgumentException("BER value is too long.");
        return [.. CliSession.HexText(tag), .. length, .. value];
    }
}
