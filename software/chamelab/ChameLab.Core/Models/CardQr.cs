using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Localization;

namespace ChameLab.Models;

/// <summary>One QR code of a shared card: part <see cref="Index"/> (1-based) of <see cref="Count"/>.</summary>
/// <param name="Id">Ties the parts of one card together (a hash prefix of the whole payload).</param>
/// <param name="Data">This part's slice of the base64url payload.</param>
public readonly record struct CardQrPart(int Index, int Count, string Id, string Data);

/// <summary>
/// Cards shared as QR codes. Each code is a link to the web app,
/// <c>https://m0n5ter.github.io/ChameLab/app/#card=1.&lt;i&gt;.&lt;n&gt;.&lt;id&gt;.&lt;data&gt;</c>, so a phone's
/// own camera opens the web app with the card, and ChameLab's scanner reads the same codes.
/// The card is its JSON (without the library id and folder) with byte arrays as hex, deflated and
/// base64url encoded; a card too big for one readable code is split evenly over several.
/// The data is in the URL fragment, which browsers never send to the server.
/// </summary>
public static class CardQr
{
    /// <summary>The stable web app the links open.</summary>
    public const string WebAppUrl = "https://m0n5ter.github.io/ChameLab/app/";

    /// <summary>Most base64 characters per code: with the link around them a version ~17 code, easy to scan off a screen.</summary>
    public const int MaxPartChars = 600;

    private const string Key = "card=";
    private const int FormatVersion = 1;

    /// <summary>The links of the codes that carry <paramref name="card"/>, in order.</summary>
    public static IReadOnlyList<string> Encode(CardSave card, string baseUrl = WebAppUrl, int maxPartChars = MaxPartChars)
    {
        var payload = Pack(card);
        var id = IdOf(payload);
        var data = Base64Url(payload);
        var count = Math.Max(1, (data.Length + maxPartChars - 1) / maxPartChars);
        var size = (data.Length + count - 1) / count;
        var parts = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * size;
            var slice = start >= data.Length ? string.Empty : data.Substring(start, Math.Min(size, data.Length - start));
            parts.Add($"{baseUrl}#{Key}{FormatVersion}.{i + 1}.{count}.{id}.{slice}");
        }
        return parts;
    }

    /// <summary>
    /// Reads a scanned code (or a pasted link / fragment): any text with <c>card=</c> in its fragment,
    /// whatever the host, so links to a development build work too.
    /// </summary>
    public static bool TryParse(string? text, out CardQrPart part)
    {
        part = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var hash = text.IndexOf('#');
        var fragment = hash >= 0 ? text[(hash + 1)..] : text;
        var at = fragment.StartsWith(Key, StringComparison.Ordinal) ? 0 : fragment.IndexOf("&" + Key, StringComparison.Ordinal) + 1;
        if (at < 0 || !fragment.AsSpan(at).StartsWith(Key, StringComparison.Ordinal)) return false;
        var value = fragment[(at + Key.Length)..];
        var amp = value.IndexOf('&');
        if (amp >= 0) value = value[..amp];

        var fields = value.Split('.');
        if (fields.Length != 5) return false;
        if (fields[0] != FormatVersion.ToString()) return false;
        if (!int.TryParse(fields[1], out var index) || !int.TryParse(fields[2], out var count)) return false;
        if (count < 1 || count > 99 || index < 1 || index > count) return false;
        if (fields[3].Length != 8 || !fields[3].All(Uri.IsHexDigit)) return false;
        if (!fields[4].All(IsBase64Url)) return false;
        part = new CardQrPart(index, count, fields[3].ToLowerInvariant(), fields[4]);
        return true;
    }

    /// <summary>The card carried by a full, ordered set of parts.</summary>
    /// <exception cref="FormatException">The parts don't add up to a card.</exception>
    public static CardSave Assemble(IReadOnlyList<CardQrPart> parts)
    {
        byte[] payload;
        try { payload = FromBase64Url(string.Concat(parts.Select(p => p.Data))); }
        catch (FormatException) { throw new FormatException(Strings.Qr_Damaged); }
        if (IdOf(payload) != parts[0].Id) throw new FormatException(Strings.Qr_Damaged);
        return Unpack(payload);
    }

    // ---- Payload ---------------------------------------------------------

    internal static byte[] Pack(CardSave card)
    {
        var json = card.ToJson();
        // The receiver files it as a new card of its own library.
        json.Remove("id");
        json.Remove("folder");
        foreach (var key in new[] { "atqa", "ats" })
            if (json[key] is JsonArray a) json[key] = ToHex(a);
        if (json["data"] is JsonArray data)
            json["data"] = new JsonArray(data.Select(d => (JsonNode)ToHex((JsonArray)d!)).ToArray());

        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(text);
        return output.ToArray();
    }

    internal static CardSave Unpack(byte[] payload)
    {
        JsonObject json;
        try
        {
            using var input = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress);
            using var text = new MemoryStream();
            input.CopyTo(text);
            json = JsonNode.Parse(text.ToArray()) as JsonObject ?? throw new FormatException(Strings.Qr_Damaged);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new FormatException(Strings.Qr_Damaged);
        }

        foreach (var key in new[] { "atqa", "ats" })
            if (json[key] is JsonValue v && (string?)v is { } hex) json[key] = FromHex(hex);
        if (json["data"] is JsonArray data)
            json["data"] = new JsonArray(data.Select(d => d is JsonValue v && (string?)v is { } hex
                ? FromHex(hex) : d?.DeepClone()).ToArray());
        var card = CardSave.FromJson(json);
        card.Id = Guid.NewGuid().ToString();
        return card;
    }

    private static string ToHex(JsonArray bytes) =>
        Convert.ToHexString(bytes.Select(b => (byte)((int?)b ?? 0)).ToArray());

    private static JsonArray FromHex(string hex) =>
        new(Convert.FromHexString(hex).Select(b => (JsonNode)(int)b).ToArray());

    private static string IdOf(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload), 0, 4).ToLowerInvariant();

    private static bool IsBase64Url(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}

