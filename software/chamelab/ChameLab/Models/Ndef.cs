using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>Type Name Format of an NDEF record: how its type field is to be read.</summary>
public enum NdefTnf : byte
{
    Empty = 0,
    WellKnown = 1,
    Media = 2,
    AbsoluteUri = 3,
    External = 4,
    Unknown = 5,
    Unchanged = 6,
}

/// <summary>One NDEF record as stored: TNF, type, ID and payload, kept byte for byte.</summary>
public sealed record NdefRecord(NdefTnf Tnf, byte[] Type, byte[] Id, byte[] Payload)
{
    public string TypeText => Encoding.UTF8.GetString(Type);
}

/// <summary>Security of a Wi-Fi network carried in a Wi-Fi Simple Config record.</summary>
public enum NdefWifiAuth
{
    Open,
    WpaPersonal,
    Wpa2Personal,
    WpaWpa2Personal,
}

public sealed record NdefWifi(string Ssid, NdefWifiAuth Auth, string Key);

/// <summary>
/// NDEF messages (NFC Forum NDEF 1.0): parsing and building records, plus the common record
/// types phones understand — URI, text, Wi-Fi (Wi-Fi Simple Config), Android application
/// record, MIME media and the smart poster (read only).
/// </summary>
public static class Ndef
{
    public const string WifiMime = "application/vnd.wfa.wsc";
    public const string AndroidAppType = "android.com:pkg";

    // NFC Forum URI RTD abbreviations, by identifier code.
    private static readonly string[] UriPrefixes =
    [
        "", "http://www.", "https://www.", "http://", "https://", "tel:", "mailto:",
        "ftp://anonymous:anonymous@", "ftp://ftp.", "ftps://", "sftp://", "smb://", "nfs://",
        "ftp://", "dav://", "news:", "telnet://", "imap:", "rtsp://", "urn:", "pop:", "sip:",
        "sips:", "tftp:", "btspp://", "btl2cap://", "btgoep://", "tcpobex://", "irdaobex://",
        "file://", "urn:epc:id:", "urn:epc:tag:", "urn:epc:pat:", "urn:epc:raw:", "urn:epc:",
        "urn:nfc:",
    ];

    // ---- Message -------------------------------------------------------

    /// <summary>Parses an NDEF message; chunked records are joined. Throws <see cref="FormatException"/> when malformed.</summary>
    public static List<NdefRecord> Parse(ReadOnlySpan<byte> message)
    {
        var records = new List<NdefRecord>();
        var pos = 0;
        NdefRecord? chunked = null;
        var chunks = new List<byte>();
        while (pos < message.Length)
        {
            var header = message[pos++];
            var sr = (header & 0x10) != 0;
            var il = (header & 0x08) != 0;
            var cf = (header & 0x20) != 0;
            var tnf = (NdefTnf)(header & 0x07);
            int typeLength = Next(message, ref pos);
            var payloadLength = sr ? Next(message, ref pos)
                : (Next(message, ref pos) << 24) | (Next(message, ref pos) << 16) | (Next(message, ref pos) << 8) | Next(message, ref pos);
            var idLength = il ? Next(message, ref pos) : 0;
            if (payloadLength < 0 || pos + typeLength + idLength + (long)payloadLength > message.Length)
                throw new FormatException("A record runs past the end of the message.");
            var type = message.Slice(pos, typeLength).ToArray();
            pos += typeLength;
            var id = message.Slice(pos, idLength).ToArray();
            pos += idLength;
            var payload = message.Slice(pos, payloadLength);
            pos += payloadLength;

            if (chunked is not null)
            {
                if (tnf != NdefTnf.Unchanged) throw new FormatException("A chunked record is cut short.");
                chunks.AddRange(payload.ToArray());
                if (cf) continue;
                records.Add(chunked with { Payload = chunks.ToArray() });
                chunked = null;
                chunks.Clear();
            }
            else if (cf)
            {
                chunked = new NdefRecord(tnf, type, id, []);
                chunks.AddRange(payload.ToArray());
                continue;
            }
            else
            {
                if (tnf == NdefTnf.Unchanged) throw new FormatException("An unchanged record outside a chunk.");
                records.Add(new NdefRecord(tnf, type, id, payload.ToArray()));
            }
            if ((header & 0x40) != 0) break; // ME
        }
        if (chunked is not null) throw new FormatException("A chunked record is cut short.");
        return records;
    }

