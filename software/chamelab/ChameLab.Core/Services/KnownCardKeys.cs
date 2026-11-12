using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

/// <summary>The kind of secret a known card holds: MIFARE Classic sector keys, or one password.</summary>
public enum KnownCardFamily { MifareClassic, Ultralight, T55xx }

/// <summary>
/// One learned card. <see cref="Tag"/> is the exact type when it was known (for a T55xx, the format it
/// broadcasts); MIFARE Classic cards hold <see cref="Keys"/>, the others one <see cref="Password"/>.
/// </summary>
public sealed record KnownCard(KnownCardFamily Family, string Uid, TagSpecificType? Tag,
    IReadOnlyList<byte[]> Keys, byte[]? Password)
{
    public KnownCardId Id => new(Family, Uid);
}

public readonly record struct KnownCardId(KnownCardFamily Family, string Uid);
public sealed record KnownMifareKey(int Sector, MfKeyType KeyType, byte[] Key);

public static class KnownCardFamilies
{
    public static readonly KnownCardFamily[] All = Enum.GetValues<KnownCardFamily>();

    /// <summary>The family a tag type belongs to; null for types with no learned secret.</summary>
    public static KnownCardFamily? Of(TagSpecificType tag) =>
        tag.IsMifareClassic() ? KnownCardFamily.MifareClassic
        : tag.IsUltralight() ? KnownCardFamily.Ultralight
        : tag.SenseType() == TagSenseType.Lf ? KnownCardFamily.T55xx
        : null;

    /// <summary>The tag types a card of this family can be, in display order.</summary>
    public static IReadOnlyList<TagSpecificType> Tags(this KnownCardFamily family) =>
        TagTypeExtensions.All().Where(t => Of(t) == family).ToArray();

    public static string DisplayName(this KnownCardFamily family) => family switch
    {
        KnownCardFamily.MifareClassic => "MIFARE Classic",
        KnownCardFamily.Ultralight => "NTAG / Ultralight",
        _ => "T55XX",
    };

    /// <summary>A few letters for a list row: “MFC 1K”, “NTAG215”, “UL EV1”, “EM”; the family's when the type is unknown.</summary>
    public static string ShortName(this KnownCardFamily family, TagSpecificType? tag) => tag switch
    {
        TagSpecificType.MifareMini => "MFC Mini",
        TagSpecificType.Mifare1024 => "MFC 1K",
        TagSpecificType.Mifare2048 => "MFC 2K",
        TagSpecificType.Mifare4096 => "MFC 4K",
        TagSpecificType.Ntag210 => "NTAG210",
        TagSpecificType.Ntag212 => "NTAG212",
        TagSpecificType.Ntag213 => "NTAG213",
        TagSpecificType.Ntag215 => "NTAG215",
        TagSpecificType.Ntag216 => "NTAG216",
        TagSpecificType.Mf0Icu1 => "UL",
        TagSpecificType.Mf0Icu2 => "UL C",
        TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21 => "UL EV1",
        { } t when t.IsEm410x() => "EM",
        TagSpecificType.HidProx => "HID",
        TagSpecificType.IoProx => "ioProx",
        TagSpecificType.FarpointePyramid => "Pyramid",
        TagSpecificType.Pac => "PAC",
        TagSpecificType.Viking => "Viking",
        TagSpecificType.Jablotron => "Jablotron",
        TagSpecificType.Idteck => "IDTECK",
        _ => family switch
        {
            KnownCardFamily.MifareClassic => "MFC",
            KnownCardFamily.Ultralight => "NTAG / UL",
            _ => "T55XX",
        },
    };

    /// <summary>The bytes of the family's secret: a 6-byte key or a 4-byte password.</summary>
    public static int SecretLength(this KnownCardFamily family) => family == KnownCardFamily.MifareClassic ? 6 : 4;

    internal static string JsonName(this KnownCardFamily family) => family switch
    {
        KnownCardFamily.MifareClassic => "mifareClassic",
        KnownCardFamily.Ultralight => "ultralight",
        _ => "t55xx",
    };
}

