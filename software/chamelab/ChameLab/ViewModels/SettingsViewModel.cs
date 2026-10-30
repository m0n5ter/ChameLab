using System.Windows.Input;
using System.Linq;
using System.Collections.Generic;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>App-side settings: appearance, scan feedback, backup and about. Device settings live on the Chameleon page.</summary>
public sealed class SettingsViewModel : PageViewModelBase
{
    private readonly CardLibrary _cards;
    private readonly DictionaryLibrary _dictionaries;

    public SettingsViewModel(CardLibrary cards, DictionaryLibrary dictionaries, AppUpdateViewModel appUpdate)
        : base("Settings", "settings")
    {
        AppUpdate = appUpdate;
        _cards = cards;
        _dictionaries = dictionaries;

        ExportBackupCommand = ReactiveCommand.CreateFromTask(ExportBackupAsync);
        ImportBackupCommand = ReactiveCommand.CreateFromTask(ImportBackupAsync);
    }

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
        var name = await StorageService.SaveAsync("Export backup", "chamelab-backup.json", data, "json");
        if (name is not null) Notify($"Backup saved: {name}.");
    });

    private System.Threading.Tasks.Task ImportBackupAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync("Import backup", "json");
        if (picked is not { } file) return;
        var (c, d) = BackupService.Import(file.Data, _cards, _dictionaries);
        Notify($"Imported {c} card(s) and {d} dictionary(ies).");
    });

    /// <summary>Application version, from the assembly's informational version.</summary>
    public string AppVersion => AppUpdateService.CurrentVersion;

    /// <summary>The app's own update check, shown in the About card.</summary>
    public AppUpdateViewModel AppUpdate { get; }

    public const string TrademarkNotice =
        "MIFARE®, MIFARE® Classic and MIFARE® Ultralight are registered trademarks of NXP B.V. " +
        "HID™ and HID ProxCard™ are trademarks of HID Global. EM4100 and related product names are " +
        "trademarks of EM Microelectronic-Marin SA. This app is not affiliated with any of them.";
}
