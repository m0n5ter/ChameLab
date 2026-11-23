using System;
using System.Globalization;
using System.Windows.Input;
using System.Linq;
using System.Collections.Generic;
using Avalonia.Styling;
using Avalonia.Threading;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>App-side settings: appearance, scan feedback, backup and about. Device settings live on the Chameleon page.</summary>
public sealed class SettingsViewModel : PageViewModelBase
{
    private readonly CardLibrary _cards;
    private readonly DictionaryLibrary _dictionaries;

    public SettingsViewModel(CardLibrary cards, DictionaryLibrary dictionaries, AppUpdateViewModel appUpdate)
        : base(() => Strings.Settings_Settings, "settings")
    {
        AppUpdate = appUpdate;
        _cards = cards;
        _dictionaries = dictionaries;

        ExportBackupCommand = ReactiveCommand.CreateFromTask(ExportBackupAsync);
        ImportBackupCommand = ReactiveCommand.CreateFromTask(ImportBackupAsync);
        OpenLinkCommand = ReactiveCommand.CreateFromTask<string>(OpenLinkAsync);
        _theme = Themes.First(t => t.Variant == AppearanceService.LoadTheme());
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Sync = new SyncViewModel(AppServices.Sync, ReportError, Notify);
    }

    /// <summary>The Device sync card.</summary>
    public SyncViewModel Sync { get; }

    /// <summary>
    /// The lists are rebuilt in the new language and the choices move to their new entries. Nothing
    /// is raised: the views are rebuilt over this view model and read both afresh, while the old
    /// pickers must not react (they would write their stale selections back).
    /// </summary>
    private void OnLanguageChanged()
    {
        _language = Languages.First(l => l.Code == _language.Code);
        _theme = Themes.First(t => t.Variant == _theme.Variant);
    }

    public bool CanUseFiles => StorageService.IsAvailable;

    /// <summary>Read afresh when the views are rebuilt for a new language.</summary>
    public IReadOnlyList<ThemeOption> Themes =>
    [
        new(ThemeVariant.Default, Strings.Settings_ThemeSystem),
        new(ThemeVariant.Light, Strings.Settings_ThemeLight),
        new(ThemeVariant.Dark, Strings.Settings_ThemeDark),
    ];

    private ThemeOption _theme;

    /// <summary>App theme; applied and remembered as soon as it changes.</summary>
    public ThemeOption Theme
    {
        get => _theme;
        set
        {
            if (value is null || value.Variant == _theme.Variant) return;
            this.RaiseAndSetIfChanged(ref _theme, value);
            AppearanceService.ApplyTheme(value.Variant);
        }
    }

    public IReadOnlyList<AccentOption> Accents => AppearanceService.Accents;

    /// <summary>App accent color; applied and remembered as soon as it changes.</summary>
    public AccentOption Accent
    {
        get;
        set
        {
            if (value is null || value == field) return;
            this.RaiseAndSetIfChanged(ref field, value);
            AppearanceService.Apply(value.Name);
        }
    } = AppearanceService.Accents.First(a => a.Name == AppearanceService.LoadAccent());

    public IReadOnlyList<LanguageOption> Languages => LocalizationService.Languages;

    private LanguageOption _language = LocalizationService.Languages.First(l => l.Code == LocalizationService.LoadLanguage());

    /// <summary>
    /// UI language; remembered and applied as soon as it changes. Applied once the picker has
    /// finished its selection, since applying rebuilds the views, the picker included.
    /// </summary>
    public LanguageOption Language
    {
        get => _language;
        set
        {
            if (value is null || value.Code == _language.Code) return;
            this.RaiseAndSetIfChanged(ref _language, value);
            var code = value.Code;
            Dispatcher.UIThread.Post(() => LocalizationService.SaveLanguage(code));
        }
    }

