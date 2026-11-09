using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class ToolsViewModel
{
    public T55xxKeySelectionViewModel T55xxKeys => T55xxSettings.Keys;
    private DictionaryLibrary _dictionaries = null!;
    private CancellationTokenSource? _t55xxCts;
    private Task _t55xxRun = Task.CompletedTask;

    /// <summary>The recovered password, as bytes; null when the tag has none or nothing was found.</summary>
    private byte[]? _t55xxFoundKeyBytes;

    private void InitT55xx(DictionaryLibrary dictionaries, IObservable<bool> idle)
    {
        _dictionaries = dictionaries;
        T55xxSettings = new(dictionaries);
        OpenT55xxCommand = ReactiveCommand.Create(() => { Tool = SniffTool.T55xx; }, idle);
        TryT55xxCommand = ReactiveCommand.CreateFromTask(StartT55xxAsync,
            this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected, x => x.IsT55xxSupported,
                x => x.IsT55xxRunning,
                (busy, connected, supported, running) => !busy && !running && connected && supported != false)
                .CombineLatest(T55xxSettings.WhenAnyValue(x => x.ReadyToStart, x => x.NewKeyError,
                    (ready, error) => ready && error is null), (available, ready) => available && ready));
        CancelT55xxCommand = ReactiveCommand.Create(() => _t55xxCts?.Cancel(),
            this.WhenAnyValue(x => x.IsT55xxRunning));
        SaveT55xxKeyToNewDictionaryCommand = ReactiveCommand.Create(SaveT55xxKeyToNewDictionary);
        SaveT55xxKeyToFileCommand = ReactiveCommand.CreateFromTask(SaveT55xxKeyToFileAsync);
        dictionaries.Changed += () =>
        {
            if (Dispatcher.UIThread.CheckAccess()) RebuildT55xxKeysTargets();
            else Dispatcher.UIThread.Post(RebuildT55xxKeysTargets);
        };
        RebuildT55xxKeysTargets();
    }

    public ICommand OpenT55xxCommand { get; private set; } = null!;
    public ICommand TryT55xxCommand { get; private set; } = null!;
    public ICommand CancelT55xxCommand { get; private set; } = null!;
    public ICommand SaveT55xxKeyToNewDictionaryCommand { get; private set; } = null!;
    public ICommand SaveT55xxKeyToFileCommand { get; private set; } = null!;
    public ObservableCollection<DumpKeySourceViewModel> T55xxKeySources => T55xxKeys.Sources;

    public bool? IsT55xxSupported
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsT55xxUnsupported));
        }
    }
    public bool IsT55xxUnsupported => IsT55xxSupported == false;
    public bool IsT55xxRunning { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    public T55xxPasswordSettingsViewModel T55xxSettings { get; private set; } = null!;
    public double T55xxProgress { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxProgressText { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxResult { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The tag's original UID, as hex.</summary>
    public string? T55xxUid { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The matching key, as hex (or a note when the tag has no password).</summary>
    public string? T55xxFoundKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The new key that was set, as hex (shown green).</summary>
    public string? T55xxSetKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    // ---- Save the recovered key (as the card editor saves its keys) ----

    /// <summary>The saved dictionaries, each a choice of where to add the recovered key.</summary>
    public ObservableCollection<KeysTargetViewModel> T55xxKeysTargets { get; } = new();
    public bool HasT55xxKeysTargets => T55xxKeysTargets.Count > 0;

    /// <summary>A recovered password is available to save.</summary>
    public bool CanSaveT55xxKey => _t55xxFoundKeyBytes is not null;

    private IReadOnlyList<byte[]> T55xxActionKeys() =>
        _t55xxFoundKeyBytes is { } k ? new[] { k } : Array.Empty<byte[]>();

    private static string T55xxKeysText(IEnumerable<byte[]> keys) =>
        string.Concat(keys.Select(k => Convert.ToHexString(k) + "\n"));

    /// <summary>The recovered key, one hex key per line, for the clipboard; null when there is none.</summary>
    public string? T55xxFoundKeyText() => _t55xxFoundKeyBytes is { } k ? Convert.ToHexString(k) + "\n" : null;

    public void NotifyKeysCopied(int count) =>
        Notify(string.Format(Strings.Cards_KeysCopied, Loc.Plural(Strings.Plural_Keys, count)));

    private static string T55xxDictionaryName(KeyDictionary dict) =>
        string.IsNullOrWhiteSpace(dict.Name) ? Strings.Common_Untitled : dict.Name;

    private void RebuildT55xxKeysTargets()
    {
        T55xxKeysTargets.Clear();
        foreach (var d in _dictionaries.Dictionaries)
        {
            var dict = d;
            T55xxKeysTargets.Add(new KeysTargetViewModel(new DictionaryItemViewModel(dict),
                ReactiveCommand.Create(() => AddT55xxKeyTo(dict))));
        }
        this.RaisePropertyChanged(nameof(HasT55xxKeysTargets));
    }

    private void AddT55xxKeyTo(KeyDictionary dict)
    {
        var keys = T55xxActionKeys();
        if (keys.Count == 0) return;
        var have = dict.Keys.Select(Convert.ToHexString).ToHashSet();
        var added = keys.Where(k => have.Add(Convert.ToHexString(k))).ToList();
        if (added.Count == 0)
        {
            Notify(string.Format(Strings.Cards_KeysAlreadyInDictionary, T55xxDictionaryName(dict)));
            return;
        }
        try { dict.Keys.AddRange(added); _dictionaries.Update(dict); }
        catch (Exception ex) { ReportError(ex.Message); return; }
        Notify(string.Format(Strings.Cards_KeysAddedTo, Loc.Plural(Strings.Plural_Keys, added.Count), T55xxDictionaryName(dict)));
    }

    private void SaveT55xxKeyToNewDictionary()
    {
        var keys = T55xxActionKeys();
        if (keys.Count == 0) return;
        var name = FreeT55xxDictionaryName(Strings.Tools_T55xxPassword);
        var dict = new KeyDictionary { Name = name, Keys = keys.Select(k => k.ToArray()).ToList() };
        try { _dictionaries.Add(dict); }
        catch (Exception ex) { ReportError(ex.Message); return; }
        Notify(string.Format(Strings.Cards_KeysSavedToNewDictionary, Loc.Plural(Strings.Plural_Keys, keys.Count), name));
    }

    /// <summary>The name, or with " (2)", " (3)"… after it when a dictionary already has it.</summary>
    private string FreeT55xxDictionaryName(string name)
    {
        var taken = _dictionaries.Dictionaries.Select(d => d.Name.Trim())
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var free = name;
        for (var n = 2; taken.Contains(free); n++) free = $"{name} ({n})";
        return free;
    }

    private Task SaveT55xxKeyToFileAsync() => RunAsync(async () =>
    {
        var keys = T55xxActionKeys();
        if (keys.Count == 0) return;
        var data = System.Text.Encoding.UTF8.GetBytes(T55xxKeysText(keys));
        var name = await StorageService.SaveAsync(Strings.Cards_ExportKeysTitle, "t55xx-key.dic", data, "dic");
        if (name is not null) Notify(string.Format(Strings.Common_SavedFile, name));
    });

    private void RebuildT55xxKeySources() => T55xxKeys.Refresh();

    private Task StartT55xxAsync()
    {
        if (IsBusy || IsT55xxRunning) return Task.CompletedTask;
        _t55xxRun = RunAsync(async () =>
        {
            var client = _device.Client ?? throw new InvalidOperationException(Strings.Common_ConnectFirst);
            // Either the one password the user knows, or every key from the chosen dictionaries.
            if (T55xxSettings.KnowKey && !T55xxSettings.ReadyToStart) throw new FormatException(Strings.Common_Password4Bytes);
            var keys = T55xxSettings.BuildOldKeys();
            // A new key is only taken when the user chose to set one; otherwise the default is used.
            if (T55xxSettings.NewKeyError is not null) throw new FormatException(T55xxSettings.NewKeyError);
            var target = T55xxSettings.BuildNewKey();
            using var cts = new CancellationTokenSource();
            _t55xxCts = cts;
            IsT55xxRunning = true;
            T55xxResult = null;
            T55xxFoundKey = null;
            _t55xxFoundKeyBytes = null;
            this.RaisePropertyChanged(nameof(CanSaveT55xxKey));
            T55xxUid = null;
            T55xxSetKey = null;
            T55xxProgress = 0;
            T55xxProgressText = string.Format(Strings.Tools_T55xxProgress, 0, keys.Count);
            try
            {
                var result = await T55xxPasswordRecovery.RecoverAsync(client, keys, target, (done, total) =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        T55xxProgress = total == 0 ? 0 : 100.0 * done / total;
                        T55xxProgressText = string.Format(Strings.Tools_T55xxProgress, done, total);
                    }), original => Dispatcher.UIThread.Post(() =>
                        T55xxUid = Hex.Spaced(original.Id)),
                    key => Dispatcher.UIThread.Post(() =>
                    {
                        _t55xxFoundKeyBytes = key;
                        T55xxFoundKey = key is null ? Strings.Tools_T55xxUnprotected : Hex.Spaced(key);
                        this.RaisePropertyChanged(nameof(CanSaveT55xxKey));
                    }), original => _dictionaries.KnownKeys.T55xxPasswordForId(original.Id), cts.Token);
                if (result is not null)
                {
                    try { _dictionaries.KnownKeys.RememberT55xxPassword(result.Card.Id, result.NewKey, result.Card.Type); }
                    catch (Exception ex) { ReportError(string.Format(Strings.Cards_CouldNotSavePassword, ex.Message)); }
                }
                Dispatcher.UIThread.Post(() =>
                {
                    T55xxResult = result is null ? Strings.Tools_T55xxNoMatch : Strings.Tools_T55xxComplete;
                    if (result is not null)
                    {
                        T55xxSetKey = Hex.Spaced(target);
                        T55xxProgress = 100;
                    }
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                T55xxResult = Strings.Tools_T55xxCancelled;
            }
            finally
            {
                _t55xxCts = null;
                IsT55xxRunning = false;
                // The outcome shows in the result area; the progress bar is done, so it goes.
                Dispatcher.UIThread.Post(() => T55xxProgressText = null);
            }
        });
        return _t55xxRun;
    }

    public override async Task OnDeactivatedAsync()
    {
        _t55xxCts?.Cancel();
        await _t55xxRun;
    }
}