/// <summary>
/// Automatically learned keys, isolated from editable dictionaries and indexed by card: MIFARE Classic
/// keys, the last known Ultralight / NTAG password, and the last known T55xx password (by the ID the tag
/// broadcasts), each with the card's type when it was known.
/// </summary>
public sealed class KnownCardKeys
{
    public const string Id = "known-card-keys";
    public static string Name => Strings.Dictionaries_KnownCardKeys;
    /// <summary>Version-1 root entry holding the NTAG / Ultralight EV1 passwords.</summary>
    public const string LegacyPasswordsSection = "ntagPasswords";
    private const int FormatVersion = 2;
    private readonly IKeyValueStore _store;
    private Dictionary<KnownCardId, Entry> _cards = new();

    private sealed record Position(int Sector, MfKeyType KeyType, string Key);
    private sealed record Entry(TagSpecificType? Tag, List<string> Keys, string? Password)
    {
        public List<Position> Positions { get; init; } = [];
    }

    public KnownCardKeys(IKeyValueStore store)
    {
        _store = store;
        try
        {
            if (store.Read(Id) is { } json) _cards = Parse(JsonNode.Parse(json));
        }
        catch (Exception ex) { LoadError = string.Format(Strings.Library_KnownKeysLoadError, ex.Message); }
    }

    public string? LoadError { get; }
    public event Action? Changed;
    public int CardCount => _cards.Count;
    public int KeyCount => _cards.Values.Sum(e => e.Keys.Count);
    public int PasswordCount => CountPasswords(KnownCardFamily.Ultralight);
    public int T55xxPasswordCount => CountPasswords(KnownCardFamily.T55xx);
    private int CountPasswords(KnownCardFamily family) =>
        _cards.Count(c => c.Key.Family == family && c.Value.Password is not null);

    /// <summary>Every card, by family and then UID.</summary>
    public IReadOnlyList<KnownCard> Cards => _cards
        .OrderBy(c => c.Key.Family).ThenBy(c => c.Key.Uid, StringComparer.Ordinal)
        .Select(c => ToCard(c.Key, c.Value)).ToArray();

    public IReadOnlyList<byte[]> ForUid(byte[] uid) =>
        _cards.TryGetValue(new(KnownCardFamily.MifareClassic, Convert.ToHexString(uid)), out var entry)
            ? entry.Keys.Select(Convert.FromHexString).ToArray() : Array.Empty<byte[]>();

    public IReadOnlyList<KnownMifareKey> PositionsForUid(byte[] uid) =>
        _cards.TryGetValue(new(KnownCardFamily.MifareClassic, Convert.ToHexString(uid)), out var entry)
            ? entry.Positions.Select(p => new KnownMifareKey(p.Sector, p.KeyType, Convert.FromHexString(p.Key))).ToArray() : [];

    /// <summary>The 4-byte PWD the NTAG / Ultralight with this UID last accepted or was given; null when unknown.</summary>
    public byte[]? PasswordForUid(byte[] uid) => PasswordFor(KnownCardFamily.Ultralight, uid);

    /// <summary>The password the T55xx broadcasting this ID was last given; null when unknown.</summary>
    public byte[]? T55xxPasswordForId(byte[] id) => PasswordFor(KnownCardFamily.T55xx, id);

    /// <summary>Every learned T55xx password, de-duplicated, for a dictionary pass.</summary>
    public IReadOnlyList<byte[]> AllT55xxPasswords() => _cards
        .Where(c => c.Key.Family == KnownCardFamily.T55xx && c.Value.Password is not null)
        .Select(c => Convert.FromHexString(c.Value.Password!)).DistinctBy(Convert.ToHexString).ToArray();

    private byte[]? PasswordFor(KnownCardFamily family, byte[] uid) =>
        _cards.TryGetValue(new(family, Convert.ToHexString(uid)), out var entry) && entry.Password is { } pwd
            ? Convert.FromHexString(pwd) : null;

