using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ChameLab.Services;
using ChameLab.Transport;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class ConnectViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private TransportCandidate? _selected;
    private string _manualEndpoint = "127.0.0.1:4321";

    public ConnectViewModel(DeviceService device) : base("Connect", "link")
    {
        _device = device;

        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        PickCommand = ReactiveCommand.CreateFromTask(PickAsync);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync,
            this.WhenAnyValue(x => x.Selected).Select(s => s is not null));
        ConnectManualCommand = ReactiveCommand.CreateFromTask(ConnectManualAsync,
            this.WhenAnyValue(x => x.ManualEndpoint).Select(e => !string.IsNullOrWhiteSpace(e)));
    }

    public ObservableCollection<TransportCandidate> Candidates { get; } = new();

    public TransportCandidate? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    private bool _hasCandidates;

    public bool HasCandidates
    {
        get => _hasCandidates;
        private set => this.RaiseAndSetIfChanged(ref _hasCandidates, value);
    }

    private bool HasUsb => _device.Scanners.Any(s => s.Kind == TransportKind.Serial);
    private bool HasBle => _device.Scanners.Any(s => s.Kind == TransportKind.Ble);

    /// <summary>A scanner whose new devices come from a browser chooser, behind the Pair over Bluetooth button.</summary>
    private ITransportPicker? Picker => _device.Scanners.OfType<ITransportPicker>().FirstOrDefault();

    public bool CanPick => Picker is not null;

    /// <summary>The transport the empty state pictures: Bluetooth where it's the only way in, else USB.</summary>
    public TransportKind PrimaryKind => HasBle && !HasUsb ? TransportKind.Ble : TransportKind.Serial;

    // The device wakes by itself when plugged in over USB; over BLE it only advertises while awake.
    public string IntroHint => (HasUsb, HasBle) switch
    {
        (true, true) => "Plug the device in over USB, or press a button on it to wake it up for Bluetooth, then pick it below.",
        (false, true) => "Press a button on the device to wake it up, then pick it below.",
        _ => "Plug the device in over USB, then pick it below.",
    };

    // A web page may only open a serial port or a Bluetooth device the user picked in the
    // browser's chooser, which opens on Rescan (USB) or Pair over Bluetooth; after that the device shows
    // up on its own like on desktop.
    public string ScanHint => OperatingSystem.IsBrowser()
        ? CanPick
            ? "Pick your Chameleon once with Rescan (USB) or Pair over Bluetooth; after that it appears here automatically."
            : "Press Rescan to pick your Chameleon once; after that it appears here automatically."
        : HasBle && !HasUsb
            ? "Nearby devices appear here automatically while they're awake."
            : "Plugged-in devices appear here automatically.";

    public string EmptyHint => OperatingSystem.IsBrowser()
        ? CanPick
            ? "Plug the Chameleon in and press Rescan, or wake it up and press Pair over Bluetooth, then allow this page to use it (Chrome or Edge)."
            : "Plug the Chameleon in, then press Rescan and allow this page to use it (Chrome or Edge)."
        : (HasUsb, HasBle) switch
        {
            (true, true) => "Plug the Chameleon in, or press a button on it to wake it up for Bluetooth — it will show up here on its own.",
            (false, true) => "Turn Bluetooth on and press a button on the Chameleon to wake it up — it will show up here on its own.",
            _ => "Plug the Chameleon in — it will show up here on its own.",
        };

    public string ManualEndpoint
    {
        get => _manualEndpoint;
        set => this.RaiseAndSetIfChanged(ref _manualEndpoint, value);
    }

    public ICommand ScanCommand { get; }
    public ICommand PickCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectManualCommand { get; }

    /// <summary>How often cheap scanners (USB serial) are re-run to catch a device being plugged in.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _pollTimer;
    private bool _autoScanning;

    /// <summary>
    /// Called while this page is shown and nothing is connected: scans right away, then keeps
    /// polling the cheap scanners and runs the watchers so a device appears without pressing Rescan.
    /// </summary>
    public void StartWatching()
    {
        if (_pollTimer is not null) return;
        _ = AutoScanAsync(AutoScan.OnOpen);
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => _ = AutoScanAsync(AutoScan.Poll);
        _pollTimer.Start();
        StartWatchers();
    }

    public void StopWatching()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        StopWatchers();
    }

    private void StartWatchers()
    {
        foreach (var scanner in _device.Scanners)
        {
            if (scanner is not ITransportWatcher watcher) continue;
            var kinds = new HashSet<TransportKind> { scanner.Kind };
            watcher.StartWatching(found => Dispatcher.UIThread.Post(() =>
            {
                if (_pollTimer is not null) Apply(found, kinds);
            }));
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _device.Scanners.OfType<ITransportWatcher>())
            watcher.StopWatching();
    }

    /// <summary>Silent background scan: no busy bar, no notice, errors ignored.</summary>
    private async Task AutoScanAsync(AutoScan trigger)
    {
        if (_autoScanning || IsBusy) return;
        _autoScanning = true;
        try
        {
            var found = await Task.Run(() => _device.ScanAsync(trigger));
            Apply(found, ScannedKinds(trigger));
        }
        catch { /* next tick retries */ }
        finally { _autoScanning = false; }
    }

    private Task ScanAsync() => RunAsync(async () =>
    {
        var found = await _device.ScanAsync();
        Dispatcher.UIThread.Post(() => Apply(found, ScannedKinds(AutoScan.Never)));
        Notify(found.Count == 0
            ? "No Chameleon found. Plug it in, or use a network bridge."
            : $"Found {found.Count} device(s).");
    });

    /// <summary>Opens the browser's Bluetooth chooser and selects the picked device, ready to connect.</summary>
    private Task PickAsync() => RunAsync(async () =>
    {
        if (Picker is not { } picker) return;
        var picked = await picker.PickAsync();
        if (picked is null) return;
        var scanner = (ITransportScanner)picker;
        var known = await scanner.ScanAsync(AutoScan.Poll);
        Dispatcher.UIThread.Post(() =>
        {
            Apply(known, new HashSet<TransportKind> { scanner.Kind });
            Selected = Candidates.FirstOrDefault(c => c.Kind == picked.Kind && c.Id == picked.Id) ?? Selected;
        });
        Notify($"Selected {picked.DisplayName}. Press Connect.");
    });

    private HashSet<TransportKind> ScannedKinds(AutoScan trigger) =>
        _device.Scanners.Where(s => DeviceService.RunsOn(s, trigger)).Select(s => s.Kind).ToHashSet();

    /// <summary>
    /// Replaces the candidates of the transport kinds that were just scanned, keeping the others
    /// (a quick serial poll mustn't drop BLE devices found earlier) and the current selection.
    /// </summary>
    private void Apply(IReadOnlyList<TransportCandidate> found, HashSet<TransportKind> scannedKinds)
    {
        var next = Candidates.Where(c => !scannedKinds.Contains(c.Kind)).Concat(found).ToList();
        if (next.SequenceEqual(Candidates)) return;

        // Match by id: a rescanned device comes back as a new record (e.g. with a fresh RSSI).
        var keep = Selected;
        Candidates.Clear();
        foreach (var c in next) Candidates.Add(c);
        Selected = Candidates.FirstOrDefault(c => keep is not null && c.Kind == keep.Kind && c.Id == keep.Id)
                   ?? (Candidates.Count > 0 ? Candidates[0] : null);
        HasCandidates = Candidates.Count > 0;
    }

    private Task ConnectAsync() => RunAsync(async () =>
    {
        if (Selected is null) { ReportError("Select a device first."); return; }
        // A BLE connection attempt made while the adapter is scanning may stall or fail.
        StopWatchers();
        try { await _device.ConnectAsync(Selected); }
        finally
        {
            if (_pollTimer is not null && _device.State != ConnectionState.Connected) StartWatchers();
        }
    });

    private Task ConnectManualAsync() => RunAsync(async () =>
    {
        var candidate = TcpScanner.Candidate(ManualEndpoint);
        await _device.ConnectAsync(candidate);
    });
}
