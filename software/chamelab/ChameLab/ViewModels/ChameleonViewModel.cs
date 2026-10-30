using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>The connected device: status, active slot, firmware and the settings it keeps.</summary>
public sealed class ChameleonViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly CardLibrary _cards;
    private DeviceInfo? _info;
    private BatteryInfo? _battery;
    private bool _readerMode;
    private bool _suppressModeWrite;
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
    private Values _saved;

    public ChameleonViewModel(DeviceService device, CardLibrary cards, FirmwareUpdateViewModel firmware) : base("Chameleon", "dashboard")
    {
        _device = device;
        _cards = cards;
        _cards.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var slot in Slots) slot.OnLibraryChanged();
        });
        Firmware = firmware;
        RefreshCommand = ReactiveCommand.CreateFromTask(OnActivatedAsync);
        ToggleModeCommand = ReactiveCommand.CreateFromTask(ToggleModeAsync);
        PrevSlotCommand = ReactiveCommand.CreateFromTask(() => ChangeSlotAsync(-1));
        NextSlotCommand = ReactiveCommand.CreateFromTask(() => ChangeSlotAsync(+1));
        SelectSlotCommand = ReactiveCommand.CreateFromTask<SlotItemViewModel>(s => ActivateSlotAsync(s.Index));
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.IsDirty));
        DiscardCommand = ReactiveCommand.Create(Discard);
        ResetCommand = ReactiveCommand.CreateFromTask(ResetAsync);
        ClearBondsCommand = ReactiveCommand.CreateFromTask(ClearBondsAsync);
        FactoryResetCommand = ReactiveCommand.CreateFromTask(FactoryResetAsync);

        // Writing the mode on user toggles only — not when we load the current value.
        this.WhenAnyValue(x => x.ReaderMode)
            .Skip(1)
            .Subscribe(ignored => { if (!_suppressModeWrite) { _ = ToggleModeAsync(); } });

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
            if (s == ConnectionState.Connected) return;
            SupportsBle = false;
            SupportsLongPressThreshold = false;
            // Edits meant for that device are dropped; the next connection loads fresh values.
            BlePin = string.Empty;
            _saved = Current;
            RaiseDirty();
        });
    }

    public FirmwareUpdateViewModel Firmware { get; }

    private int _activeSlot = 1;

    /// <summary>The eight slots as last read from the device (empty until then).</summary>
    public ObservableCollection<SlotItemViewModel> Slots { get; } = new();

    public int ActiveSlot
    {
        get => _activeSlot;
        private set
        {
            this.RaiseAndSetIfChanged(ref _activeSlot, value);
            foreach (var slot in Slots) slot.IsActive = slot.Index == value;
            this.RaisePropertyChanged(nameof(ActiveItem));
            this.RaisePropertyChanged(nameof(ActiveHeader));
        }
    }

    public SlotItemViewModel? ActiveItem => Slots.FirstOrDefault(s => s.Index == ActiveSlot);

    public string ActiveHeader => ActiveItem?.Header ?? "–";

    public ICommand PrevSlotCommand { get; }
    public ICommand NextSlotCommand { get; }
    public ICommand SelectSlotCommand { get; }

    // Like the device buttons: step to the next slot holding a card, wrapping around.
    private Task ChangeSlotAsync(int delta)
    {
        for (var step = 1; step < 8; step++)
        {
            var target = ((ActiveSlot - 1 + delta * step + 8 * 8) % 8) + 1;
            if (Slots.FirstOrDefault(s => s.Index == target) is { IsEmpty: false })
                return ActivateSlotAsync(target);
        }
        return Task.CompletedTask;
    }

    private Task ActivateSlotAsync(int target) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || target == ActiveSlot) return;
        await client.SetActiveSlotAsync(target);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ActiveSlot = target);
        Notify($"Slot {target} is now active.");
    });

    public DeviceInfo? Info
    {
        get => _info;
        private set => this.RaiseAndSetIfChanged(ref _info, value);
    }

    public BatteryInfo? Battery
    {
        get => _battery;
        private set => this.RaiseAndSetIfChanged(ref _battery, value);
    }

    public bool ReaderMode
    {
        get => _readerMode;
        set => this.RaiseAndSetIfChanged(ref _readerMode, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleModeCommand { get; }

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

    /// <summary>Reads the device afresh; unsaved setting edits survive leaving and coming back.</summary>
    public override Task OnActivatedAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;

        var info = await client.GetDeviceInfoAsync();
        BatteryInfo? bat = null;
        try { bat = await client.GetBatteryInfoAsync(); } catch { /* lite has no gauge */ }

        var active = 1;
        SlotInfo[] slots = [];
        try
        {
            slots = await client.WithSlotLockAsync(async () =>
            {
                var all = (await client.GetAllSlotsAsync()).ToArray();
                await client.ReadLfCyclesAsync(all);
                return all;
            });
            active = slots.FirstOrDefault(s => s.IsActive)?.Index ?? 1;
        }
        catch { /* older firmware */ }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Info = info;
            Battery = bat;
            Slots.Clear();
            foreach (var slot in slots) Slots.Add(new SlotItemViewModel(slot, _cards));
            _activeSlot = 0;
            ActiveSlot = active;
            _suppressModeWrite = true;
            ReaderMode = info.Mode == DeviceMode.Reader;
            _suppressModeWrite = false;
        });

        if (!IsDirty) await LoadSettingsAsync(client);
    });

    private Task ToggleModeAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var target = ReaderMode ? DeviceMode.Reader : DeviceMode.Tag;
        await client.SetDeviceModeAsync(target);
        Notify($"Switched to {target} mode.");
    });

    private async Task LoadSettingsAsync(ChameleonClient client)
    {
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
    }

    // Sends the values and persists them in one go, so they survive a reboot.
    private Task SaveAsync() => RunSaveAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var values = Current;
        var pin = BlePin ?? string.Empty;
        if (pin.Length > 0 && (pin.Length != 6 || !pin.All(char.IsDigit)))
            throw new FormatException("The BLE PIN must be exactly 6 digits.");

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
    });

    /// <summary>Puts every field back to what the device holds.</summary>
    private void Discard()
    {
        ClearSaveError();
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
        await LoadSettingsAsync(client);
    }, "Settings reset to defaults.");

    private Task ClearBondsAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.DeleteAllBleBondsAsync();
    }, "Bonded BLE devices cleared.");

    private Task FactoryResetAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.WipeFdsAsync();
        // The device drops the link after a factory reset.
        await _device.DisconnectAsync();
    }, "Factory reset done. The device has been disconnected.");
}
