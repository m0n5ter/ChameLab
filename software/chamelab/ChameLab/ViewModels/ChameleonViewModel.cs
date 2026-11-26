using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>Destructive device action awaiting confirmation in the shell modal dialog.</summary>
public enum ChameleonDangerAction
{
    None,
    ClearBonds,
    ResetSettings,
    FactoryReset,
}

/// <summary>The connected device: status, active slot, firmware and the settings it keeps.</summary>
public sealed class ChameleonViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly CardLibrary _cards;
    private bool _suppressModeWrite;
    private Values _saved;
    private CancellationTokenSource? _batteryRetryCts;

    public ChameleonViewModel(DeviceService device, CardLibrary cards, FirmwareUpdateViewModel firmware) : base(() => Strings.Nav_Chameleon, "dashboard")
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
        AskResetCommand = ReactiveCommand.Create(() => { PendingDanger = ChameleonDangerAction.ResetSettings; });
        AskClearBondsCommand = ReactiveCommand.Create(() => { PendingDanger = ChameleonDangerAction.ClearBonds; });
        AskFactoryResetCommand = ReactiveCommand.Create(() => { PendingDanger = ChameleonDangerAction.FactoryReset; });
        CancelDangerCommand = ReactiveCommand.Create(() => { PendingDanger = ChameleonDangerAction.None; });
        ConfirmDangerCommand = ReactiveCommand.CreateFromTask(ConfirmDangerAsync);

        // Writing the mode on user toggles only, not when we load the current value.
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
            CancelBatteryRetry();
            PendingDanger = ChameleonDangerAction.None;
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

    // Like the device buttons: step to the next active slot, wrapping around.
    private Task ChangeSlotAsync(int delta)
    {
        for (var step = 1; step < 8; step++)
        {
            var target = ((ActiveSlot - 1 + delta * step + 8 * 8) % 8) + 1;
            if (Slots.FirstOrDefault(s => s.Index == target) is { IsEnabled: true })
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
        Notify(string.Format(Strings.Chameleon_SlotActive, target));
    });

    public DeviceInfo? Info
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public BatteryInfo? Battery
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool ReaderMode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
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
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = AnimationMode.Full;

    public int SleepTimeout
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 60;

    public ButtonPressFunction ButtonAShort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = ButtonPressFunction.None;

    public ButtonPressFunction ButtonBShort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = ButtonPressFunction.None;

    public ButtonPressFunction ButtonALong
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = ButtonPressFunction.None;

    public ButtonPressFunction ButtonBLong
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = ButtonPressFunction.None;

    public int LongPressThreshold
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 500;

    public bool SupportsLongPressThreshold
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool SupportsBle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool BlePairingEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string BlePin
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

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
    public ICommand AskResetCommand { get; }
    public ICommand AskClearBondsCommand { get; }
    public ICommand AskFactoryResetCommand { get; }
    public ICommand CancelDangerCommand { get; }
    public ICommand ConfirmDangerCommand { get; }

    /// <summary>Pending destructive action awaiting user confirmation; <see cref="ChameleonDangerAction.None"/> when closed.</summary>
    public ChameleonDangerAction PendingDanger
    {
        get;
        private set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsConfirmingDanger));
            this.RaisePropertyChanged(nameof(DangerPrompt));
            this.RaisePropertyChanged(nameof(DangerDetail));
            this.RaisePropertyChanged(nameof(DangerConfirmText));
            this.RaisePropertyChanged(nameof(DangerConfirmIconKey));
        }
    }

    public bool IsConfirmingDanger => PendingDanger != ChameleonDangerAction.None;

    public string DangerPrompt => PendingDanger switch
    {
        ChameleonDangerAction.ClearBonds => Strings.Chameleon_ClearAllBondedBLEDevices,
        ChameleonDangerAction.ResetSettings => Strings.Chameleon_ResetAllSettings,
        ChameleonDangerAction.FactoryReset => Strings.Chameleon_FactoryResetTheChameleon,
        _ => string.Empty,
    };

    public string DangerDetail => PendingDanger switch
    {
        ChameleonDangerAction.ClearBonds => Strings.Chameleon_PairedPhonesAndTabletsWillHave,
        ChameleonDangerAction.ResetSettings => Strings.Chameleon_CurrentDeviceSettingsWillBeReplaced,
        ChameleonDangerAction.FactoryReset => Strings.Chameleon_AllSlotsNicknamesEmulatorDataAnd,
        _ => string.Empty,
    };

    public string DangerConfirmText => PendingDanger switch
    {
        ChameleonDangerAction.ClearBonds => Strings.Chameleon_ClearBonds,
        ChameleonDangerAction.ResetSettings => Strings.Chameleon_ResetSettings,
        ChameleonDangerAction.FactoryReset => Strings.Chameleon_EraseEverything,
        _ => string.Empty,
    };

    public string DangerConfirmIconKey => PendingDanger switch
    {
        ChameleonDangerAction.ClearBonds => "ble",
        ChameleonDangerAction.ResetSettings => "reset",
        _ => "delete",
    };

    /// <summary>Reads the device afresh; unsaved setting edits survive leaving and coming back.</summary>
    public override Task OnActivatedAsync() => RunAsync(async () =>
    {
        CancelBatteryRetry();
        var client = _device.Client;
        if (client is null) return;

        var info = await client.GetDeviceInfoAsync();

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

        BatteryInfo? bat = null;
        try { bat = await client.GetBatteryInfoAsync(); } catch { /* lite has no gauge */ }

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

        // Older firmware only measures battery on a 5 s timer after wake-up, so connecting
        // quickly over BLE can return 0 mV before the first tick; re-read quietly in the background.
        if (bat is { Millivolts: 0 })
            ScheduleBatteryRetry(client);
    });

    public override Task OnDeactivatedAsync()
    {
        CancelBatteryRetry();
        PendingDanger = ChameleonDangerAction.None;
        return base.OnDeactivatedAsync();
    }

    private void CancelBatteryRetry()
    {
        var previous = Interlocked.Exchange(ref _batteryRetryCts, null);
        if (previous is null) return;
        try { previous.Cancel(); } catch { /* ignore */ }
        previous.Dispose();
    }

    private void ScheduleBatteryRetry(ChameleonClient client)
    {
        CancelBatteryRetry();
        var cts = new CancellationTokenSource();
        _batteryRetryCts = cts;
        _ = RetryBatteryAsync(client, cts.Token);
    }

    private static readonly int[] BatteryRetryDelaysMs = [0, 1500, 1500, 1500, 1500];

    private async Task RetryBatteryAsync(ChameleonClient client, CancellationToken ct)
    {
        // Re-check once right after settings load, then every 1.5 s up to 6 s to cover the 5 s ADC timer.
        try
        {
            foreach (var delay in BatteryRetryDelaysMs)
            {
                if (delay > 0) await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested || !ReferenceEquals(_device.Client, client)) return;
                var fresh = await client.GetBatteryInfoAsync(ct);
                if (fresh.Millivolts > 0)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!ct.IsCancellationRequested && ReferenceEquals(_device.Client, client))
                            Battery = fresh;
                    });
                    return;
                }
            }
        }
        catch { /* disconnected, canceled, or unsupported */ }
    }

    private Task ToggleModeAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var target = ReaderMode ? DeviceMode.Reader : DeviceMode.Tag;
        await client.SetDeviceModeAsync(target);
        Notify(string.Format(Strings.Chameleon_SwitchedMode, target));
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
            throw new FormatException(Strings.Chameleon_PinLength);

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

    private Task ConfirmDangerAsync()
    {
        var action = PendingDanger;
        PendingDanger = ChameleonDangerAction.None;
        return action switch
        {
            ChameleonDangerAction.ClearBonds => ClearBondsAsync(),
            ChameleonDangerAction.ResetSettings => ResetAsync(),
            ChameleonDangerAction.FactoryReset => FactoryResetAsync(),
            _ => Task.CompletedTask,
        };
    }

    private Task ResetAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.ResetSettingsAsync();
        await LoadSettingsAsync(client);
    }, Strings.Chameleon_SettingsReset);

    private Task ClearBondsAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.DeleteAllBleBondsAsync();
    }, Strings.Chameleon_BondsCleared);

    private Task FactoryResetAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.WipeFdsAsync();
        // The device drops the link after a factory reset.
        await _device.DisconnectAsync();
    }, Strings.Chameleon_FactoryResetDone);
}
