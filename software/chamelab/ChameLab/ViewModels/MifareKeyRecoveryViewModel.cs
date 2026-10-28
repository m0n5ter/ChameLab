using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>A recovery session for the physical target card. It never edits the source card.
/// All methods share the same authenticated keys, dictionary selection and cancellation lifetime.</summary>
public sealed class MifareKeyRecoveryViewModel : ReactiveObject, IKeyRecoveryContext
{
    private readonly Func<ChameleonClient?> _client;
    private readonly DictionaryLibrary _dictionaries;
    private readonly Func<IReadOnlyList<SlotInfo>> _slots;
    private readonly Func<IReadOnlyList<byte[]>> _sourceKeys;
    private readonly Func<Func<Task>, Task> _run;
    private readonly IHardnestedEngine _hardnested;
    private readonly Action? _cancelWrite;
    private readonly Func<MifareDump, CardSave> _targetCard;
    private CancellationTokenSource? _cts;
    private MifareClassicPrngType? _prng;
    private bool _darkside;
    private bool _online, _busy;
    private int _version;
    private string? _progress, _detail, _foundKey;
    private int _tried, _total;
    private int? _captured;
    private bool _mfkey32;
    private bool _writing;
    private bool _usesKeys = true;
    private bool _showWriteProgress;
    private bool _hasWriteSession;
    private int _writeSector = -1;
    private bool _knownKeysSelected = true;
    private int _selectedSector;
    public MifareDump? Target { get; private set; }
    public Task? RunningTask { get; private set; }

    public MifareKeyRecoveryViewModel(Func<ChameleonClient?> client, DictionaryLibrary dictionaries,
        Func<IReadOnlyList<SlotInfo>> slots, Func<IReadOnlyList<byte[]>> sourceKeys,
        Func<Func<Task>, Task> run, IHardnestedEngine? hardnested = null, Action? cancelWrite = null,
        Func<MifareDump, CardSave>? targetCard = null)
    {
        _client = client;
        _dictionaries = dictionaries;
        _slots = slots;
        _sourceKeys = sourceKeys;
        _run = run;
        _hardnested = hardnested ?? AppServices.Hardnested;
        _cancelWrite = cancelWrite;
        _targetCard = targetCard ?? (target => CardFormats.FromMifareDump(target));
        var idle = this.WhenAnyValue(x => x.CanOperate);
        DumpCommand = ReactiveCommand.CreateFromTask(() => StartAsync(SearchAsync), idle);
        CheckRecoveryCommand = ReactiveCommand.CreateFromTask(() => StartAsync(ProbeAsync), idle);
        DarksideCommand = Attack(MifareRecoveryMethod.Darkside, this.WhenAnyValue(x => x.ShowDarkside));
        NestedCommand = Attack(MifareRecoveryMethod.Nested, this.WhenAnyValue(x => x.ShowNested));
        StaticNestedCommand = Attack(MifareRecoveryMethod.StaticNested, this.WhenAnyValue(x => x.ShowStaticNested));
        HardnestedCommand = Attack(MifareRecoveryMethod.Hardnested, this.WhenAnyValue(x => x.ShowHardnested));
        Mfkey32Command = ReactiveCommand.CreateFromTask(() => StartAsync(Mfkey32Async),
            this.WhenAnyValue(x => x.CanRunMfkey32));
        CancelDumpCommand = ReactiveCommand.Create(Cancel);
    }

    private ICommand Attack(MifareRecoveryMethod method, IObservable<bool> availability) =>
        ReactiveCommand.CreateFromTask(() => StartAsync((client, ct) => RecoverAsync(client, method, ct)),
            this.WhenAnyValue(x => x.CanOperate).CombineLatest(availability,
                (idle, show) => idle && show && (method != MifareRecoveryMethod.Hardnested || HardnestedWarning is null)));