    private static byte Next(ReadOnlySpan<byte> data, ref int pos) =>
        pos < data.Length ? data[pos++] : throw new FormatException("A record runs past the end of the message.");

    /// <summary>Builds an NDEF message; short records wherever the payload fits in 255 bytes.</summary>
    public static byte[] Encode(IReadOnlyList<NdefRecord> records)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var sr = r.Payload.Length < 256;
            var header = (byte)r.Tnf;
            if (i == 0) header |= 0x80;
            if (i == records.Count - 1) header |= 0x40;
            if (sr) header |= 0x10;
            if (r.Id.Length > 0) header |= 0x08;
            bytes.Add(header);
            bytes.Add(checked((byte)r.Type.Length));
            if (sr) bytes.Add((byte)r.Payload.Length);
            else bytes.AddRange([(byte)(r.Payload.Length >> 24), (byte)(r.Payload.Length >> 16), (byte)(r.Payload.Length >> 8), (byte)r.Payload.Length]);
            if (r.Id.Length > 0) bytes.Add(checked((byte)r.Id.Length));
            bytes.AddRange(r.Type);
            bytes.AddRange(r.Id);
            bytes.AddRange(r.Payload);
        }
        return bytes.ToArray();
    }

    // ---- Builders ------------------------------------------------------

    /// <summary>URI record, the longest matching prefix abbreviated.</summary>
    public static NdefRecord Uri(string uri)
    {
        var code = 0;
        for (var i = 1; i < UriPrefixes.Length; i++)
            if (uri.StartsWith(UriPrefixes[i], StringComparison.Ordinal) && UriPrefixes[i].Length > UriPrefixes[code].Length)
                code = i;
        byte[] payload = [(byte)code, .. Encoding.UTF8.GetBytes(uri[UriPrefixes[code].Length..])];
        return new NdefRecord(NdefTnf.WellKnown, "U"u8.ToArray(), [], payload);
    }

    /// <summary>Text record in UTF-8.</summary>
    public static NdefRecord Text(string text, string language)
    {
        var lang = Encoding.ASCII.GetBytes(string.IsNullOrWhiteSpace(language) ? "en" : language.Trim());
        if (lang.Length > 63) throw new FormatException("The language code is too long.");
        byte[] payload = [(byte)lang.Length, .. lang, .. Encoding.UTF8.GetBytes(text)];
        return new NdefRecord(NdefTnf.WellKnown, "T"u8.ToArray(), [], payload);
    }

    public static NdefRecord Mime(string type, byte[] payload) =>
        new(NdefTnf.Media, Encoding.ASCII.GetBytes(type.Trim()), [], payload);

    /// <summary>Android application record: opens (or offers to install) the app.</summary>
    public static NdefRecord AndroidApp(string package) =>
        new(NdefTnf.External, Encoding.ASCII.GetBytes(AndroidAppType), [], Encoding.UTF8.GetBytes(package.Trim()));

    /// <summary>Wi-Fi Simple Config credential, as Android and iOS read it.</summary>
    public static NdefRecord Wifi(NdefWifi wifi)
    {
        var (auth, encryption) = wifi.Auth switch
        {
            NdefWifiAuth.Open => (0x0001, 0x0001),
            NdefWifiAuth.WpaPersonal => (0x0002, 0x0004),
            NdefWifiAuth.Wpa2Personal => (0x0020, 0x0008),
            _ => (0x0022, 0x000C),
        };
        var credential = new List<byte>();
        Attribute(credential, 0x1026, [1]); // network index
        Attribute(credential, 0x1045, Encoding.UTF8.GetBytes(wifi.Ssid));
        Attribute(credential, 0x1003, [(byte)(auth >> 8), (byte)auth]);
        Attribute(credential, 0x100F, [(byte)(encryption >> 8), (byte)encryption]);
        Attribute(credential, 0x1027, wifi.Auth == NdefWifiAuth.Open ? [] : Encoding.UTF8.GetBytes(wifi.Key));
        Attribute(credential, 0x1020, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // any access point
        var payload = new List<byte>();
        Attribute(payload, 0x100E, credential.ToArray());
        return Mime(WifiMime, payload.ToArray());
    }

    private static void Attribute(List<byte> to, int id, byte[] value)
    {
        to.AddRange([(byte)(id >> 8), (byte)id, (byte)(value.Length >> 8), (byte)value.Length]);
        to.AddRange(value);
    }

    // ---- Decoders ------------------------------------------------------

    private static bool IsWellKnown(NdefRecord r, string type) =>
        r.Tnf == NdefTnf.WellKnown && r.TypeText == type;

    public static bool TryUri(NdefRecord r, out string uri)
    {
        uri = string.Empty;
        if (!IsWellKnown(r, "U") || r.Payload.Length == 0) return false;
        var prefix = r.Payload[0] < UriPrefixes.Length ? UriPrefixes[r.Payload[0]] : string.Empty;
        uri = prefix + Encoding.UTF8.GetString(r.Payload, 1, r.Payload.Length - 1);
        return true;
    }

    public static bool TryText(NdefRecord r, out string text, out string language)
    {
        text = language = string.Empty;
        if (!IsWellKnown(r, "T") || r.Payload.Length == 0) return false;
        var status = r.Payload[0];
        var langLength = status & 0x3F;
        if (1 + langLength > r.Payload.Length) return false;
        language = Encoding.ASCII.GetString(r.Payload, 1, langLength);
        var body = r.Payload.AsSpan(1 + langLength);
        if ((status & 0x80) == 0) text = Encoding.UTF8.GetString(body);
        else if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) text = Encoding.Unicode.GetString(body[2..]);
        else if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(body[2..]);
        else text = Encoding.BigEndianUnicode.GetString(body);
        return true;
    }

    public static bool TryAndroidApp(NdefRecord r, out string package)
    {
        package = string.Empty;
        if (r.Tnf != NdefTnf.External || r.TypeText != AndroidAppType) return false;
        package = Encoding.UTF8.GetString(r.Payload);
        return true;
    }

    public static bool IsMime(NdefRecord r, string type) =>
        r.Tnf == NdefTnf.Media && string.Equals(r.TypeText, type, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first credential of a Wi-Fi Simple Config record, when its security is one this app can edit.</summary>
    public static bool TryWifi(NdefRecord r, out NdefWifi wifi)
    {
        wifi = new NdefWifi(string.Empty, NdefWifiAuth.Open, string.Empty);
        if (!IsMime(r, WifiMime)) return false;
        var credential = FindAttribute(r.Payload, 0x100E);
        if (credential is null) return false;
        var ssid = FindAttribute(credential, 0x1045);
        var auth = FindAttribute(credential, 0x1003);
        if (ssid is null || auth is not { Length: 2 }) return false;
        NdefWifiAuth? kind = ((auth[0] << 8) | auth[1]) switch
        {
            0x0001 => NdefWifiAuth.Open,
            0x0002 => NdefWifiAuth.WpaPersonal,
            0x0020 => NdefWifiAuth.Wpa2Personal,
            0x0022 => NdefWifiAuth.WpaWpa2Personal,
            _ => null,
        };
        if (kind is null) return false;
        var key = FindAttribute(credential, 0x1027) ?? [];
        wifi = new NdefWifi(Encoding.UTF8.GetString(ssid), kind.Value, Encoding.UTF8.GetString(key));
        return true;
    }

    private static byte[]? FindAttribute(byte[] data, int id)
    {
        for (var pos = 0; pos + 4 <= data.Length;)
        {
            var type = (data[pos] << 8) | data[pos + 1];
            var length = (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            if (pos + length > data.Length) return null;
            if (type == id) return data[pos..(pos + length)];
            pos += length;
        }
        return null;
    }

    /// <summary>Payload as text when it is printable UTF-8; null for binary data.</summary>
    public static string? PayloadText(byte[] payload)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload);
            return text.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t') ? text : null;
        }
        catch (ArgumentException) { return null; }
    }

    public static string WifiAuthLabel(this NdefWifiAuth auth) => auth switch
    {
        NdefWifiAuth.Open => "Open (no password)",
        NdefWifiAuth.WpaPersonal => "WPA-Personal",
        NdefWifiAuth.Wpa2Personal => "WPA2-Personal",
        _ => "WPA/WPA2-Personal",
    };

    /// <summary>What a record is and its content in one line each, for the record list.</summary>
    public static (string Kind, string Value) Describe(NdefRecord r)
    {
        if (TryUri(r, out var uri))
            return (uri.StartsWith("tel:") ? "Phone" : uri.StartsWith("mailto:") ? "Email" : "Link", uri);
        if (TryText(r, out var text, out var lang)) return ($"Text ({lang})", text);
        if (TryWifi(r, out var wifi))
            return ("Wi-Fi", wifi.Auth == NdefWifiAuth.Open ? wifi.Ssid : $"{wifi.Ssid} · {wifi.Auth.WifiAuthLabel()} · {wifi.Key}");
        if (TryAndroidApp(r, out var package)) return ("Android app", package);
        if (IsWellKnown(r, "Sp"))
        {
            try
            {
                var parts = Parse(r.Payload).Select(Describe).Select(d => d.Value).Where(v => v.Length > 0);
                return ("Smart poster", string.Join(" · ", parts));
            }
            catch (FormatException) { return ("Smart poster", Hex.Spaced(r.Payload)); }
        }
        var value = PayloadText(r.Payload) ?? Hex.Spaced(r.Payload);
        return r.Tnf switch
        {
            NdefTnf.Empty => ("Empty record", string.Empty),
            NdefTnf.Media when IsMime(r, "text/vcard") || IsMime(r, "text/x-vcard") => ("Contact (vCard)", value),
            NdefTnf.Media => (r.TypeText, value),
            NdefTnf.AbsoluteUri => ("Absolute URI", r.TypeText),
            NdefTnf.External => ($"External: {r.TypeText}", value),
            NdefTnf.WellKnown => ($"Well-known: {r.TypeText}", value),
            _ => ("Unknown type", value),
        };
    }
}

