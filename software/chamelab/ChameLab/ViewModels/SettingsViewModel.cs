using System.Windows.Input;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class SettingsViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly CardLibrary _cards;
    private readonly DictionaryLibrary _dictionaries;
    private AnimationMode _animationMode = AnimationMode.Full;
    private int _sleepTimeout = 60;
    private ButtonPressFunction _buttonAShort = ButtonPressFunction.None;
    private ButtonPressFunction _buttonBShort = ButtonPressFunction.None;
    private ButtonPressFunction _buttonALong = ButtonPressFunction.None;
    private ButtonPressFunction _buttonBLong = ButtonPressFunction.None;
    private int _longPressThreshold = 500;
    private bool _supportsLongPressThreshold;
    private bool _supportsBle;
    private bool _blePairingEnabled;
    private string _blePin = string.Empty;
    private bool _confirmingFactoryReset;
    private bool _confirmingClearBonds;
    private bool _isConnected;
    private Values _saved;
    private AccentOption _accent =
        AppearanceService.Accents.First(a => a.Name == AppearanceService.LoadAccent());

    public SettingsViewModel(DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries,
        AppUpdateViewModel appUpdate)
        : base("Settings", "settings")
    {
        AppUpdate = appUpdate;
        _device = device;
        _cards = cards;
        _dictionaries = dictionaries;
        _isConnected = device.State == ConnectionState.Connected;

        // Device settings need a connection; appearance and backup work offline.
        var connected = this.WhenAnyValue(x => x.IsConnected);
        var canSave = this.WhenAnyValue(x => x.IsConnected, x => x.IsDirty, (c, d) => c && d);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canSave);
        DiscardCommand = ReactiveCommand.Create(Discard);
        ResetCommand = ReactiveCommand.CreateFromTask(ResetAsync, connected);
        ClearBondsCommand = ReactiveCommand.CreateFromTask(ClearBondsAsync, connected);
        FactoryResetCommand = ReactiveCommand.CreateFromTask(FactoryResetAsync, connected);
        ExportBackupCommand = ReactiveCommand.CreateFromTask(ExportBackupAsync);
        ImportBackupCommand = ReactiveCommand.CreateFromTask(ImportBackupAsync);

        _saved = Current;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AnimationMode) or nameof(SleepTimeout) or nameof(ButtonAShort)
                or nameof(ButtonBShort) or nameof(ButtonALong) or nameof(ButtonBLong)
                or nameof(LongPressThreshold) or nameof(BlePairingEnabled) or nameof(BlePin))
                RaiseDirty();
        };

        _device.StateChanged += s => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = s == ConnectionState.Connected;
            if (!IsConnected)
            {
                SupportsBle = false;
                SupportsLongPressThreshold = false;
                ConfirmingFactoryReset = false;
                ConfirmingClearBonds = false;
                // Edits meant for that device are dropped; the next connection loads fresh values.
                BlePin = string.Empty;
                _saved = Current;
                RaiseDirty();
            }
        });
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public bool CanUseFiles => StorageService.IsAvailable;

    public IReadOnlyList<AccentOption> Accents => AppearanceService.Accents;

    /// <summary>App accent color; applied and remembered as soon as it changes.</summary>
    public AccentOption Accent
    {
        get => _accent;
        set
        {
            if (value is null || value == _accent) return;
            this.RaiseAndSetIfChanged(ref _accent, value);
            AppearanceService.Apply(value.Name);
        }
    }

    public ICommand ExportBackupCommand { get; }
    public ICommand ImportBackupCommand { get; }

    private System.Threading.Tasks.Task ExportBackupAsync() => RunAsync(async () =>
    {
        var data = BackupService.Export(_cards, _dictionaries);
        var name = await StorageService.SaveAsync("Export backup", "chamelab-backup.json", data, "json");
        if (name is not null) SetStatus($"Backup saved: {name}.");
    });

    private System.Threading.Tasks.Task ImportBackupAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync("Import backup", "json");
        if (picked is not { } file) return;
        var (c, d) = BackupService.Import(file.Data, _cards, _dictionaries);
        SetStatus($"Imported {c} card(s) and {d} dictionary(ies).");
    });

    public IReadOnlyList<AnimationMode> AnimationModes { get; } = new[]
    {
        AnimationMode.Full, AnimationMode.Minimal, AnimationMode.Symmetric, AnimationMode.None,
    };

    public IReadOnlyList<ButtonPressFunction> ButtonFunctions { get; } = new[]
    {
        ButtonPressFunction.None, ButtonPressFunction.NextSlot, ButtonPressFunction.PrevSlot,
        ButtonPressFunction.Clone, ButtonPressFunction.Battery, ButtonPressFunction.FieldGen,
    };

    public AnimationMode AnimationMode
    {
        get => _animationMode;
        set => this.RaiseAndSetIfChanged(ref _animationMode, value);
    }

    public int SleepTimeout
    {
        get => _sleepTimeout;
        set => this.RaiseAndSetIfChanged(ref _sleepTimeout, value);
    }

    public ButtonPressFunction ButtonAShort
    {
        get => _buttonAShort;
        set => this.RaiseAndSetIfChanged(ref _buttonAShort, value);
    }

    public ButtonPressFunction ButtonBShort
    {
        get => _buttonBShort;
        set => this.RaiseAndSetIfChanged(ref _buttonBShort, value);
    }

    public ButtonPressFunction ButtonALong
    {
        get => _buttonALong;
        set => this.RaiseAndSetIfChanged(ref _buttonALong, value);
    }

    public ButtonPressFunction ButtonBLong
    {
        get => _buttonBLong;
        set => this.RaiseAndSetIfChanged(ref _buttonBLong, value);
    }

    public int LongPressThreshold
    {
        get => _longPressThreshold;
        set => this.RaiseAndSetIfChanged(ref _longPressThreshold, value);
    }

    public bool SupportsLongPressThreshold
    {
        get => _supportsLongPressThreshold;
        private set => this.RaiseAndSetIfChanged(ref _supportsLongPressThreshold, value);
    }

    public bool SupportsBle
    {
        get => _supportsBle;
        private set => this.RaiseAndSetIfChanged(ref _supportsBle, value);
    }

    public bool BlePairingEnabled
    {
        get => _blePairingEnabled;
        set => this.RaiseAndSetIfChanged(ref _blePairingEnabled, value);
    }

    public string BlePin
    {
        get => _blePin;
        set => this.RaiseAndSetIfChanged(ref _blePin, value);
    }

    public bool ConfirmingFactoryReset
    {
        get => _confirmingFactoryReset;
        set => this.RaiseAndSetIfChanged(ref _confirmingFactoryReset, value);
    }

    public bool ConfirmingClearBonds
    {
        get => _confirmingClearBonds;
        set => this.RaiseAndSetIfChanged(ref _confirmingClearBonds, value);
    }

    /// <summary>A device setting differs from what the device holds (drives the save bar).</summary>
    public bool IsDirty => Current != _saved || !string.IsNullOrEmpty(BlePin);

    private void RaiseDirty() => this.RaisePropertyChanged(nameof(IsDirty));

    // The editable device settings, as loaded or last saved; a typed PIN counts as a change on its own.
    private readonly record struct Values(
        AnimationMode AnimationMode, int SleepTimeout,
        ButtonPressFunction ButtonAShort, ButtonPressFunction ButtonBShort,
        ButtonPressFunction ButtonALong, ButtonPressFunction ButtonBLong,
        int LongPressThreshold, bool BlePairingEnabled);

    private Values Current => new(AnimationMode, SleepTimeout, ButtonAShort, ButtonBShort,
        ButtonALong, ButtonBLong, LongPressThreshold, BlePairingEnabled);

    public ICommand SaveCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ClearBondsCommand { get; }
    public ICommand FactoryResetCommand { get; }

    /// <summary>Application version, from the assembly's informational version.</summary>
    public string AppVersion => AppUpdateService.CurrentVersion;

    /// <summary>The app's own update check, shown in the About card.</summary>
    public AppUpdateViewModel AppUpdate { get; }

    public const string TrademarkNotice =
        "MIFARE®, MIFARE® Classic and MIFARE® Ultralight are registered trademarks of NXP B.V. " +
        "HID™ and HID ProxCard™ are trademarks of HID Global. EM4100 and related product names are " +
        "trademarks of EM Microelectronic-Marin SA. This app is not affiliated with any of them.";

    /// <summary>Unsaved edits survive leaving and coming back; otherwise the page shows what the device holds.</summary>
    public override Task OnActivatedAsync() => IsDirty ? Task.CompletedTask : LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;

        var anim = await client.GetAnimationModeAsync();
        var sleep = await client.GetSleepTimeoutAsync();
        var a = await client.GetButtonConfigAsync(ButtonType.A, longPress: false);
        var b = await client.GetButtonConfigAsync(ButtonType.B, longPress: false);
        var aLong = await client.GetButtonConfigAsync(ButtonType.A, longPress: true);
        var bLong = await client.GetButtonConfigAsync(ButtonType.B, longPress: true);

        var threshold = 500;
        var hasThreshold = false;
        try { threshold = await client.GetLongPressThresholdAsync(); hasThreshold = true; }
        catch { /* older firmware */ }

        var blePairing = false;
        var hasBle = false;
        try { blePairing = await client.GetBlePairingEnableAsync(); hasBle = true; }
        catch { /* lite / older firmware has no BLE */ }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AnimationMode = anim;
            SleepTimeout = sleep;
            ButtonAShort = a;
            ButtonBShort = b;
            ButtonALong = aLong;
            ButtonBLong = bLong;
            LongPressThreshold = threshold;
            SupportsLongPressThreshold = hasThreshold;
            BlePairingEnabled = blePairing;
            SupportsBle = hasBle;
            BlePin = string.Empty;
            _saved = Current;
            RaiseDirty();
        });
        SetStatus("Settings loaded.");
    });

    // Sends the values and persists them in one go, so they survive a reboot.
    private Task SaveAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var values = Current;
        var pin = BlePin ?? string.Empty;
        if (pin.Length > 0 && (pin.Length != 6 || !pin.All(char.IsDigit)))
            throw new System.FormatException("The BLE PIN must be exactly 6 digits.");

        await client.SetAnimationModeAsync(values.AnimationMode);
        await client.SetSleepTimeoutAsync(values.SleepTimeout);
        await client.SetButtonConfigAsync(ButtonType.A, false, values.ButtonAShort);
        await client.SetButtonConfigAsync(ButtonType.B, false, values.ButtonBShort);
        await client.SetButtonConfigAsync(ButtonType.A, true, values.ButtonALong);
        await client.SetButtonConfigAsync(ButtonType.B, true, values.ButtonBLong);
        if (SupportsLongPressThreshold)
            try { await client.SetLongPressThresholdAsync(values.LongPressThreshold); } catch { }
        if (SupportsBle)
        {
            await client.SetBlePairingEnableAsync(values.BlePairingEnabled);
            if (pin.Length > 0) await client.SetBlePairingKeyAsync(pin);
        }
        await client.SaveSettingsAsync();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _saved = values;
            if (BlePin == pin) BlePin = string.Empty;
            RaiseDirty();
        });
    }, "Settings saved.");

    /// <summary>Puts every field back to what the device holds.</summary>
    private void Discard()
    {
        AnimationMode = _saved.AnimationMode;
        SleepTimeout = _saved.SleepTimeout;
        ButtonAShort = _saved.ButtonAShort;
        ButtonBShort = _saved.ButtonBShort;
        ButtonALong = _saved.ButtonALong;
        ButtonBLong = _saved.ButtonBLong;
        LongPressThreshold = _saved.LongPressThreshold;
        BlePairingEnabled = _saved.BlePairingEnabled;
        BlePin = string.Empty;
    }

    private Task ResetAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.ResetSettingsAsync();
        await LoadAsync();
    }, "Settings reset to defaults.");

    private Task ClearBondsAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.DeleteAllBleBondsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ConfirmingClearBonds = false);
    }, "Bonded BLE devices cleared.");

    private Task FactoryResetAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.WipeFdsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ConfirmingFactoryReset = false);
        // The device drops the link after a factory reset.
        await _device.DisconnectAsync();
    }, "Factory reset done. The device has been disconnected.");
}
