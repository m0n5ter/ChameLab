using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>One choosable UI language. An empty code follows the operating system.</summary>
public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// The app's UI language. The choice is saved in <see cref="AppSettings"/> and applied once at
/// start-up, before any view reads its strings, so a change takes effect after a restart.
/// Only the UI language changes: number and date formats keep following the system.
/// </summary>
public static class LocalizationService
{
    private const string LanguageSetting = "language";

    /// <summary>Languages with a translation; each one is named in its own language.</summary>
    private static readonly (string Code, string Name)[] Translations =
    [
        ("en", "English"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("fr", "Français"),
        ("it", "Italiano"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("pt", "Português"),
        ("ru", "Русский"),
        ("zh-Hans", "简体中文"),
    ];

    /// <summary>"System default" first, then every translation.</summary>
    public static IReadOnlyList<LanguageOption> Languages => field ??=
        [new LanguageOption("", Strings.Language_System), .. Translations.Select(t => new LanguageOption(t.Code, t.Name))];

    /// <summary>The saved language code, or "" (system default) when none is saved or it is unknown.</summary>
    public static string LoadLanguage()
    {
        var saved = AppSettings.GetString(LanguageSetting);
        return Translations.Any(t => t.Code == saved) ? saved! : "";
    }

    /// <summary>Remembers the language; it applies from the next start.</summary>
    public static void SaveLanguage(string code) => AppSettings.Set(LanguageSetting, code);

    /// <summary>Switches the UI language to the saved choice. Called once at start-up.</summary>
    public static void ApplySaved()
    {
        var code = LoadLanguage();
        if (code.Length == 0) return;

        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
