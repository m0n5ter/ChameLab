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
        RefreshCommand = ReactiveCommand.CreateFromTask(OnActivatedAsync, connected);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync, connected);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, connected);
        ResetCommand = ReactiveCommand.CreateFromTask(ResetAsync, connected);
        ApplyBleCommand = ReactiveCommand.CreateFromTask(ApplyBleAsync, connected);
        ClearBondsCommand = ReactiveCommand.CreateFromTask(ClearBondsAsync, connected);
        FactoryResetCommand = ReactiveCommand.CreateFromTask(FactoryResetAsync, connected);
        ExportBackupCommand = ReactiveCommand.CreateFromTask(ExportBackupAsync);
        ImportBackupCommand = ReactiveCommand.CreateFromTask(ImportBackupAsync);

        _device.StateChanged += s => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = s == ConnectionState.Connected;
            if (!IsConnected)
            {
                SupportsBle = false;
                SupportsLongPressThreshold = false;
                ConfirmingFactoryReset = false;
                ConfirmingClearBonds = false;
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

    public ICommand RefreshCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ApplyBleCommand { get; }
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

    public override Task OnActivatedAsync() => RunAsync(async () =>
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
        });
        SetStatus("Settings loaded.");
    });

    private Task ApplyAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetAnimationModeAsync(AnimationMode);
        await client.SetSleepTimeoutAsync(SleepTimeout);
        await client.SetButtonConfigAsync(ButtonType.A, false, ButtonAShort);
        await client.SetButtonConfigAsync(ButtonType.B, false, ButtonBShort);
        await client.SetButtonConfigAsync(ButtonType.A, true, ButtonALong);
        await client.SetButtonConfigAsync(ButtonType.B, true, ButtonBLong);
        if (SupportsLongPressThreshold)
            try { await client.SetLongPressThresholdAsync(LongPressThreshold); } catch { }
    }, "Settings applied (remember to save to flash).");

    private Task SaveAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SaveSettingsAsync();
    }, "Settings saved to flash.");

    private Task ResetAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.ResetSettingsAsync();
        await OnActivatedAsync();
    }, "Settings reset to defaults.");

    private Task ApplyBleAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetBlePairingEnableAsync(BlePairingEnabled);
        if (!string.IsNullOrEmpty(BlePin))
        {
            if (BlePin.Length != 6 || !BlePin.All(char.IsDigit))
                throw new System.FormatException("The BLE PIN must be exactly 6 digits.");
            await client.SetBlePairingKeyAsync(BlePin);
        }
        await client.SaveSettingsAsync();
    }, "BLE settings saved to flash.");

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