    public ICommand DumpCommand { get; }
    public ICommand CheckRecoveryCommand { get; }
    public ICommand DarksideCommand { get; }
    public ICommand NestedCommand { get; }
    public ICommand StaticNestedCommand { get; }
    public ICommand HardnestedCommand { get; }
    public ICommand Mfkey32Command { get; }
    public ICommand CancelDumpCommand { get; }
    public ObservableCollection<DumpKeySourceViewModel> DumpKeySources { get; } = new();
    public ObservableCollection<DumpSectorRow> DumpSectors { get; } = new();
    public bool CanOperate => _online && !_busy && _cts is null && !_writing && !_hasWriteSession;
    public bool CanDump => true;
    public bool ShowRecoveryTools => _usesKeys && (IsDumpRunning || !RecoveryComplete);
    public bool ShowWriteProgress => _showWriteProgress;
    public bool IsBusy => _busy || IsDumpRunning;
    public bool IsDumpRunning => _cts is not null || _writing;
    public bool ShowDumpActions => !IsDumpRunning && !RecoveryComplete;
    public bool IsUltralightDump => false;
    public bool HasDumpSectors => DumpSectors.Count > 0;
    public bool AllTargetKeysRecovered => Target is { } target
        && target.KeyA.All(key => key is { Length: 6 })
        && target.KeyB.All(key => key is { Length: 6 })
        && DumpSectors.Count == target.SectorCount
        && DumpSectors.All(sector => sector.KeyAState == KeyCellState.Found && sector.KeyBState == KeyCellState.Found);
    public bool AllPlannedSectorsWritten => ShowWriteProgress
        && DumpSectors.Any(sector => sector.WriteState != SectorWriteState.None)
        && DumpSectors.All(sector => sector.WriteState is SectorWriteState.None or SectorWriteState.Written);
    private bool RecoveryComplete => _hasWriteSession || AllTargetKeysRecovered || AllPlannedSectorsWritten;
    private bool HasKnownKeys => Target is { } d && d.KeyA.Concat(d.KeyB).Any(k => k is not null);
    private bool HasUnknownKeys => Target is null || Target.KeyA.Concat(Target.KeyB).Any(k => k is null);
    public bool ShowCheckRecovery => _prng is null;
    public bool ShowDarkside => _darkside && HasUnknownKeys;
    public bool ShowNested => _prng == MifareClassicPrngType.Weak && HasKnownKeys && HasUnknownKeys;
    public bool ShowStaticNested => _prng == MifareClassicPrngType.Static && HasKnownKeys && HasUnknownKeys;
    public bool ShowHardnested => _prng == MifareClassicPrngType.Hard && HasKnownKeys && HasUnknownKeys;
    public string? HardnestedWarning => _hardnested.UnavailableReason;
    public string TryKeysTip => "Find the target card's keys using the selected dictionaries and source keys";
    public string RecoverySectorHint => ShowWriteProgress
        ? "Hover a sector for written block counts and errors" : "Target card keys · hover a sector for key details";
    public int RecoverySelectedSector
    {
        get => _selectedSector;
        set => this.RaiseAndSetIfChanged(ref _selectedSector, value);
    }
    public string? DumpProgressText => _progress;
    public string? DumpProgressDetail => _detail;
    public int DumpKeyTried => _tried;
    public int DumpKeyTotal => _total;
    public bool HasDumpKeyProgress => _total > 0;
    public string DumpKeyProgressTip => _writing ? $"{_tried}/{_total} blocks written" : $"{_tried} of {_total} keys tried";
    public string DumpKeyCountText => $"{BuildKeyDictionary().Count} unique keys (source keys are also tried)";
    public string DumpStopLabel => "Stop";
    public string DumpStopTip => _writing ? "Stop writing; blocks already written stay on the card"
        : "Stop recovery and keep the keys already found";
    public bool IsMfkey32Done => false;
    public bool IsMfkey32Progress => _mfkey32;
    public string? Mfkey32FoundKey => _foundKey;
    public bool ShowMfkey32FoundKey => _foundKey is not null && _detail is null;
    public string Mfkey32CaptureText => $"Captured {_captured} authentications";
    public bool ShowMfkey32CaptureCount => _captured is not null && _foundKey is null && _detail is null;
    public string? DumpNotice => Target is null ? "Place the target card on the reader, then try keys or check recovery methods."
        : $"Target UID: {Target.UidHex}";
    public bool ShowDumpNotice => false;
    public string? RecoveryIdentity => _usesKeys ? DumpNotice : null;
    public bool ShowMfkey32 => false;
    public bool CanRunMfkey32 => ShowMfkey32 && CanOperate && Target is not null && Mfkey32Slot is not null;
    private SlotInfo? Mfkey32Slot => Target is { } target
        ? Mfkey32Wizard.FindSlot(_slots(), _targetCard(target)) : null;
    public string Mfkey32Tip => Target is null ? "Scan the target card before starting mfkey32."
        : Mfkey32Slot is null ? "Load the target card into a slot before starting mfkey32."
        : "Recover the target card's keys from a reader using mfkey32";

