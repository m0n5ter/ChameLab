using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Text.RegularExpressions;
using ChameLab.Localization;
using Xunit;

namespace ChameLab.Tests;

/// <summary>Every translation covers every English string, with the same format placeholders.</summary>
public sealed partial class LocalizationTests
{
    public static TheoryData<string> Translations => new() { "ru" };

    [Theory]
    [MemberData(nameof(Translations))]
    public void Translation_HasEveryString(string language)
    {
        var english = Read(CultureInfo.InvariantCulture);
        var translated = Read(CultureInfo.GetCultureInfo(language));

        Assert.Empty(english.Keys.Except(translated.Keys).Order());
        Assert.Empty(translated.Keys.Except(english.Keys).Order());
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Translation_KeepsPlaceholders(string language)
    {
        var english = Read(CultureInfo.InvariantCulture);
        var translated = Read(CultureInfo.GetCultureInfo(language));

        var mismatched = english.Keys.Intersect(translated.Keys)
            .Where(key => !Placeholders(english[key]).SetEquals(Placeholders(translated[key])))
            .Order()
            .ToArray();
        Assert.Empty(mismatched);
    }

    /// <summary>The strings stored for exactly this culture, without falling back to English.</summary>
    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();
}