/// <summary>Where an NDEF message lives in Type 2 tag (Ultralight / NTAG) memory, and what it holds.</summary>
public sealed record Type2NdefArea(
    bool IsFormatted,
    int Capacity,
    int PrefixLength,
    IReadOnlyList<NdefRecord>? Records,
    int MessageLength,
    string? Error,
    int FirstUnreadPage);

/// <summary>
/// NDEF on Type 2 tags (NFC Forum Type 2 Tag 1.0): the capability container on page 3 and the
/// TLV blocks of the data area from page 4 — lock / memory control TLVs, the NDEF message TLV
/// and the terminator.
/// </summary>
public static class Type2Ndef
{
    public const int FirstPage = 4;
    private const byte CcMagic = 0xE1;

    /// <summary>Data area size the capability container announces for a factory-fresh card of this type.</summary>
    public static int DefaultCapacity(TagSpecificType tag) => tag switch
    {
        TagSpecificType.Ntag215 => 496,
        TagSpecificType.Ntag216 => 872,
        _ => (tag.UltralightUserEnd() - FirstPage) * 4,
    };

    /// <summary>Reads the NDEF message from the card's pages; never throws.</summary>
    public static Type2NdefArea Read(IReadOnlyList<byte[]> pages, TagSpecificType tag)
    {
        var cc = Page(pages, 3);
        var formatted = cc is { } c && c[0] == CcMagic;
        var capacity = Capacity(pages, tag);
        var (area, firstUnread) = Area(pages, capacity);
        if (cc is null)
            return new(false, capacity, 0, null, 0, "Page 3 (capability container) was not read.", firstUnread);
        if (!formatted)
            return new(false, capacity, 0, null, 0, "The card is not formatted for NDEF (no capability container on page 3).", firstUnread);
        var unread = firstUnread >= 0 ? $" Pages from {firstUnread} were not read." : string.Empty;

        var pos = 0;
        var controlEnd = 0; // end of the last control TLV, which a new message goes after
        while (pos < area.Length)
        {
            var t = area[pos];
            if (t == 0x00) { pos++; continue; }
            if (t == 0xFE) break;
            var start = pos++;
            if (!TryLength(area, ref pos, out var length))
                return new(true, capacity, start, null, 0, "The NDEF data area is malformed." + unread, firstUnread);
            if (t == 0x03)
            {
                if (pos + length > area.Length)
                    return new(true, capacity, start, null, 0, "The NDEF message runs past the end of the data area." + unread, firstUnread);
                try
                {
                    var records = Ndef.Parse(area.AsSpan(pos, length));
                    var cut = firstUnread >= 0 && firstUnread <= FirstPage + (pos + length - 1) / 4;
                    return new(true, capacity, start, records, length,
                        cut ? "Part of the message is on pages that were not read." : null, firstUnread);
                }
                catch (FormatException ex)
                {
                    return new(true, capacity, start, null, length, $"The NDEF message is malformed: {ex.Message}{unread}", firstUnread);
                }
            }
            if (t is not (0x01 or 0x02 or 0xFD))
                return new(true, capacity, start, null, 0, $"Unknown TLV {t:X2} in the NDEF data area." + unread, firstUnread);
            pos += length;
            controlEnd = Math.Min(pos, area.Length);
        }
        // No message TLV: control TLVs before the terminator stay in front of a new one.
        return new(true, capacity, controlEnd, [], 0, null, firstUnread);
    }