    public void SetAvailability(bool online, bool busy)
    {
        _online = online;
        _busy = busy;
        if (!online) Reset();
        Refresh();
    }

    public void SetKeySources(IEnumerable<DumpKeySourceViewModel> sources)
    {
        var dictionaries = sources.ToArray();
        DumpKeySources.Clear();
        var known = new DumpKeySourceViewModel(KnownCardKeys.Id, KnownCardKeys.Name,
            Target is { } d ? _dictionaries.KnownKeys.ForUid(d.Uid) : Array.Empty<byte[]>(), _knownKeysSelected);
        known.WhenAnyValue(x => x.IsSelected).Skip(1).Subscribe(selected =>
        {
            _knownKeysSelected = selected;
            Refresh();
        });
        DumpKeySources.Add(known);
        foreach (var source in dictionaries) DumpKeySources.Add(source);
        Refresh();
    }

    public IReadOnlyList<byte[]> BuildKeyDictionary() => DumpKeySources.Where(s => s.IsSelected)
        .SelectMany(s => s.IsKnownCardKeys
            ? Target is { } d ? _dictionaries.KnownKeys.ForUid(d.Uid) : Array.Empty<byte[]>() : s.Keys)
        .DistinctBy(Convert.ToHexString).ToArray();

    public void Cancel()
    {
        if (_cts is { } cts) cts.Cancel();
        else if (_writing) _cancelWrite?.Invoke();
        else return;
        _progress = "Stopping…";
        Refresh();
    }

    public void Reset()
    {
        Cancel();
        _version++;
        Target = null;
        _prng = null;
        _darkside = false;
        _writing = false;
        _showWriteProgress = false;
        _hasWriteSession = false;
        DumpSectors.Clear();
        _progress = _detail = _foundKey = null;
        _tried = _total = 0;
        SetKeySources(DumpKeySources.Where(s => !s.IsKnownCardKeys));
        Refresh();
    }

    public void SetGeneration(MagicGeneration generation)
    {
        _usesKeys = generation != MagicGeneration.Gen1a;
        Reset();
    }

    public void PrepareWrite(TagSpecificType tag, MifareDump? source = null, bool writeBlock0 = true, bool resume = false)
    {
        _writing = true;
        _showWriteProgress = true;
        _hasWriteSession = true;
        _writeSector = -1;
        _progress = resume ? "Checking target UID…" : "Preparing write…";
        _detail = null;
        if (!resume) InitializeWriteSectors(tag, source, writeBlock0);
        Refresh();
    }

    /// <summary>Show the card geometry before any device operation starts, including keyless Gen1a writes.</summary>
    public void PreviewWrite(TagSpecificType tag, MifareDump? source = null, bool writeBlock0 = true)
    {
        _showWriteProgress = !_usesKeys;
        InitializeWriteSectors(tag, source, writeBlock0);
        Refresh();
    }

    private void InitializeWriteSectors(TagSpecificType tag, MifareDump? source, bool writeBlock0)
    {
        if (DumpSectors.Count == 0)
            ResizeSectors(MifareClassic.SectorCount(MifareClassic.FromSlotTagType(tag)));
        var blocks = source is null ? Enumerable.Range(0, tag.MifareClassicBlocks()).ToArray()
            : Enumerable.Range(0, source.BlockCount).Where(b => source.GetBlock(b) is { Length: 16 }).ToArray();
        foreach (var sector in DumpSectors) sector.BeginWrite(blocks.Where(b => b != 0 || writeBlock0));
    }