    /// <summary>Commit edited cards together, leaving unrelated learned keys intact.</summary>
    public void ApplyEdits(IEnumerable<KnownCard> changed, IEnumerable<KnownCardId> removed)
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        var next = Copy();
        foreach (var id in removed) next.Remove(Normalize(id));
        foreach (var card in changed)
        {
            var id = Normalize(card.Id);
            var tag = ValidTag(id.Family, card.Tag);
            if (id.Family == KnownCardFamily.MifareClassic)
            {
                if (card.Keys.Any(k => k.Length != 6)) throw new FormatException(Strings.Keys_KeyLength);
                var keys = card.Keys.Select(Convert.ToHexString).Distinct().ToList();
                next[id] = new(tag, keys, null) { Positions = next.GetValueOrDefault(id)?.Positions
                    .Where(p => keys.Contains(p.Key)).ToList() ?? [] };
            }
            else
            {
                if (card.Password is not { Length: 4 } pwd) throw new FormatException(Strings.Common_Password4Bytes);
                next[id] = new(tag, new(), Convert.ToHexString(pwd));
            }
        }
        Save(next);
    }

    private static KnownCardId Normalize(KnownCardId id)
    {
        var uid = Convert.ToHexString(Convert.FromHexString(id.Uid));
        if (uid.Length == 0) throw new FormatException(Strings.Keys_InvalidUid);
        return id with { Uid = uid };
    }

    public JsonObject ToJson() => ToJson(_cards);

    public void Merge(JsonNode node)
    {
        // Validate the entire backup before adding any keys.
        var incoming = Parse(node);
        var next = Copy();
        foreach (var (id, entry) in incoming)
        {
            if (!next.TryGetValue(id, out var local)) { next[id] = entry; continue; }
            var keys = new List<string>(local.Keys);
            foreach (var key in entry.Keys) if (!keys.Contains(key)) keys.Add(key);
            // A password already known here is newer than the backup's.
            next[id] = new(local.Tag ?? entry.Tag, keys, local.Password ?? entry.Password)
            { Positions = local.Positions.Concat(entry.Positions).DistinctBy(p => (p.Sector, p.KeyType)).ToList() };
        }
        if (Same(next)) return;
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        Save(next);
    }

    public void Remember(byte[] uid, IEnumerable<byte[]> keys, TagSpecificType? tag = null)
    {
        if (uid.Length == 0) return;
        var id = new KnownCardId(KnownCardFamily.MifareClassic, Convert.ToHexString(uid));
        _cards.TryGetValue(id, out var old);
        var values = old is null ? new List<string>() : new List<string>(old.Keys);
        var seen = values.ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
            if (key.Length == 6 && seen.Add(Convert.ToHexString(key))) values.Add(Convert.ToHexString(key));
        if (values.Count == 0) return;
        Put(id, new(ValidTag(id.Family, tag) ?? old?.Tag, values, null) { Positions = old?.Positions ?? [] });
    }

    /// <summary>Every key of the dump, with the card's MIFARE Classic type.</summary>
    public void Remember(MifareDump dump) => RememberPositions(dump.Uid,
        Enumerable.Range(0, dump.SectorCount).SelectMany(s => new[] {
            new KnownMifareKey(s, MfKeyType.A, dump.KeyA[s] ?? []),
            new KnownMifareKey(s, MfKeyType.B, dump.KeyB[s] ?? []) }), MifareClassic.ToSlotTagType(dump.Type));

    public void RememberPosition(byte[] uid, int sector, MfKeyType type, byte[] key, TagSpecificType? tag = null) =>
        RememberPositions(uid, [new(sector, type, key)], tag);

    private void RememberPositions(byte[] uid, IEnumerable<KnownMifareKey> positions, TagSpecificType? tag)
    {
        if (uid.Length == 0) return;
        var id = new KnownCardId(KnownCardFamily.MifareClassic, Convert.ToHexString(uid));
        var old = _cards.GetValueOrDefault(id);
        var values = old?.Keys.ToList() ?? [];
        var slots = old?.Positions.ToDictionary(p => (p.Sector, p.KeyType)) ?? [];
        foreach (var p in positions)
        {
            if (p.Key.Length != 6 || p.Sector is < 0 or >= 40 || p.KeyType is not (MfKeyType.A or MfKeyType.B)) continue;
            var key = Convert.ToHexString(p.Key);
            if (!values.Contains(key)) values.Add(key);
            slots[(p.Sector, p.KeyType)] = new(p.Sector, p.KeyType, key);
        }
        if (values.Count == 0) return;
        Put(id, new(ValidTag(id.Family, tag) ?? old?.Tag, values, null) { Positions = slots.Values.ToList() });
    }

    /// <summary>Adds or replaces the NTAG / Ultralight password: a card has one PWD at a time.</summary>
    public void RememberPassword(byte[] uid, byte[] pwd, TagSpecificType? tag = null) =>
        PutPassword(KnownCardFamily.Ultralight, uid, pwd, tag);

    /// <summary>Adds or replaces the password of the T55xx now broadcasting this ID.</summary>
    public void RememberT55xxPassword(byte[] id, byte[] pwd, TagSpecificType? tag = null) =>
        PutPassword(KnownCardFamily.T55xx, id, pwd, tag);

    private void PutPassword(KnownCardFamily family, byte[] uid, byte[] pwd, TagSpecificType? tag)
    {
        if (uid.Length == 0 || pwd.Length != 4) return;
        var id = new KnownCardId(family, Convert.ToHexString(uid));
        _cards.TryGetValue(id, out var old);
        Put(id, new(ValidTag(family, tag) ?? old?.Tag, new(), Convert.ToHexString(pwd)));
    }

    private void Put(KnownCardId id, Entry entry)
    {
        if (_cards.TryGetValue(id, out var old) && SameEntry(old, entry)) return;
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        var next = Copy();
        next[id] = entry;
        Save(next);
    }

    private Dictionary<KnownCardId, Entry> Copy() =>
        _cards.ToDictionary(c => c.Key, c => c.Value with { Keys = new List<string>(c.Value.Keys), Positions = c.Value.Positions.ToList() });

    private bool Same(Dictionary<KnownCardId, Entry> next) =>
        next.Count == _cards.Count && next.All(c => _cards.TryGetValue(c.Key, out var e) && SameEntry(e, c.Value));

    private static bool SameEntry(Entry a, Entry b) =>
        a.Tag == b.Tag && a.Password == b.Password && a.Keys.SequenceEqual(b.Keys) && a.Positions.SequenceEqual(b.Positions);

    private static TagSpecificType? ValidTag(KnownCardFamily family, TagSpecificType? tag) =>
        tag is { } t && KnownCardFamilies.Of(t) == family ? t : null;

    private static KnownCard ToCard(KnownCardId id, Entry entry) =>
        new(id.Family, id.Uid, entry.Tag, entry.Keys.Select(Convert.FromHexString).ToArray(),
            entry.Password is { } pwd ? Convert.FromHexString(pwd) : null);

    private void Save(Dictionary<KnownCardId, Entry> cards)
    {
        _store.Write(Id, ToJson(cards).ToJsonString());
        _cards = cards;
        Changed?.Invoke();
    }

    private static JsonObject ToJson(Dictionary<KnownCardId, Entry> cards)
    {
        var list = new JsonArray();
        foreach (var (id, entry) in cards.OrderBy(c => c.Key.Family).ThenBy(c => c.Key.Uid, StringComparer.Ordinal))
        {
            var card = new JsonObject { ["family"] = id.Family.JsonName(), ["uid"] = id.Uid };
            if (entry.Tag is { } tag) card["tag"] = tag.ToString();
            if (id.Family == KnownCardFamily.MifareClassic)
            {
                card["keys"] = new JsonArray(entry.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
                if (entry.Positions.Count > 0) card["sectors"] = new JsonArray(entry.Positions.Select(p => (JsonNode)new JsonObject
                    { ["sector"] = p.Sector, ["type"] = p.KeyType == MfKeyType.A ? "A" : "B", ["key"] = p.Key }).ToArray());
            }
            else card["password"] = entry.Password;
            list.Add((JsonNode)card); // the generic Add<T> isn't trim-safe
        }
        return new JsonObject { ["version"] = FormatVersion, ["cards"] = list };
    }

    private static Dictionary<KnownCardId, Entry> Parse(JsonNode? node)
    {
        if (node is not JsonObject root) throw new FormatException(Strings.Keys_ExpectedGrouped);
        return root.ContainsKey("version") ? ParseCards(root) : ParseLegacy(root);
    }

    private static Dictionary<KnownCardId, Entry> ParseCards(JsonObject root)
    {
        if (root["version"]?.GetValue<int>() is not FormatVersion || root["cards"] is not JsonArray cards)
            throw new FormatException(Strings.Keys_ExpectedGrouped);
        var result = new Dictionary<KnownCardId, Entry>();
        foreach (var node in cards)
        {
            if (node is not JsonObject card) throw new FormatException(Strings.Keys_InvalidUidGroup);
            var familyName = card["family"]?.GetValue<string>();
            var family = KnownCardFamilies.All.Cast<KnownCardFamily?>().FirstOrDefault(f => f!.Value.JsonName() == familyName)
                ?? throw new FormatException(Strings.Keys_InvalidUidGroup);
            var uid = ParseUid(card["uid"]?.GetValue<string>());
            // An unknown type name (from a newer version) only loses the type, not the card.
            var tag = Enum.TryParse<TagSpecificType>(card["tag"]?.GetValue<string>(), out var t) ? ValidTag(family, t) : null;
            var id = new KnownCardId(family, uid);
            if (family == KnownCardFamily.MifareClassic)
            {
                if (card["keys"] is not JsonArray keys) throw new FormatException(Strings.Keys_InvalidUidGroup);
                AddKeys(result, id, tag, keys);
                if (card["sectors"] is JsonArray positions)
                    foreach (var nodePosition in positions)
                    {
                        var sector = nodePosition?["sector"]?.GetValue<int>() ?? -1;
                        var type = nodePosition?["type"]?.GetValue<string>();
                        var key = Convert.FromHexString(nodePosition?["key"]?.GetValue<string>() ?? "");
                        if (sector is < 0 or >= 40 || type is not ("A" or "B") || key.Length != 6)
                            throw new FormatException(Strings.Keys_InvalidKey);
                        var hex = Convert.ToHexString(key);
                        if (!result[id].Keys.Contains(hex)) result[id].Keys.Add(hex);
                        var kt = type == "A" ? MfKeyType.A : MfKeyType.B;
                        result[id].Positions.RemoveAll(p => p.Sector == sector && p.KeyType == kt);
                        result[id].Positions.Add(new(sector, kt, hex));
                    }
            }
            else result[id] = new(tag, new(), ParsePassword(card["password"]?.GetValue<string>()));
        }
        return result;
    }

    private static Dictionary<KnownCardId, Entry> ParseLegacy(JsonObject root)
    {
        var result = new Dictionary<KnownCardId, Entry>();
        foreach (var (uid, value) in root)
        {
            if (uid == LegacyPasswordsSection)
            {
                if (value is not JsonObject section) throw new FormatException(Strings.Keys_InvalidPasswordSection);
                foreach (var (cardUid, pwd) in section)
                    result[new(KnownCardFamily.Ultralight, ParseUid(cardUid))] =
                        new(null, new(), ParsePassword(pwd?.GetValue<string>()));
                continue;
            }
            if (value is not JsonArray keys) throw new FormatException(Strings.Keys_InvalidUidGroup);
            AddKeys(result, new(KnownCardFamily.MifareClassic, ParseUid(uid)), null, keys);
        }
        return result;
    }

    private static void AddKeys(Dictionary<KnownCardId, Entry> result, KnownCardId id, TagSpecificType? tag, JsonArray keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            var bytes = Convert.FromHexString(key?.GetValue<string>() ?? "");
            if (bytes.Length != 6) throw new FormatException(Strings.Keys_InvalidKey);
            values.Add(Convert.ToHexString(bytes));
        }
        if (!result.TryGetValue(id, out var existing)) result[id] = existing = new(tag, new(), null);
        foreach (var key in values) if (!existing.Keys.Contains(key)) existing.Keys.Add(key);
    }

    private static string ParseUid(string? uid)
    {
        var normalized = Convert.ToHexString(Convert.FromHexString(uid ?? ""));
        if (normalized.Length == 0) throw new FormatException(Strings.Keys_InvalidUidGroup);
        return normalized;
    }

    private static string ParsePassword(string? pwd)
    {
        var bytes = Convert.FromHexString(pwd ?? "");
        if (bytes.Length != 4) throw new FormatException(Strings.Keys_InvalidPassword);
        return Convert.ToHexString(bytes);
    }
}