    /// <summary>Beep when a card scan or dump finishes; remembered as soon as it changes.</summary>
    public bool ScanSound
    {
        get => ScanFeedback.SoundEnabled;
        set
        {
            if (value == ScanFeedback.SoundEnabled) return;
            ScanFeedback.SoundEnabled = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Vibrate when a card scan or dump finishes (phones only).</summary>
    public bool ScanVibration
    {
        get => ScanFeedback.VibrationEnabled;
        set
        {
            if (value == ScanFeedback.VibrationEnabled) return;
            ScanFeedback.VibrationEnabled = value;
            this.RaisePropertyChanged();
        }
    }

    public bool CanVibrate => ScanFeedback.CanVibrate;

    public ICommand ExportBackupCommand { get; }
    public ICommand ImportBackupCommand { get; }

    private System.Threading.Tasks.Task ExportBackupAsync() => RunAsync(async () =>
    {
        var data = BackupService.Export(_cards, _dictionaries);
        var name = await StorageService.SaveAsync(Strings.Settings_ExportBackup, "chamelab-backup.json", data, "json");
        if (name is not null) Notify(string.Format(Strings.Settings_BackupSaved, name));
    });

    private System.Threading.Tasks.Task ImportBackupAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync(Strings.Settings_ImportBackup, "json");
        if (picked is not { } file) return;
        var (c, d) = BackupService.Import(file.Data, _cards, _dictionaries);
        Notify(string.Format(Strings.Settings_ImportedBackup, Loc.Plural(Strings.Plural_Cards, c), Loc.Plural(Strings.Plural_Dictionaries, d)));
    });

    /// <summary>Application version, from the assembly's informational version.</summary>
    public string AppVersion => AppUpdateService.CurrentVersion;

    /// <summary>The app's own update check, shown in the About card.</summary>
    public AppUpdateViewModel AppUpdate { get; }

    public static string TrademarkNotice => Strings.Settings_TrademarkNotice;

    private static readonly SocialLink Discord = new("Discord", "https://discord.gg/UBSReGd4Gc", "discord", 1);

    private static readonly SocialLink[] RussianLinks =
    [
        new("Telegram", "https://t.me/chamelab", "telegram", 2),
        new("MAX", "https://max.ru/channel_ChameLab", "max", 0),
    ];

    public const string WebsiteUrl = "https://m0n5ter.github.io/ChameLab/";

    // Boosty takes Russian cards, Patreon the rest of the world
    private static readonly SocialLink Boosty = new("Boosty", "https://boosty.to/chamelab", "heart", 5, Accent: true);
    private static readonly SocialLink Patreon = new("Patreon", "https://www.patreon.com/ChameLab", "heart", 5, Accent: true);

    /// <summary>
    /// Store copies leave the support link out: Google Play and the App Store want payments to the
    /// developer from inside the app to go through their own billing.
    /// </summary>
    private static bool ShowsSupportLink => !AppUpdateService.UpdatedByStore && !OperatingSystem.IsIOS();

    /// <summary>
    /// Links for the About card: the website first, then the support page (Boosty in the Russian UI,
    /// Patreon in the others), then the Russian-speaking channels in the Russian UI and Discord in
    /// every other language. Read afresh when the views are rebuilt for a
    /// new language (the website's name is localized).
    /// </summary>
    public IReadOnlyList<SocialLink> SocialLinks =>
    [
        new(Strings.Settings_Website, WebsiteUrl, "web", 4),
        .. SupportLink is { } support ? [support] : Array.Empty<SocialLink>(),
        .. Russian ? RussianLinks : [Discord],
    ];

    private static bool Russian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    /// <summary>
    /// The support page for this language (Boosty in Russian, Patreon in the others), or null in
    /// store copies. Shown in the About card and at the foot of the navigation pane.
    /// </summary>
    public SocialLink? SupportLink => ShowsSupportLink ? (Russian ? Boosty : Patreon) : null;

    /// <summary>The navigation pane's support button: "Support us on Patreon".</summary>
    public string? SupportLabel => SupportLink is { } link ? string.Format(Strings.Main_SupportUs, link.Name) : null;

    /// <summary>Opens a community link in the browser.</summary>
    public ICommand OpenLinkCommand { get; }

    private static async System.Threading.Tasks.Task OpenLinkAsync(string url)
    {
        if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}

/// <summary>
/// A theme choice as the Settings picker lists it. Equal by variant alone, so the choice
/// still matches its entry once the list is rebuilt in another language.
/// </summary>
public sealed record ThemeOption(ThemeVariant Variant, string Name)
{
    public bool Equals(ThemeOption? other) => other is not null && other.Variant == Variant;

    public override int GetHashCode() => Variant.GetHashCode();
}

/// <summary>
/// A link shown in the About card, with its <c>Icon.*</c> mark. Short of room, lower
/// <paramref name="Priority"/> drops its label first (see <see cref="Controls.ActionRow"/>).
/// <paramref name="Accent"/> draws the icon in the accent color.
/// </summary>
public sealed record SocialLink(string Name, string Url, string IconKey, int Priority, bool Accent = false);