    // Keep acknowledged blocks and the updated UID/keys for a retry on the same physical target.
    public void FinishWrite(MifareWriteSession? session = null)
    {
        if (session?.TargetKeys is { } target) Target = target;
        _writing = false;
        _progress = _detail = null;
        _tried = _total = 0;
        foreach (var row in DumpSectors)
        {
            row.StopSearch();
            row.StopWrite();
            if (Target is { } keys && row.Sector < keys.SectorCount)
            {
                if (keys.KeyA[row.Sector] is { } a)
                    row.ApplyKey(new MifareKeyProgress(row.Sector, MfKeyType.A, MifareKeyPhase.Found, 0, 0, a));
                if (keys.KeyB[row.Sector] is { } b)
                    row.ApplyKey(new MifareKeyProgress(row.Sector, MfKeyType.B, MifareKeyPhase.Found, 0, 0, b));
            }
        }
        if (session is not null)
            foreach (var block in session.WrittenBlocks)
                foreach (var row in DumpSectors) row.ApplyWrite(new MifareBlockProgress(block, 1, MifareBlockState.Read));
        SetKeySources(DumpKeySources.Where(s => !s.IsKnownCardKeys));
        Refresh();
    }

    public void ReportWritingBlock(int block)
    {
        if (!_writing) return;
        var sector = MifareClassic.SectorOfBlock(block);
        if (_writeSector == sector) return;
        _writeSector = sector;
        _progress = "Writing card…";
        _detail = $"Sector {sector}";
        Refresh();
    }

    public void ReportWriteCount(MifareProgress progress)
    {
        _tried = progress.Done;
        _total = progress.Total;
        Refresh();
    }

    public void ApplyWriteProgress(MifareBlockProgress progress)
    {
        foreach (var sector in DumpSectors) sector.ApplyWrite(progress);
        if (progress.State == MifareBlockState.Reading) ReportWritingBlock(progress.First);
    }

    private void ResizeSectors(int count)
    {
        DumpSectors.Clear();
        for (var s = 0; s < count; s++) DumpSectors.Add(new DumpSectorRow(s));
        RecoverySelectedSector = 0;
    }

    private Task StartAsync(Func<ChameleonClient, CancellationToken, Task> operation)
    {
        if (!CanOperate) return Task.CompletedTask;
        return RunningTask = _run(async () =>
        {
            if (_client() is not { } client) return;
            using var cts = new CancellationTokenSource();
            _cts = cts;
            _progress = "Starting recovery…";
            Refresh();
            try { await operation(client, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            finally
            {
                try
                {
                    if (Target is { } target)
                        _dictionaries.KnownKeys.Remember(target.Uid, target.KeyA.Concat(target.KeyB).OfType<byte[]>());
                }
                finally
                {
                    // Drain posted progress before clearing the session's running state.
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _cts = null;
                        _progress = _detail = _foundKey = null;
                        _captured = null;
                        _mfkey32 = false;
                        _tried = _total = 0;
                        foreach (var row in DumpSectors) row.StopSearch();
                        Refresh();
                    }, DispatcherPriority.Background);
                }
            }
        });
    }

