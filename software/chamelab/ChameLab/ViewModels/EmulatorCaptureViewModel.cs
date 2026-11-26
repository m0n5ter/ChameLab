using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed record EmulatorCaptureKey(string Prefix, string Key)
{
    public string Text => Prefix + Key;
}

/// <summary>Reads an emulator's collection without changing or clearing it, and saves recovered keys.</summary>
public sealed class EmulatorCaptureViewModel : PageViewModelBase
{
    private readonly Func<ChameleonClient?> _client;
    private readonly int _slot;
    private readonly DictionaryLibrary _dictionaries;
    private readonly IKeyRecoveryEngine _engine;
    private IReadOnlyList<byte[]> _keys = [];
    private int _collectionVersion;
    private string? _lastNonces;

    public EmulatorCaptureViewModel(Func<ChameleonClient?> client, int slot, bool isMifare,
        DictionaryLibrary dictionaries, IKeyRecoveryEngine? engine = null) : base(() => Strings.Cards_SaveKeys, "key")
    {
        _client = client;
        _slot = slot;
        IsMifare = isMifare;
        _dictionaries = dictionaries;
        _engine = engine ?? AppServices.KeyRecovery;
        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshAsync(),
            this.WhenAnyValue(x => x.IsBusy, x => x.IsRefreshing, x => x.CollectionEnabled,
                (busy, refreshing, enabled) => !busy && !refreshing && enabled));
        SaveToNewDictionaryCommand = ReactiveCommand.Create(SaveToNewDictionary);
        SaveToFileCommand = ReactiveCommand.CreateFromTask(SaveToFileAsync, idle);
    }

    public bool IsMifare { get; }
    /// <summary>Collection reads run independently of user actions and the window's activity bar.</summary>
    public bool IsRefreshing { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>The collection flag last read from the device or successfully saved, excluding pending edits.</summary>
    public bool CollectionEnabled { get; internal set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool CanUseFiles => StorageService.IsAvailable;
    public int? Count { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string CountLabel => IsMifare ? Strings.Slots_NonceCount : Strings.Slots_PasswordCount;
    public string CountValue => Count?.ToString() ?? "—";
    public bool HasKeys => _keys.Count > 0;
    public string? KeysText => HasKeys ? string.Concat(_keys.Select(k => Convert.ToHexString(k) + "\n")) : null;
    public IReadOnlyList<EmulatorCaptureKey> KeyRows { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = [];
    public string? Results => HasKeys ? string.Join('\n', KeyRows.Select(r => r.Text)) : null;
    public string? ResultNote { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public ICommand RefreshCommand { get; }
    public ICommand SaveToNewDictionaryCommand { get; }
    public ICommand SaveToFileCommand { get; }
    public ObservableCollection<KeysTargetViewModel> KeysTargets { get; } = [];
    public bool HasKeysTargets => KeysTargets.Count > 0;

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (IsRefreshing || IsBusy) return;
        IsRefreshing = true;
        ClearError();
        try
        {
            await using var wakeLock = await AppServices.WakeLock.AcquireAsync();
            await ReadCollectionAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[{nameof(EmulatorCaptureViewModel)}] {ex}");
            ReportError(ex.Message);
        }
        finally { IsRefreshing = false; }
    }

    private async Task ReadCollectionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var version = _collectionVersion;
        var client = _client() ?? throw new InvalidOperationException(Strings.Common_ConnectFirst);
        var captured = await client.WithSlotLockAsync(async () =>
        {
            await client.SetActiveSlotAsync(_slot, ct);
            if (IsMifare)
            {
                var count = await client.GetMf1DetectionCountAsync(ct);
                var logs = count >= 2 ? await client.GetAllMf1DetectionLogsAsync(ct) : null;
                return (Count: logs?.Count ?? count, Passwords: (IReadOnlyList<byte[]>?)null, Nonces: logs);
            }
            var passwords = await client.GetAllMf0DetectionPasswordsAsync(ct);
            return (Count: passwords.Count, Passwords: (IReadOnlyList<byte[]>?)passwords,
                Nonces: (IReadOnlyList<DetectionLog>?)null);
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (version != _collectionVersion) return;
        SetCount(captured.Count);
        if (captured.Passwords is { } values)
        {
            var keys = values.DistinctBy(Convert.ToHexString).ToArray();
            if (keys.Select(Convert.ToHexString).SequenceEqual(_keys.Select(Convert.ToHexString))) return;
            SetKeys(keys);
        }
        else if (captured.Nonces is { Count: >= 2 } logs)
        {
            var fingerprint = string.Join('|', logs.Select(l =>
                $"{l.Block}:{l.KeyType}:{l.IsNested}:{Convert.ToHexString(l.Uid)}:{Convert.ToHexString(l.Nt)}:{Convert.ToHexString(l.Nr)}:{Convert.ToHexString(l.Ar)}"));
            if (fingerprint == _lastNonces) return;
            var found = await RecoverKeysAsync(logs, _engine, ct);
            ct.ThrowIfCancellationRequested();
            if (version != _collectionVersion) return;
            var keys = found.Select(f => Convert.FromHexString(f.Key.ToString("X12")))
                .DistinctBy(Convert.ToHexString).ToArray();
            SetKeys(keys, found.Select(f => new EmulatorCaptureKey(
                $"{Convert.ToHexString(f.Log.Uid)} {MifareClassic.SectorOfBlock(f.Log.Block),2}{(f.Log.KeyType == MfKeyType.A ? 'A' : 'B')} ",
                f.Key.ToString("X12"))).ToArray());
            ResultNote = keys.Length == 0 ? Strings.Tools_NoUniqueKey : null;
            try
            {
                foreach (var item in found)
                    _dictionaries.KnownKeys.RememberPosition(item.Log.Uid,
                        MifareClassic.SectorOfBlock(item.Log.Block), item.Log.KeyType,
                        Convert.FromHexString(item.Key.ToString("X12")));
                _lastNonces = fingerprint;
            }
            catch (Exception ex) { ReportError(string.Format(Strings.Cards_CouldNotSaveKnownKeys, ex.Message)); }
        }
        else
        {
            _lastNonces = null;
            SetKeys([]);
        }
    }

    /// <summary>Forget the collection after saving with detection disabled, including pending results.</summary>
    public void Clear()
    {
        _collectionVersion++;
        _lastNonces = null;
        SetCount(0);
        SetKeys([]);
        ClearMessages();
    }

    internal static async Task<IReadOnlyList<(DetectionLog Log, ulong Key)>> RecoverKeysAsync(
        IReadOnlyList<DetectionLog> logs, IKeyRecoveryEngine engine, CancellationToken ct = default)
    {
        var found = new List<(DetectionLog, ulong)>();
        foreach (var group in logs.Where(l => !l.IsNested)
                     .GroupBy(l => (Uid: Convert.ToHexString(l.Uid), l.Block, l.KeyType)))
        {
            var records = group.DistinctBy(l => (Convert.ToHexString(l.Nt),
                Convert.ToHexString(l.Nr), Convert.ToHexString(l.Ar))).ToArray();
            var recovered = false;
            for (var i = 0; i < records.Length && !recovered; i++)
            for (var j = i + 1; j < records.Length && !recovered; j++)
            {
                ct.ThrowIfCancellationRequested();
                var keys = await engine.RecoverAsync(new KeyRecoveryRequest(KeyRecoveryOperation.Mfkey32)
                    { Logs = [records[i], records[j]] }, ct);
                if (keys.Count == 0) continue;
                found.Add((records[i], keys[0]));
                recovered = true;
            }
        }
        return found;
    }

    private void SetCount(int count)
    {
        Count = count;
        this.RaisePropertyChanged(nameof(CountValue));
    }

    private void SetKeys(IReadOnlyList<byte[]> keys, IReadOnlyList<EmulatorCaptureKey>? rows = null)
    {
        _keys = keys;
        KeyRows = rows ?? keys.Select(k => new EmulatorCaptureKey("", Convert.ToHexString(k))).ToArray();
        ResultNote = null;
        this.RaisePropertyChanged(nameof(HasKeys));
        this.RaisePropertyChanged(nameof(KeysText));
        this.RaisePropertyChanged(nameof(Results));
    }

    // Rebuilt when the menu opens, so dictionary edits need no subscription tied to a closed slot editor.
    public void RebuildKeysTargets()
    {
        KeysTargets.Clear();
        foreach (var dictionary in _dictionaries.Dictionaries)
            KeysTargets.Add(new KeysTargetViewModel(new DictionaryItemViewModel(dictionary),
                ReactiveCommand.Create(() => AddKeysTo(dictionary))));
        this.RaisePropertyChanged(nameof(HasKeysTargets));
    }

    private void AddKeysTo(KeyDictionary dictionary)
    {
        if (!HasKeys) return;
        try
        {
            var have = dictionary.Keys.Select(Convert.ToHexString).ToHashSet();
            var added = _keys.Where(k => have.Add(Convert.ToHexString(k))).Select(k => k.ToArray()).ToArray();
            var name = string.IsNullOrWhiteSpace(dictionary.Name) ? Strings.Common_Untitled : dictionary.Name;
            if (added.Length == 0) { Notify(string.Format(Strings.Cards_KeysAlreadyInDictionary, name)); return; }
            dictionary.Keys.AddRange(added);
            _dictionaries.Update(dictionary);
            Notify(string.Format(Strings.Cards_KeysAddedTo, Loc.Plural(Strings.Plural_Keys, added.Length), name));
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private void SaveToNewDictionary()
    {
        if (!HasKeys) return;
        try
        {
            var basis = $"{string.Format(Strings.Slots_SlotN, _slot)} · {(IsMifare ? "mfkey32" : "NTAG")}";
            var name = _dictionaries.FreeName(basis);
            _dictionaries.Add(new KeyDictionary { Name = name, Keys = _keys.Select(k => k.ToArray()).ToList() });
            Notify(string.Format(Strings.Cards_KeysSavedToNewDictionary, Loc.Plural(Strings.Plural_Keys, _keys.Count), name));
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private Task SaveToFileAsync() => RunAsync(async () =>
    {
        if (KeysText is not { } text) return;
        var name = await StorageService.SaveAsync(Strings.Cards_ExportKeysTitle,
            $"slot-{_slot}-{(IsMifare ? "mfkey32" : "ntag")}.dic", Encoding.UTF8.GetBytes(text), "dic");
        if (name is not null) Notify(string.Format(Strings.Common_SavedFile, name));
    });

    public void NotifyKeysCopied() => Notify(string.Format(Strings.Cards_KeysCopied, Loc.Plural(Strings.Plural_Keys, _keys.Count)));
}
