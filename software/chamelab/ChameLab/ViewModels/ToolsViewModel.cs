using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>A group of captured nonces for one block + key type, recoverable in-app via mfkey32.</summary>
public sealed class NonceGroupViewModel : ViewModelBase
{
    private readonly DetectionLog? _a;
    private readonly DetectionLog? _b;
    private bool _recovering;
    private string? _recoveredKey;
    private string? _recoverError;

    public NonceGroupViewModel(int block, char keyChar, IReadOnlyList<DetectionLog> logs)
    {
        Block = block;
        KeyChar = keyChar;
        Logs = logs;
        Uid = logs[0].UidHex;

        // mfkey32 (Moebius) needs two distinct (nt, nr, ar) exchanges on the same block/key.
        var distinct = logs
            .GroupBy(l => l.NtHex + l.NrHex + l.ArHex)
            .Select(g => g.First())
            .ToList();
        HasEnough = distinct.Count >= 2;
        if (HasEnough)
        {
            _a = distinct[0];
            _b = distinct[1];
            Command = $"mfkey32v2 {_a.UidHex} {_a.NtHex} {_a.NrHex} {_a.ArHex} {_b.NtHex} {_b.NrHex} {_b.ArHex}".ToLowerInvariant();
        }
        else
        {
            Command = string.Empty;
        }

        RecoverCommand = ReactiveCommand.CreateFromTask(
            RecoverAsync,
            this.WhenAnyValue(x => x.Recovering, r => HasEnough && !r));
    }

    public int Block { get; }
    public char KeyChar { get; }
    public string Uid { get; }
    public IReadOnlyList<DetectionLog> Logs { get; }
    public bool HasEnough { get; }
    public string Command { get; }

    public ICommand RecoverCommand { get; }

    public bool Recovering
    {
        get => _recovering;
        private set => this.RaiseAndSetIfChanged(ref _recovering, value);
    }

    /// <summary>The recovered 12-hex-digit key, or null until recovery succeeds.</summary>
    public string? RecoveredKey
    {
        get => _recoveredKey;
        private set
        {
            this.RaiseAndSetIfChanged(ref _recoveredKey, value);
            this.RaisePropertyChanged(nameof(HasRecoveredKey));
        }
    }

    public bool HasRecoveredKey => !string.IsNullOrEmpty(_recoveredKey);

    /// <summary>A message shown when recovery runs but finds no key.</summary>
    public string? RecoverError
    {
        get => _recoverError;
        private set => this.RaiseAndSetIfChanged(ref _recoverError, value);
    }

    public string Header => $"Block {Block} · Key {KeyChar} · UID {Uid}";
    public string Status => HasEnough
        ? $"{Logs.Count} nonce(s) — ready"
        : $"{Logs.Count} nonce(s) — need a second auth to recover";

    private async Task RecoverAsync()
    {
        if (_a is null || _b is null) return;

        Recovering = true;
        RecoverError = null;
        try
        {
            var key = await Task.Run(() => Mfkey32Recovery.Recover(_a, _b));
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (key is { } k)
                    RecoveredKey = k.ToString("X12");
                else
                    RecoverError = "No key found — capture more authentications and try again.";
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RecoverError = $"Recovery failed: {ex.Message}");
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Recovering = false);
        }
    }
}

/// <summary>
/// Tools page. mfkey32: collect MIFARE Classic auth nonces from a reader while
/// the device emulates a Classic card, then produce the <c>mfkey32v2</c> command
/// to recover the sector key (run it with the bundled recovery tool or Proxmark).
/// </summary>
public sealed class ToolsViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private bool _detectionEnabled;
    private bool _busyToggling;
    private string _summary = "Enable collection, present the emulated card to a reader, then refresh.";

    public ToolsViewModel(DeviceService device) : base("Tools", "bolt")
    {
        _device = device;
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        ClearCommand = ReactiveCommand.CreateFromTask(ClearAsync);

        this.WhenAnyValue(x => x.DetectionEnabled)
            .Skip(1)
            .Subscribe(v => { if (!_busyToggling) _ = SetDetectionAsync(v); });
    }

    public ObservableCollection<NonceGroupViewModel> Groups { get; } = new();

    public bool DetectionEnabled
    {
        get => _detectionEnabled;
        set => this.RaiseAndSetIfChanged(ref _detectionEnabled, value);
    }

    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    public bool HasGroups => Groups.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }

    public override Task OnActivatedAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var enabled = false;
        try { enabled = await client.GetMf1DetectionEnableAsync(); } catch { }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _busyToggling = true;
            DetectionEnabled = enabled;
            _busyToggling = false;
        });
    });

    private Task SetDetectionAsync(bool enabled) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetMf1DetectionEnableAsync(enabled);
        SetStatus(enabled ? "Nonce collection enabled." : "Nonce collection disabled.");
    });

    private Task RefreshAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var logs = await client.GetAllMf1DetectionLogsAsync();

        var groups = logs
            .GroupBy(l => (l.Block, l.KeyChar))
            .OrderBy(g => g.Key.Block).ThenBy(g => g.Key.KeyChar)
            .Select(g => new NonceGroupViewModel(g.Key.Block, g.Key.KeyChar, g.ToList()))
            .ToList();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Groups.Clear();
            foreach (var g in groups) Groups.Add(g);
            this.RaisePropertyChanged(nameof(HasGroups));
            Summary = logs.Count == 0
                ? "No nonces captured yet. Enable collection and present the card to a reader."
                : $"{logs.Count} nonce(s) in {groups.Count} group(s).";
        });
    });

    private Task ClearAsync() => RunAsync(async () =>
    {
        // Toggling detection off then on resets the firmware's capture buffer.
        var client = _device.Client;
        if (client is null) return;
        await client.SetMf1DetectionEnableAsync(false);
        await client.SetMf1DetectionEnableAsync(DetectionEnabled);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Groups.Clear();
            this.RaisePropertyChanged(nameof(HasGroups));
            Summary = "Captured nonces cleared.";
        });
    });
}
