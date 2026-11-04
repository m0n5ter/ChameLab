using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using ChameLab.Services;

namespace ChameLab.Browser;

/// <summary>
/// Chinese, Japanese and Korean glyphs for the web app. The browser offers Avalonia no system
/// fonts, so Inter alone would draw these languages as boxes. The bundled Noto Sans fonts hold
/// only the characters of the translations (tools/fonts/subset_cjk.py) and are fallbacks after
/// Inter; the UI language's own font comes first, so shared ideographs get its glyph shapes.
/// </summary>
internal static class CjkFonts
{
    private static readonly Uri Key = new("fonts:ChameLabCjk");

    public static AppBuilder WithCjkFonts(this AppBuilder builder)
    {
        string[] scripts = Language() switch
        {
            "ja" => ["JP", "SC", "KR"],
            "ko" => ["KR", "SC", "JP"],
            _ => ["SC", "JP", "KR"],
        };
        return builder
            .ConfigureFonts(fonts => fonts.AddFontCollection(
                new EmbeddedFontCollection(Key, new Uri("avares://ChameLab.Browser/Assets/Fonts"))))
            .With(new FontManagerOptions
            {
                FontFallbacks = scripts
                    .Select(s => new FontFallback { FontFamily = new FontFamily($"{Key}#Noto Sans {s}") })
                    .ToArray(),
            });
    }

    /// <summary>The UI language that <see cref="LocalizationService.ApplySaved"/> will pick.</summary>
    private static string Language()
    {
        var saved = LocalizationService.LoadLanguage();
        return (saved.Length > 0 ? CultureInfo.GetCultureInfo(saved) : CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
    }
}