/// <summary>What happened to a scanned code.</summary>
public enum CardQrScan
{
    /// <summary>Not a ChameLab card code.</summary>
    NotACard,
    /// <summary>A part already received.</summary>
    Repeated,
    /// <summary>A new part; more are still missing.</summary>
    Added,
    /// <summary>The last missing part: <see cref="CardQrCollector.Card"/> is ready.</summary>
    Complete,
}

/// <summary>
/// Gathers the codes of one card in any order. A code of another card starts over with that card,
/// so a scan that wandered to a different screen doesn't mix two cards.
/// </summary>
public sealed class CardQrCollector
{
    private readonly Dictionary<int, CardQrPart> _parts = new();
    private string? _id;

    /// <summary>Parts of the current card received so far.</summary>
    public int Received => _parts.Count;

    /// <summary>Parts the current card has; 0 before the first code.</summary>
    public int Count { get; private set; }

    /// <summary>The card, once every part is in.</summary>
    public CardSave? Card { get; private set; }

    /// <summary>The 1-based parts still missing.</summary>
    public IEnumerable<int> Missing => Enumerable.Range(1, Count).Where(i => !_parts.ContainsKey(i));

    /// <exception cref="FormatException">All parts are in but they don't make a card.</exception>
    public CardQrScan Add(string? text) =>
        CardQr.TryParse(text, out var part) ? Add(part) : CardQrScan.NotACard;

    /// <exception cref="FormatException">All parts are in but they don't make a card.</exception>
    public CardQrScan Add(CardQrPart part)
    {
        if (part.Id != _id || part.Count != Count)
        {
            Reset();
            _id = part.Id;
            Count = part.Count;
        }
        else if (_parts.ContainsKey(part.Index) || Card is not null)
            return CardQrScan.Repeated;

        _parts[part.Index] = part;
        if (_parts.Count < Count) return CardQrScan.Added;
        Card = CardQr.Assemble(Enumerable.Range(1, Count).Select(i => _parts[i]).ToList());
        return CardQrScan.Complete;
    }

    public void Reset()
    {
        _parts.Clear();
        _id = null;
        Count = 0;
        Card = null;
    }
}
