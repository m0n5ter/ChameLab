using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ChameLab.Models;

/// <summary>
/// A contact card (vCard 3.0) in an NDEF MIME record (text/vcard): a phone adds the contact when
/// the tag is tapped. Only the fields edited here are kept; a card with anything else (an address,
/// a photo, a second number) is not read as one, so it stays MIME data and is never cut down.
/// </summary>
public sealed record NdefVCard(string Name, string Phone, string Email, string Organization, string Website)
{
    public const string MimeType = "text/vcard";

    /// <summary>Properties shown as fields, plus the ones every card has.</summary>
    private static readonly HashSet<string> Known = ["BEGIN", "VERSION", "N", "FN", "TEL", "EMAIL", "ORG", "URL", "END"];

    public byte[] ToBytes()
    {
        var lines = new List<string> { "BEGIN:VCARD", "VERSION:3.0", $"N:;{Escape(Name)};;;", $"FN:{Escape(Name)}" };
        if (Organization.Length > 0) lines.Add($"ORG:{Escape(Organization)}");
        if (Phone.Length > 0) lines.Add($"TEL:{Escape(Phone)}");
        if (Email.Length > 0) lines.Add($"EMAIL:{Escape(Email)}");
        if (Website.Length > 0) lines.Add($"URL:{Escape(Website)}");
        lines.Add("END:VCARD");
        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
    }

    /// <summary>The card in a MIME record's payload, when it has only the fields edited here (at most one of each).</summary>
    public static bool TryParse(string mimeType, byte[] payload, out NdefVCard card)
    {
        card = new NdefVCard("", "", "", "", "");
        if (!mimeType.Equals(MimeType, StringComparison.OrdinalIgnoreCase)
            && !mimeType.Equals("text/x-vcard", StringComparison.OrdinalIgnoreCase))
            return false;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException) { return false; }

        // Lines folded with a leading space or tab continue the line before.
        var unfolded = text.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
        var values = new Dictionary<string, string>();
        foreach (var line in unfolded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return false;
            // "TEL;TYPE=CELL" names TEL; a group prefix ("item1.TEL") is not something to keep quietly.
            var name = line[..colon].Split(';')[0].ToUpperInvariant();
            if (!Known.Contains(name) || values.ContainsKey(name)) return false;
            values[name] = line[(colon + 1)..];
        }
        if (!values.TryGetValue("BEGIN", out var begin) || !begin.Equals("VCARD", StringComparison.OrdinalIgnoreCase)
            || !values.ContainsKey("END"))
            return false;

        var fullName = values.TryGetValue("FN", out var fn) ? Unescape(fn)
            : values.TryGetValue("N", out var n) ? string.Join(" ", n.Split(';').Take(2).Reverse().Where(p => p.Length > 0).Select(Unescape))
            : "";
        card = new NdefVCard(fullName,
            Unescape(values.GetValueOrDefault("TEL", "")),
            Unescape(values.GetValueOrDefault("EMAIL", "")),
            Unescape(values.GetValueOrDefault("ORG", "").TrimEnd(';')),
            Unescape(values.GetValueOrDefault("URL", "")));
        return true;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }
}