    private async Task<MifareDump> EnsureTargetAsync(ChameleonClient client, CancellationToken ct, bool allowChange = false)
    {
        await client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count != 1) throw new InvalidOperationException("Place exactly one target card on the reader.");
        var tag = tags[0];
        if (!await client.Mf1IsSupportedAsync(ct))
            throw new InvalidOperationException("The target must be a MIFARE Classic card.");
        ct.ThrowIfCancellationRequested();
        if (Target is { } current && !current.Uid.AsSpan().SequenceEqual(tag.Uid) && !allowChange)
            throw new InvalidOperationException("The target card changed. Close and reopen the writer before continuing.");
        if (Target is null || !Target.Uid.AsSpan().SequenceEqual(tag.Uid))
        {
            _showWriteProgress = false;
            Target = new MifareDump(MifareClassic.FromSak(tag.Sak), tag.Uid, tag.Atqa, tag.Sak);
            _prng = null;
            _darkside = false;
            ResizeSectors(Target.SectorCount);
            SetKeySources(DumpKeySources.Where(s => !s.IsKnownCardKeys));
        }
        Refresh();
        return Target;
    }

    private async Task ProbeAsync(ChameleonClient client, CancellationToken ct)
        => await ProbeCurrentAsync(client, ct, allowChange: true);

    private async Task ProbeCurrentAsync(ChameleonClient client, CancellationToken ct, bool allowChange)
    {
        var version = _version;
        await EnsureTargetAsync(client, ct, allowChange);
        _progress = "Checking recovery methods…";
        Refresh();
        var prng = await client.DetectNtLevelAsync(ct);
        var darkside = false;
        if (prng != MifareClassicPrngType.Static)
        {
            try { darkside = await client.CheckDarksideAsync(ct) == DarksideResult.Vulnerable; }
            catch (ChameleonException) { }
        }
        ct.ThrowIfCancellationRequested();
        if (version != _version) return;
        _prng = prng;
        _darkside = darkside;
        Refresh();
    }

    private async Task SearchAsync(ChameleonClient client, CancellationToken ct)
    {
        var target = await EnsureTargetAsync(client, ct);
        var keys = _sourceKeys().Concat(BuildKeyDictionary()).DistinctBy(Convert.ToHexString).ToArray();
        await client.FindMifareKeysAsync(target, keys, KeyProgress(), ct);
        if (_prng is null && !ct.IsCancellationRequested) await ProbeCurrentAsync(client, ct, allowChange: false);
        Refresh();
    }

    private async Task RecoverAsync(ChameleonClient client, MifareRecoveryMethod method, CancellationToken ct)
    {
        var target = await EnsureTargetAsync(client, ct);
        var version = _version;
        var progress = new CallbackProgress<KeyRecoveryProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            if (version != _version) return;
            _progress = p.Status;
            _detail = p.Sector < 0 ? p.Detail
                : $"Sector {p.Sector} · key {(p.KeyType == MfKeyType.A ? "A" : "B")} · {p.Detail}";
            _tried = _total = 0;
            if (p.Sector >= 0 && p.Sector < DumpSectors.Count)
                DumpSectors[p.Sector].ApplyKey(new MifareKeyProgress(p.Sector, p.KeyType, p.Mark switch
                {
                    KeyMark.Found => MifareKeyPhase.Found,
                    KeyMark.Checking => MifareKeyPhase.Searching,
                    _ => MifareKeyPhase.NotFound,
                }, 0, 0, p.Key));
            Refresh();
        }));
        await new MifareKeyRecovery(client, _hardnested).RecoverAsync(target, method, progress, ct);
        Refresh();
    }

    private async Task Mfkey32Async(ChameleonClient client, CancellationToken ct)
    {
        var target = await EnsureTargetAsync(client, ct);
        var version = _version;
        _mfkey32 = true;
        Refresh();
        var progress = new CallbackProgress<Mfkey32Progress>(p => Dispatcher.UIThread.Post(() =>
        {
            if (version != _version) return;
            _progress = p.Status;
            _captured = p.CapturedCount;
            _foundKey = p.KeyFound ?? _foundKey;
            Refresh();
        }));
        await new Mfkey32Wizard(client, _dictionaries.KnownKeys).RunAsync(_targetCard(target),
            progress, keys => client.FindMifareKeysAsync(target, keys, KeyProgress(), ct), ct);
    }

    private IProgress<MifareKeyProgress> KeyProgress()
    {
        var version = _version;
        return new CallbackProgress<MifareKeyProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            if (version == _version) ApplyKey(p);
        }));
    }

    public void ApplyKey(MifareKeyProgress p)
    {
        if (_writing) return;
        if (p.Sector < 0 || p.Sector >= DumpSectors.Count) return;
        DumpSectors[p.Sector].ApplyKey(p);
        if (_cts?.IsCancellationRequested == true) return;
        _progress = p.Phase == MifareKeyPhase.Searching ? "Checking dictionary keys…"
            : p.Phase == MifareKeyPhase.Found ? "Dictionary key found." : "Dictionary key not found.";
        _detail = $"Sector {p.Sector} · key {(p.KeyType == MfKeyType.A ? "A" : "B")} · {p.Tried}/{p.Total}";
        _tried = p.Tried;
        _total = p.Total;
        Refresh();
    }

    public void Refresh()
    {
        // The panel is a projection of this session; one update refreshes its derived availability and progress.
        foreach (var name in PresentationProperties) this.RaisePropertyChanged(name);
        this.RaisePropertyChanged(nameof(CanOperate));
        this.RaisePropertyChanged(nameof(CanRunMfkey32));
        this.RaisePropertyChanged(nameof(AllTargetKeysRecovered));
        this.RaisePropertyChanged(nameof(AllPlannedSectorsWritten));
    }
    private static readonly string[] PresentationProperties = typeof(IKeyRecoveryContext).GetProperties()
        .Where(p => !typeof(ICommand).IsAssignableFrom(p.PropertyType)).Select(p => p.Name).ToArray();

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
