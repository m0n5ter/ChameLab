using System;
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

public sealed class DashboardViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private DeviceInfo? _info;
    private BatteryInfo? _battery;
    private bool _readerMode;
    private bool _suppressModeWrite;

    public DashboardViewModel(DeviceService device, FirmwareUpdateViewModel firmware) : base("Dashboard", "dashboard")
    {
        _device = device;
        Firmware = firmware;
        RefreshCommand = ReactiveCommand.CreateFromTask(OnActivatedAsync);
        ToggleModeCommand = ReactiveCommand.CreateFromTask(ToggleModeAsync);
        PrevSlotCommand = ReactiveCommand.CreateFromTask(() => ChangeSlotAsync(-1));
        NextSlotCommand = ReactiveCommand.CreateFromTask(() => ChangeSlotAsync(+1));
        SelectSlotCommand = ReactiveCommand.CreateFromTask<SlotItemViewModel>(s => ActivateSlotAsync(s.Index));

        // Writing the mode on user toggles only — not when we load the current value.
        this.WhenAnyValue(x => x.ReaderMode)
            .Skip(1)
            .Subscribe(ignored => { if (!_suppressModeWrite) { _ = ToggleModeAsync(); } });
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
        }
    }

    public SlotItemViewModel? ActiveItem => Slots.FirstOrDefault(s => s.Index == ActiveSlot);

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
        SetStatus($"Slot {target} is now active.");
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
            slots = (await client.GetAllSlotsAsync()).ToArray();
            active = slots.FirstOrDefault(s => s.IsActive)?.Index ?? 1;
        }
        catch { /* older firmware */ }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Info = info;
            Battery = bat;
            Slots.Clear();
            foreach (var slot in slots) Slots.Add(new SlotItemViewModel(slot));
            _activeSlot = 0;
            ActiveSlot = active;
            _suppressModeWrite = true;
            ReaderMode = info.Mode == DeviceMode.Reader;
            _suppressModeWrite = false;
        });
    });

    private Task ToggleModeAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var target = ReaderMode ? DeviceMode.Reader : DeviceMode.Tag;
        await client.SetDeviceModeAsync(target);
        SetStatus($"Switched to {target} mode.");
    });
}
