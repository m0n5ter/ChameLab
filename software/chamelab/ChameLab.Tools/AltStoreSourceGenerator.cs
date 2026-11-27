using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ChameLab.Tools;

internal static partial class AltStoreSourceGenerator
{
    private const string DefaultRepository = "m0n5ter/ChameLab";
    private const string AccentTintColor = "#D6BA81";
    private const string DeveloperName = "m0n5ter";
    private const string PatreonUrl = "https://www.patreon.com/ChameLab";
    private const string Subtitle = "Slots, dumps, key recovery and sniffing for ChameleonUltra and ChameleonLite";

    private const string AppDescription =
        """
        ChameLab is a free, open-source app for the ChameleonUltra and ChameleonLite RFID emulators. Connect over Bluetooth LE or TCP and manage everything your device can do from your iPhone or iPad.

        • Slots: all eight slots at a glance, with each card's name and LED color. Drag to reorder, tap to activate, edit HF and LF separately.
        • Multi-card LF: one LF slot holds up to eight LF cards of any type and presents the next one on each tap of a reader. Needs the ChameLab firmware, which the app installs for you.
        • Read and dump: ISO 14443-A and 125 kHz scans, MIFARE Classic Mini, 1K and 4K dumps with your key dictionaries, a hex editor and one-tap emulation.
        • Card library: search and color tags. Imports Proxmark3, Flipper .nfc / .rfid, MCT, ChameleonUltraGUI, .bin and .eml files.
        • Key recovery: Darkside, Nested, Static nested, Hardnested and mfkey32, computed on the phone.
        • Sniffing: record a reader talking to the emulated card and recover MIFARE Classic keys; capture the 125 kHz signal as a waveform.
        • Writing: T55xx and EM4305 tags, Gen1a and Gen2 magic cards.
        • Device settings and firmware updates over Bluetooth LE.
        • Every long operation can be stopped.

        Requires a ChameleonUltra or ChameleonLite device. Free software under the GNU GPL v3.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-dev\.(\d+))?(?:[-+]\S*)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\bv?(\d+\.\d+(?:\.\d+)?(?:-dev\.\d+)?)\b")]
    private static partial Regex VersionInText();

    [GeneratedRegex(@"^chamelab-.*ios\.ipa$", RegexOptions.IgnoreCase)]
    private static partial Regex IpaAssetPattern();

    [GeneratedRegex(@"`|\*\*")]
    private static partial Regex MarkdownFormatPattern();

    public static void Run(string root, string inputPath, string outputDirectory, string[] extraArgs)
    {
        var baseUrl = ParseBaseUrl(extraArgs) ?? DefaultBaseUrl();
        if (!baseUrl.EndsWith('/')) baseUrl += "/";

        using var inputDoc = JsonDocument.Parse(File.ReadAllText(inputPath));
        var (releases, devRelease) = ParseInput(inputDoc.RootElement);

        var metadata = ReadAppleMetadata(
            Path.Combine(root, "software/chamelab/ChameLab.iOS/Info.plist"),
            Path.Combine(root, "software/chamelab/ChameLab.iOS/Entitlements.plist"));
        var changelogLines = File.ReadAllLines(Path.Combine(root, "CHANGELOG.md"));

        Directory.CreateDirectory(outputDirectory);
        var stableJson = BuildSource(releases, devRelease: null, changelogLines, metadata, baseUrl, isDevFeed: false);
        var devJson = BuildSource(releases, devRelease, changelogLines, metadata, baseUrl, isDevFeed: true);

        File.WriteAllText(Path.Combine(outputDirectory, "altstore.json"), stableJson.ToJsonString(JsonOptions) + "\n");
        File.WriteAllText(Path.Combine(outputDirectory, "altstore-dev.json"), devJson.ToJsonString(JsonOptions) + "\n");
    }

    internal static (List<JsonElement> Releases, JsonElement? Dev) ParseInput(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            var list = root.EnumerateArray().Select(e => e.Clone()).ToList();
            JsonElement? dev = null;
            foreach (var r in list)
            {
                if (GetString(r, "tag_name") == "dev") { dev = r; break; }
            }
            return (list, dev);
        }

        var releases = root.TryGetProperty("releases", out var relProp) && relProp.ValueKind == JsonValueKind.Array
            ? relProp.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
        JsonElement? devObj = root.TryGetProperty("dev", out var devProp) && devProp.ValueKind == JsonValueKind.Object
            ? devProp.Clone()
            : null;
        return (releases, devObj);
    }

    internal static JsonObject BuildSource(
        IReadOnlyList<JsonElement> releases,
        JsonElement? devRelease,
        string[] changelogLines,
        AppleAppMetadata metadata,
        string baseUrl,
        bool isDevFeed)
    {
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var fileName = isDevFeed ? "altstore-dev.json" : "altstore.json";
        var sourceUrl = baseUrl + fileName;
        var iconUrl = baseUrl + "icon.png";

        var versions = BuildVersions(releases, devRelease, changelogLines, metadata.MinimumOsVersion, isDevFeed);

        var permissions = new JsonObject
        {
            ["entitlements"] = new JsonArray(metadata.Entitlements.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
            ["privacy"] = new JsonObject(metadata.Privacy.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
        };

        var app = new JsonObject
        {
            ["name"] = "ChameLab",
            ["bundleIdentifier"] = metadata.BundleIdentifier,
            ["developerName"] = DeveloperName,
            ["subtitle"] = Subtitle,
            ["localizedDescription"] = AppDescription.ReplaceLineEndings("\n").Trim(),
            ["iconURL"] = iconUrl,
            ["tintColor"] = AccentTintColor,
            ["category"] = "utilities",
            ["appPermissions"] = permissions,
            ["versions"] = versions,
        };

        return new JsonObject
        {
            ["name"] = isDevFeed ? "ChameLab (Dev)" : "ChameLab",
            ["identifier"] = isDevFeed ? $"{metadata.BundleIdentifier}.source.dev" : $"{metadata.BundleIdentifier}.source",
            ["sourceURL"] = sourceUrl,
            ["subtitle"] = Subtitle,
            ["description"] = isDevFeed
                ? "Official ChameLab development builds for SideStore and AltStore."
                : "Official ChameLab stable releases for SideStore and AltStore.",
            ["iconURL"] = iconUrl,
            ["website"] = baseUrl,
            ["patreonURL"] = PatreonUrl,
            ["tintColor"] = AccentTintColor,
            ["featuredApps"] = new JsonArray(metadata.BundleIdentifier),
            ["apps"] = new JsonArray(app),
            ["news"] = new JsonArray(),
        };
    }

    private static JsonArray BuildVersions(
        IReadOnlyList<JsonElement> releases,
        JsonElement? devRelease,
        string[] changelogLines,
        string minOsVersion,
        bool isDevFeed)
    {
        var candidates = new List<(ParsedReleaseVersion Parsed, bool IsDev, DateTimeOffset PublishedAt, JsonObject Entry)>();

        if (isDevFeed && devRelease is { } dev && TryBuildVersionEntry(dev, changelogLines, minOsVersion, isDevEntry: true) is { } devItem)
            candidates.Add(devItem);

        foreach (var release in releases)
        {
            if (GetBool(release, "draft") || GetBool(release, "prerelease")) continue;
            if (GetString(release, "tag_name") == "dev") continue;
            if (TryBuildVersionEntry(release, changelogLines, minOsVersion, isDevEntry: false) is { } item)
                candidates.Add(item);
        }

        // Order newest first; for identical (Version, BuildVersion) keep the tagged release unless dev has extra commits.
        var ordered = candidates
            .OrderByDescending(c => c.Parsed.SortKey)
            .ThenBy(c => c.IsDev ? 1 : 0)
            .ThenByDescending(c => c.PublishedAt);

        var result = new JsonArray();
        var seen = new HashSet<(string Version, string BuildVersion)>();
        foreach (var item in ordered)
        {
            var key = (item.Parsed.ShortVersion, item.Parsed.BuildVersion);
            if (!seen.Add(key)) continue;
            result.Add((JsonNode)item.Entry);
        }
        return result;
    }

    private static (ParsedReleaseVersion Parsed, bool IsDev, DateTimeOffset PublishedAt, JsonObject Entry)? TryBuildVersionEntry(
        JsonElement release,
        string[] changelogLines,
        string minOsVersion,
        bool isDevEntry)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? ipaAsset = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = GetString(asset, "name");
            if (assetName is not null && IpaAssetPattern().IsMatch(assetName))
            {
                ipaAsset = asset;
                break;
            }
        }
        if (ipaAsset is not { } ipa) return null;

        var tag = GetString(release, "tag_name") ?? "";
        var name = GetString(release, "name") ?? tag;
        var versionText = isDevEntry
            ? (VersionInText().Match(name) is { Success: true } m ? "v" + m.Groups[1].Value : tag)
            : tag;

        if (!TryParseVersion(versionText, out var parsed)) return null;

        var downloadUrl = GetString(ipa, "browser_download_url");
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        var size = ipa.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : 0;
        var dateText = GetString(release, "published_at") ?? GetString(release, "created_at") ?? "";
        var publishedAt = DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTimeOffset.MinValue;

        var body = GetString(release, "body") ?? "";
        var ipaName = GetString(ipa, "name") ?? "";
        var sha256 = ExtractSha256(ipa, ipaName, body);
        var notes = BuildReleaseNotes(changelogLines, parsed.DisplayTag, isDevEntry, parsed.DevCommits > 0);

        var entry = new JsonObject
        {
            ["version"] = parsed.ShortVersion,
            ["buildVersion"] = parsed.BuildVersion,
            ["date"] = publishedAt != DateTimeOffset.MinValue
                ? publishedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : dateText,
            ["localizedDescription"] = notes,
            ["downloadURL"] = downloadUrl,
            ["size"] = size,
            ["minOSVersion"] = minOsVersion,
        };
        if (sha256 is not null) entry["sha256"] = sha256;

        return (parsed, isDevEntry, publishedAt, entry);
    }

    internal static string BuildReleaseNotes(string[] changelogLines, string displayTag, bool isDevEntry, bool hasDevCommits)
    {
        if (isDevEntry && hasDevCommits)
        {
            var header = $"Development build {displayTag} from main.";
            var unreleased = ReleaseAnnouncer.Section(changelogLines, "unreleased");
            return unreleased is { Bullets.Count: > 0 }
                ? header + "\n\n" + FormatBullets(unreleased.Value.Bullets)
                : header;
        }

        var section = ReleaseAnnouncer.Section(changelogLines, displayTag);
        if (section is not { Bullets.Count: > 0 } s)
            return $"ChameLab {displayTag}.";

        var lines = new List<string>();
        if (s.Firmware is { } fw) lines.Add($"Firmware {fw}");
        lines.Add(FormatBullets(s.Bullets));
        return string.Join("\n\n", lines);
    }

    private static string FormatBullets(List<string> bullets) =>
        string.Join('\n', bullets.Select(b => "• " + MarkdownFormatPattern().Replace(b, "").Trim()));

    internal static string? ExtractSha256(JsonElement asset, string assetName, string releaseBody)
    {
        if (GetString(asset, "digest") is { } digest
            && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = digest["sha256:".Length..].Trim();
            if (IsHexSha256(hex)) return hex.ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(assetName) && !string.IsNullOrEmpty(releaseBody))
        {
            var match = Regex.Match(
                releaseBody,
                $@"(?m)^([0-9a-fA-F]{{64}})\s+\*?{Regex.Escape(assetName)}\s*$");
            if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
        }

        return null;
    }

    private static bool IsHexSha256(string value) =>
        value.Length == 64 && value.All(c => Uri.IsHexDigit(c));

    /// <summary>
    /// Mirrors the version and build-code logic in <c>.github/workflows/build_gui.yml</c> so
    /// <c>version</c> matches <c>CFBundleShortVersionString</c> and <c>buildVersion</c> matches <c>CFBundleVersion</c>.
    /// </summary>
    internal static bool TryParseVersion(string text, out ParsedReleaseVersion parsed)
    {
        var match = VersionPattern().Match(text.Trim());
        if (!match.Success)
        {
            parsed = default;
            return false;
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        var commits = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;

        var clampedCommits = Math.Min(commits, 99);
        var code = major * 1_000_000L + minor * 10_000L + patch * 100L + clampedCommits;
        if (major < 2) code += 10_000_000L;
        if (code < 1) code = 1;

        var shortVersion = $"{major}.{minor}.{patch}";
        var displayTag = commits > 0 ? $"v{shortVersion}-dev.{commits}" : $"v{shortVersion}";
        parsed = new ParsedReleaseVersion(
            shortVersion,
            code.ToString(CultureInfo.InvariantCulture),
            displayTag,
            commits,
            (major, minor, patch, commits));
        return true;
    }

    internal static AppleAppMetadata ReadAppleMetadata(string infoPlistPath, string entitlementsPlistPath)
    {
        var infoDict = ReadPlistDict(infoPlistPath);
        var entitlementsDict = ReadPlistDict(entitlementsPlistPath);

        var bundleId = infoDict.TryGetValue("CFBundleIdentifier", out var id)
            ? id
            : throw new InvalidDataException("Info.plist is missing CFBundleIdentifier.");
        var minOs = infoDict.TryGetValue("MinimumOSVersion", out var min) ? min : "13.0";

        var privacy = infoDict
            .Where(kv => kv.Key.StartsWith("NS", StringComparison.Ordinal) && kv.Key.EndsWith("UsageDescription", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var entitlements = entitlementsDict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new AppleAppMetadata(bundleId, minOs, entitlements, privacy);
    }

    private static Dictionary<string, string> ReadPlistDict(string path)
    {
        var doc = XDocument.Load(path);
        var dict = doc.Root?.Element("dict") ?? throw new InvalidDataException($"Invalid plist format: {path}");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;
        foreach (var element in dict.Elements())
        {
            if (element.Name == "key")
            {
                currentKey = element.Value;
            }
            else if (currentKey is not null)
            {
                result[currentKey] = element.Value;
                currentKey = null;
            }
        }
        return result;
    }

    private static string? ParseBaseUrl(string[] extraArgs)
    {
        return extraArgs switch
        {
            [] => null,
            ["--base-url", var url] => url,
            _ => throw new ArgumentException("Usage: generate-altstore INPUT.json OUTPUT_DIR [--base-url URL]"),
        };
    }

    private static string DefaultBaseUrl()
    {
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        if (string.IsNullOrWhiteSpace(repo) || repo.Equals(DefaultRepository, StringComparison.OrdinalIgnoreCase))
            return "https://m0n5ter.github.io/ChameLab/";
        var parts = repo.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            ? $"https://{parts[0].ToLowerInvariant()}.github.io/{parts[1]}/"
            : "https://m0n5ter.github.io/ChameLab/";
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static bool GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    internal readonly record struct ParsedReleaseVersion(
        string ShortVersion,
        string BuildVersion,
        string DisplayTag,
        int DevCommits,
        (int Major, int Minor, int Patch, int Commits) SortKey);

    internal sealed record AppleAppMetadata(
        string BundleIdentifier,
        string MinimumOsVersion,
        IReadOnlyList<string> Entitlements,
        IReadOnlyDictionary<string, string> Privacy);
}