    /// <summary>
    /// The data area (from page 4) with <paramref name="message"/> as its only NDEF TLV: control TLVs
    /// in front of the old message are kept, the terminator follows and the rest is zeroed. Also
    /// returns the capability container a blank (zero) page 3 gets. Throws when it doesn't fit.
    /// </summary>
    public static (byte[] Area, byte[]? CapabilityContainer) Build(IReadOnlyList<byte[]> pages, TagSpecificType tag, byte[] message)
    {
        var current = Read(pages, tag);
        var cc = Page(pages, 3);
        byte[]? newCc = null;
        var prefix = current.PrefixLength;
        if (!current.IsFormatted)
        {
            if (cc is not null && cc.Any(b => b != 0))
                throw new FormatException("Page 3 holds data other than a capability container; it is one-time programmable, so the card can't take NDEF.");
            newCc = [CcMagic, 0x10, (byte)(DefaultCapacity(tag) / 8), 0x00];
            prefix = 0;
        }
        var capacity = current.IsFormatted ? current.Capacity : DefaultCapacity(tag);
        var used = UsedBytes(prefix, message.Length);
        if (used > capacity)
            throw new FormatException($"The message needs {used} bytes, but the card holds {capacity}.");

        var (old, _) = Area(pages, capacity);
        var area = new byte[capacity];
        Array.Copy(old, area, prefix);
        var pos = prefix;
        area[pos++] = 0x03;
        if (message.Length < 0xFF) area[pos++] = (byte)message.Length;
        else { area[pos++] = 0xFF; area[pos++] = (byte)(message.Length >> 8); area[pos++] = (byte)message.Length; }
        message.CopyTo(area, pos);
        pos += message.Length;
        if (pos < capacity) area[pos] = 0xFE;
        return (area, newCc);
    }

