using System;
using System.Collections.Generic;
using System.Linq;
using ActiproSoftware.UI.Avalonia.Themes;
using ActiproSoftware.UI.Avalonia.Themes.Generation;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ChameLab.Services;

/// <summary>One choosable Actipro accent colour ramp, with a swatch for the picker.</summary>
public sealed record AccentOption(string Name, IBrush Swatch);

/// <summary>
/// App-side appearance preferences (not device settings). The accent is an
/// Actipro colour ramp name, applied by regenerating the ModernTheme resources.
/// The light / dark choice is the application's requested theme variant.
/// </summary>
public static class AppearanceService
{
    private const string AccentSetting = "accent";
    private const string ThemeSetting = "theme";
    public const string DefaultAccent = "Emerald";

    private static IReadOnlyList<AccentOption>? _accents;

    /// <summary>Every Actipro hue, with its mid (500) shade as the swatch colour.</summary>
    public static IReadOnlyList<AccentOption> Accents => _accents ??= BuildAccents();

    private static IReadOnlyList<AccentOption> BuildAccents()
    {
        var palette = new DefaultColorPaletteFactory().Create();
        return Enum.GetValues<Hue>()
            .Select(hue => (hue, ramp: palette.Ramps[hue]))
            .Where(x => x.ramp is not null)
            .Select(x => new AccentOption(x.hue.ToString(),
                new SolidColorBrush(x.ramp!.Shades.Resolve(500).Color.ToRgb())))
            .ToArray();
    }

    /// <summary>The saved accent name, or the default when none is saved or it is unknown.</summary>
    public static string LoadAccent()
    {
        var saved = AppSettings.GetString(AccentSetting);
        return Accents.Any(a => a.Name == saved) ? saved! : DefaultAccent;
    }

    /// <summary>
    /// Applies the saved accent and theme, then remembers every later theme change
    /// (the title bar toggle sets the application's variant directly). Called once at start-up.
    /// </summary>
    public static void ApplySaved()
    {
        Apply(LoadAccent(), persist: false);

        if (Application.Current is not { } app) return;
        if (LoadTheme() is { } theme) app.RequestedThemeVariant = theme;
        app.PropertyChanged += (_, e) =>
        {
            if (e.Property == Application.RequestedThemeVariantProperty)
                SaveTheme(app.RequestedThemeVariant);
        };
    }

    private static ThemeVariant? LoadTheme()
    {
        return AppSettings.GetString(ThemeSetting) switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            "Default" => ThemeVariant.Default,
            _ => null,
        };
    }

    private static void SaveTheme(ThemeVariant? theme)
    {
        AppSettings.Set(ThemeSetting, theme?.Key.ToString() ?? "Default");
    }

    public static void Apply(string name, bool persist = true)
    {
        if (ModernTheme.TryGetCurrent(out var theme) && theme?.Definition is { } definition
            && definition.AccentColorRampName != name)
        {
            definition.AccentColorRampName = name;
            theme.RefreshResources();
        }
        if (!persist) return;
        AppSettings.Set(AccentSetting, name);
    }
}
