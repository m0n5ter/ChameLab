using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>The three screens of the mfkey32 wizard.</summary>
public enum Mfkey32Step { Intro, Collecting, Done }

/// <summary>One key the wizard recovered, with a one-click save into a dictionary.</summary>
public sealed class RecoveredKeyViewModel : ViewModelBase
{
    private bool _saved;

    public RecoveredKeyViewModel(int block, char keyChar, string keyHex, Action<RecoveredKeyViewModel> save)
    {
        Block = block;
        KeyChar = keyChar;
        KeyHex = keyHex;
        SaveCommand = ReactiveCommand.Create(() => save(this), this.WhenAnyValue(x => x.Saved, s => !s));
    }

    public int Block { get; }
    public char KeyChar { get; }
    /// <summary>The 12-hex-digit key.</summary>
    public string KeyHex { get; }

    public string Header => $"Block {Block} · Key {KeyChar}";

    public ICommand SaveCommand { get; }

    /// <summary>The key has been added to a dictionary (the Save button then reads "Saved").</summary>
    public bool Saved
    {
        get => _saved;
        set => this.RaiseAndSetIfChanged(ref _saved, value);
    }
}

/// <summary>
/// Tools page. The mfkey32 wizard recovers MIFARE Classic keys with no setup from the
/// user: it switches the device to tag mode, puts a blank Classic card on a free slot,
/// captures a reader's authentication attempts, and recovers the key in-app as soon as it
/// has enough nonces. Everything it changed on the device (the device mode, the active slot
/// and the bait slot) is put back when the wizard is left.
/// </summary>
public sealed class ToolsViewModel : PageViewModelBase
{
    private const string RecoveredDictName = "Recovered keys";
    private static readonly TagSpecificType BaitType = TagSpecificType.Mifare1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1200);

    private readonly DeviceService _device;
    private readonly DictionaryLibrary _dictionaries;

    private Mfkey32Step _step = Mfkey32Step.Intro;
    private int _capturedCount;

    // The device state captured when the wizard starts, restored when it ends.
    private bool _active;
    private DeviceMode _prevMode;
    private int _prevSlot;
    private int _baitSlot;

    private CancellationTokenSource? _pollCts;

    public ToolsViewModel(DeviceService device, DictionaryLibrary dictionaries) : base("Tools", "bolt")
    {
        _device = device;
        _dictionaries = dictionaries;

        StartCommand = ReactiveCommand.CreateFromTask(StartAsync);
        RestartCommand = ReactiveCommand.CreateFromTask(RestartAsync);
        FinishCommand = ReactiveCommand.CreateFromTask(() => StopAsync());
    }

    public ObservableCollection<RecoveredKeyViewModel> Keys { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand FinishCommand { get; }

    // ---- Step ----------------------------------------------------------

    public Mfkey32Step Step
    {
        get => _step;
        private set
        {
            this.RaiseAndSetIfChanged(ref _step, value);
            this.RaisePropertyChanged(nameof(IsIntro));
            this.RaisePropertyChanged(nameof(IsCollecting));
            this.RaisePropertyChanged(nameof(IsDone));
        }
    }

    public bool IsIntro => _step == Mfkey32Step.Intro;
    public bool IsCollecting => _step == Mfkey32Step.Collecting;
    public bool IsDone => _step == Mfkey32Step.Done;

    /// <summary>How many usable (first-auth) nonces the emulator has captured so far.</summary>
    public int CapturedCount
    {
        get => _capturedCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _capturedCount, value);
            this.RaisePropertyChanged(nameof(CaptureText));
        }
    }

    public string CaptureText => _capturedCount == 0
        ? "Waiting for the reader… hold your Chameleon to it and let it scan a few times."
        : $"Captured {_capturedCount} authentication(s). The key is recovered automatically once there are enough.";

    public string KeyCountText => Keys.Count == 1 ? "Recovered 1 key." : $"Recovered {Keys.Count} keys.";

    // ---- Wizard --------------------------------------------------------

    private Task StartAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) { SetStatus("Connect a Chameleon first.", isError: true); return; }

        await client.WithSlotLockAsync(async () =>
        {
            _prevMode = await client.GetDeviceModeAsync();
            _prevSlot = await client.GetActiveSlotAsync();

            var slots = await client.GetAllSlotsAsync();
            var free = slots.FirstOrDefault(s =>
                s.HfType == TagSpecificType.Undefined && s.LfType == TagSpecificType.Undefined);
            if (free is null)
                throw new InvalidOperationException("All eight slots are in use. Free one up, then start the wizard.");
            _baitSlot = free.Index;

            // Put a blank Classic card on the free slot, make it active and start capturing nonces.
            await client.SetDeviceModeAsync(DeviceMode.Tag);
            await client.SetActiveSlotAsync(_baitSlot);
            await client.SetSlotTagTypeAsync(_baitSlot, BaitType);
            await client.SetSlotDataDefaultAsync(_baitSlot, BaitType);
            await client.SetSlotEnableAsync(_baitSlot, TagSenseType.Hf, enabled: true);
            await client.SetActiveSlotAsync(_baitSlot);
            await client.SetMf1DetectionEnableAsync(true);
        });

        _active = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Keys.Clear();
            this.RaisePropertyChanged(nameof(KeyCountText));
            CapturedCount = 0;
            Step = Mfkey32Step.Collecting;
        });
        StartPolling();
    });

    /// <summary>Done → collect again, reusing the bait slot and clearing the capture buffer.</summary>
    private async Task RestartAsync()
    {
        if (!_active) { await StartAsync(); return; }
        await RunAsync(async () =>
        {
            var client = _device.Client;
            if (client is null) { await StopAsync(); return; }
            await client.WithSlotLockAsync(async () =>
            {
                await client.SetActiveSlotAsync(_baitSlot);
                // Toggling detection off then on resets the firmware's capture buffer.
                await client.SetMf1DetectionEnableAsync(false);
                await client.SetMf1DetectionEnableAsync(true);
            });
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Keys.Clear();
                this.RaisePropertyChanged(nameof(KeyCountText));
                CapturedCount = 0;
                Step = Mfkey32Step.Collecting;
            });
            StartPolling();
        });
    }

    /// <summary>Ends the wizard: stops polling, puts the device back as it was, returns to the intro.</summary>
    private async Task StopAsync()
    {
        _pollCts?.Cancel();
        _pollCts = null;

        if (_active)
        {
            _active = false;
            var client = _device.Client;
            if (client is not null)
            {
                try
                {
                    await client.WithSlotLockAsync(async () =>
                    {
                        await client.SetActiveSlotAsync(_baitSlot);
                        await client.SetMf1DetectionEnableAsync(false);
                        await client.DeleteSlotSenseTypeAsync(_baitSlot, TagSenseType.Hf);
                        await client.SetSlotEnableAsync(_baitSlot, TagSenseType.Hf, enabled: false);
                        if (_prevSlot is >= 1 and <= 8) await client.SetActiveSlotAsync(_prevSlot);
                        await client.SetDeviceModeAsync(_prevMode);
                    });
                }
                catch { /* best effort: the device config was never persisted */ }
            }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Keys.Clear();
            this.RaisePropertyChanged(nameof(KeyCountText));
            CapturedCount = 0;
            Step = Mfkey32Step.Intro;
        });
    }

    public override Task OnDeactivatedAsync() => StopAsync();

    // ---- Polling / recovery --------------------------------------------

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = _device.Client;
                if (client is null) { await StopAsync(); return; }

                var logs = await client.WithSlotLockAsync(() => client.GetAllMf1DetectionLogsAsync(ct), ct);
                if (ct.IsCancellationRequested) return;

                // mfkey32 needs a first authentication: a nested auth sends an encrypted nonce
                // that would "recover" a wrong but plausible key, so those are left out.
                var usable = logs.Where(l => !l.IsNested).ToList();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CapturedCount = usable.Count);

                // Two distinct (nt, nr, ar) exchanges on one block + key type are enough (Moebius).
                var ready = usable
                    .GroupBy(l => (l.Block, l.KeyChar))
                    .Select(g => g.GroupBy(x => x.NtHex + x.NrHex + x.ArHex).Select(x => x.First()).ToList())
                    .Where(d => d.Count >= 2)
                    .ToList();

                if (ready.Count > 0 && await TryRecoverAsync(ready)) return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* a transient read error: keep polling */ }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Recovers every ready group; on success shows the keys and ends collection.</summary>
    private async Task<bool> TryRecoverAsync(IReadOnlyList<List<DetectionLog>> ready)
    {
        var found = await Task.Run(() =>
        {
            var keys = new List<(byte[] Uid, int Block, char KeyChar, string Hex)>();
            var seen = new HashSet<string>();
            foreach (var d in ready)
                if (Mfkey32Recovery.Recover(d[0], d[1]) is { } k)
                {
                    var hex = k.ToString("X12");
                    if (seen.Add($"{d[0].UidHex}:{d[0].Block}:{d[0].KeyChar}:{hex}"))
                        keys.Add((d[0].Uid, d[0].Block, d[0].KeyChar, hex));
                }
            return keys;
        });

        if (found.Count == 0) return false; // one candidate couldn't be resolved yet — gather more

        foreach (var (uid, _, _, hex) in found)
        {
            try { _dictionaries.KnownKeys.Remember(uid, new[] { Convert.FromHexString(hex) }); }
            catch (Exception ex) { SetStatus($"Could not save known card keys: {ex.Message}", isError: true); }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Keys.Clear();
            foreach (var (_, block, keyChar, hex) in found)
                Keys.Add(new RecoveredKeyViewModel(block, keyChar, hex, SaveKey));
            this.RaisePropertyChanged(nameof(KeyCountText));
            Step = Mfkey32Step.Done;
        });
        return true;
    }

    // ---- Saving a key --------------------------------------------------

    private void SaveKey(RecoveredKeyViewModel key)
    {
        var bytes = Convert.FromHexString(key.KeyHex);
        var dict = _dictionaries.Dictionaries.FirstOrDefault(d => d.Name == RecoveredDictName);
        if (dict is null)
        {
            dict = new KeyDictionary { Name = RecoveredDictName };
            dict.Keys.Add(bytes);
            _dictionaries.Add(dict);
        }
        else
        {
            if (!dict.Keys.Any(k => k.SequenceEqual(bytes))) dict.Keys.Add(bytes);
            _dictionaries.Update(dict);
        }
        key.Saved = true;
        SetStatus($"Saved the key to the “{RecoveredDictName}” dictionary.");
    }
}
