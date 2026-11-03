using System;
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

    [Theory]
    [InlineData("", 2)]
    [InlineData("ru", 3)]
    public void PluralStrings_HaveEveryForm(string language, int forms)
    {
        var strings = Read(language.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language));

        var wrong = strings.Where(e => e.Key.StartsWith("Plural_", StringComparison.Ordinal) && e.Value.Split('|').Length != forms)
            .Select(e => e.Key).Order().ToArray();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData(1, "{0} карта")]
    [InlineData(3, "{0} карты")]
    [InlineData(5, "{0} карт")]
    [InlineData(11, "{0} карт")]
    [InlineData(21, "{0} карта")]
    [InlineData(112, "{0} карт")]
    public void Plural_PicksRussianForm(int count, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
        try
        {
            Assert.Equal(string.Format(expected, count), Loc.Plural("{0} карта|{0} карты|{0} карт", count));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
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
