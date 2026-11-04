using System.Windows.Input;
using System.Linq;
using System.Collections.Generic;
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
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// The list is rebuilt in the new language and the choice moves to its new entry. Nothing is
    /// raised: the views are rebuilt over this view model and read both afresh, while the old
    /// picker must not react (it would write its stale selection back).
    /// </summary>
    private void OnLanguageChanged() => _language = Languages.First(l => l.Code == _language.Code);

    public bool CanUseFiles => StorageService.IsAvailable;

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
}