    /// <summary>Bytes of the data area a message takes: control TLVs in front, TLV header, message, terminator.</summary>
    public static int UsedBytes(int prefix, int messageLength) =>
        prefix + (messageLength < 0xFF ? 2 : 4) + messageLength + 1;

    /// <summary>Bytes of the data area: what the capability container says, within the card's user memory.</summary>
    public static int Capacity(IReadOnlyList<byte[]> pages, TagSpecificType tag)
    {
        var max = (tag.UltralightUserEnd() - FirstPage) * 4;
        return Page(pages, 3) is { } cc && cc[0] == CcMagic && cc[2] > 0 ? Math.Min(cc[2] * 8, max) : max;
    }

    private static (byte[] Area, int FirstUnread) Area(IReadOnlyList<byte[]> pages, int capacity)
    {
        var area = new byte[capacity];
        var firstUnread = -1;
        for (var i = 0; i < (capacity + 3) / 4; i++)
        {
            var page = Page(pages, FirstPage + i);
            if (page is null) { if (firstUnread < 0) firstUnread = FirstPage + i; continue; }
            Array.Copy(page, 0, area, i * 4, Math.Min(4, capacity - i * 4));
        }
        return (area, firstUnread);
    }

    private static bool TryLength(byte[] area, ref int pos, out int length)
    {
        length = 0;
        if (pos >= area.Length) return false;
        length = area[pos++];
        if (length != 0xFF) return true;
        if (pos + 2 > area.Length) return false;
        length = (area[pos] << 8) | area[pos + 1];
        pos += 2;
        return true;
    }

    private static byte[]? Page(IReadOnlyList<byte[]> pages, int page) =>
        page < pages.Count && pages[page].Length == 4 ? pages[page] : null;
}
