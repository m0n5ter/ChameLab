using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>One choosable UI language. An empty code follows the operating system.</summary>
public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// The app's UI language. The choice is saved in <see cref="AppSettings"/>, applied at start-up
/// before any view reads its strings, and applied again right away when it changes:
/// <see cref="LanguageChanged"/> then lets the views rebuild in the new language.
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

    /// <summary>"System default" first, then every translation. Rebuilt when the language changes,
    /// since "System default" is itself translated.</summary>
    public static IReadOnlyList<LanguageOption> Languages => _languages ??=
        [new LanguageOption("", Strings.Language_System), .. Translations.Select(t => new LanguageOption(t.Code, t.Name))];

    private static IReadOnlyList<LanguageOption>? _languages;

    /// <summary>The system's UI language, from before the app chose one.</summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    /// <summary>Raised on the calling thread after <see cref="Apply"/> switched the UI language.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The saved language code, or "" (system default) when none is saved or it is unknown.</summary>
    public static string LoadLanguage()
    {
        var saved = AppSettings.GetString(LanguageSetting);
        return Translations.Any(t => t.Code == saved) ? saved! : "";
    }

    /// <summary>Remembers the language and switches the UI to it.</summary>
    public static void SaveLanguage(string code)
    {
        AppSettings.Set(LanguageSetting, code);
        Apply(code);
    }

    /// <summary>Switches the UI language to the saved choice. Called once at start-up.</summary>
    public static void ApplySaved() => SetCulture(LoadLanguage());

    /// <summary>Switches the UI language now and tells the views, if it is a different language.</summary>
    public static void Apply(string code)
    {
        if (!SetCulture(code)) return;
        _languages = null;
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Sets the UI culture ("" follows the system's): the process-wide default, for other threads
    /// and continuations, and the calling thread's own, which some platforms (the browser) keep
    /// apart from the default. Returns false when it was already this culture.
    /// </summary>
    private static bool SetCulture(string code)
    {
        var culture = code.Length == 0 ? null : CultureInfo.GetCultureInfo(code);
        if (Equals(CultureInfo.DefaultThreadCurrentUICulture, culture) && Equals(CultureInfo.CurrentUICulture, culture ?? SystemCulture))
            return false;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture ?? SystemCulture;
        return true;
    }

    /// <summary>
    /// Makes the calling thread use the chosen UI culture again, should it have kept an older one:
    /// called before the views are rebuilt.
    /// </summary>
    public static void UseOnThisThread() =>
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture ?? SystemCulture;
}
