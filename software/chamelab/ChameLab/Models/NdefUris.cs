using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ChameLab.Models;

/// <summary>An email to write: the mailto: link of an NDEF URI record.</summary>
public sealed record NdefEmail(string Address, string Subject, string Body);

/// <summary>A text message to send: the sms: link of an NDEF URI record.</summary>
public sealed record NdefSms(string Number, string Body);

/// <summary>A place on the map: the geo: link of an NDEF URI record.</summary>
public sealed record NdefGeo(double Latitude, double Longitude);

/// <summary>
/// The links phones act on, kept in NDEF URI records: a phone number to call (tel:), an email
/// (mailto:), a text message (sms:) and a place (geo:). Parsing is lenient about case and
/// returns false for anything it can't show as fields, which then stays an ordinary link.
/// </summary>
public static class NdefUris
{
    public static string Phone(string number) => "tel:" + Compact(number);

    public static bool TryPhone(string uri, out string number)
    {
        number = string.Empty;
        if (!uri.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || uri.Contains('?')) return false;
        number = Uri.UnescapeDataString(uri[4..]);
        return number.Length > 0;
    }

    public static string Email(NdefEmail email)
    {
        var query = Query(("subject", email.Subject), ("body", email.Body));
        return "mailto:" + email.Address.Trim() + query;
    }

    public static bool TryEmail(string uri, out NdefEmail email)
    {
        email = new NdefEmail(string.Empty, string.Empty, string.Empty);
        if (!uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return false;
        var (path, fields) = Split(uri[7..]);
        if (fields is null) return false;
        var address = Uri.UnescapeDataString(path);
        if (!address.Contains('@') || address.Contains(',')) return false;
        if (fields.Keys.Any(k => k is not ("subject" or "body"))) return false;
        email = new NdefEmail(address, fields.GetValueOrDefault("subject", ""), fields.GetValueOrDefault("body", ""));
        return true;
    }

    public static string Sms(NdefSms sms) => "sms:" + Compact(sms.Number) + Query(("body", sms.Body));

    public static bool TrySms(string uri, out NdefSms sms)
    {
        sms = new NdefSms(string.Empty, string.Empty);
        if (!uri.StartsWith("sms:", StringComparison.OrdinalIgnoreCase)) return false;
        var (path, fields) = Split(uri[4..]);
        if (fields is null || fields.Keys.Any(k => k != "body")) return false;
        var number = Uri.UnescapeDataString(path);
        if (number.Length == 0 || number.Contains(',')) return false;
        sms = new NdefSms(number, fields.GetValueOrDefault("body", ""));
        return true;
    }

    public static string Geo(NdefGeo geo) =>
        string.Create(CultureInfo.InvariantCulture, $"geo:{geo.Latitude},{geo.Longitude}");

    public static bool TryGeo(string uri, out NdefGeo geo)
    {
        geo = new NdefGeo(0, 0);
        if (!uri.StartsWith("geo:", StringComparison.OrdinalIgnoreCase)) return false;
        // Only the plain "geo:lat,lon" form; altitude, parameters and queries stay a link.
        var parts = uri[4..].Split(',');
        if (parts.Length != 2
            || !TryCoordinate(parts[0], 90, out var lat)
            || !TryCoordinate(parts[1], 180, out var lon))
            return false;
        geo = new NdefGeo(lat, lon);
        return true;
    }

    /// <summary>A latitude (limit 90) or longitude (limit 180) in degrees, with a dot for decimals.</summary>
    public static bool TryCoordinate(string text, double limit, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value) && Math.Abs(value) <= limit;

    /// <summary>A phone number as dialed: spaces and dashes typed for reading are dropped.</summary>
    private static string Compact(string number) =>
        new(number.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '(' or ')' or '.')).ToArray());

    private static string Query(params (string Key, string Value)[] fields)
    {
        var set = fields.Where(f => f.Value.Length > 0)
            .Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value)).ToList();
        return set.Count == 0 ? string.Empty : "?" + string.Join("&", set);
    }

    /// <summary>The part before "?" and the query's fields (lowercase keys); null fields when the query can't be read.</summary>
    private static (string Path, Dictionary<string, string>? Fields) Split(string rest)
    {
        var at = rest.IndexOf('?');
        var fields = new Dictionary<string, string>();
        if (at < 0) return (rest, fields);
        foreach (var pair in rest[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) return (rest, null);
            var key = pair[..eq].ToLowerInvariant();
            if (fields.ContainsKey(key)) return (rest, null);
            fields[key] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return (rest[..at], fields);
    }
}
