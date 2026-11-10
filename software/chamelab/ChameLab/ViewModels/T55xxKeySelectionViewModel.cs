using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>The same saved T55xx dictionary choices in Tools and the card writer.</summary>
public sealed class T55xxKeySelectionViewModel : ViewModelBase
{
    private readonly DictionaryLibrary _library;
    private readonly List<IDisposable> _subscriptions = [];
    public T55xxKeySelectionViewModel(DictionaryLibrary library)
    {
        _library = library;
        library.Changed += () =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        };
        Refresh();
    }

    public ObservableCollection<DumpKeySourceViewModel> Sources { get; } = [];
    public int Count { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string CountText => Count == 0 ? Strings.Cards_NoKeysSelected : Loc.Plural(Strings.Plural_UniqueKeys, Count);
    public IReadOnlyList<byte[]> BuildKeys() => Sources.Where(s => s.IsSelected)
        .SelectMany(s => s.Keys).DistinctBy(Convert.ToHexString).ToArray();

    /// <summary>Whether the learned card passwords are in play (also prepends the scanned tag's own).</summary>
    public bool IncludeKnownCardKeys => Sources.Any(s => s.IsKnownCardKeys && s.IsSelected);

    public void Refresh()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Sources.Clear();
        // The learned card passwords first, then the built-in keys, then the user's dictionaries (as for MIFARE).
        Add(KnownCardKeys.Id, KnownCardKeys.Name, _library.KnownKeys.AllT55xxPasswords());
        Add("t55xx-built-in", Strings.Common_BuiltInKeys, ChameleonClient.DefaultT55xxOldKeys, separatorAfter: true);
        // T55xx passwords are 4 bytes: those of every dictionary that has some.
        foreach (var dict in _library.Dictionaries.Where(d => d.HasKeysOfLength(4)))
            Add(dict.Id, string.IsNullOrWhiteSpace(dict.Name) ? Strings.Common_Untitled : dict.Name, dict.KeysOfLength(4), dict.Color);
        UpdateCount();
    }

    private void Add(string id, string name, IReadOnlyList<byte[]> keys, string? color = null, bool separatorAfter = false)
    {
        var source = new DumpKeySourceViewModel(id, name, keys, KeySourceSelection.IsSelected(id), color, separatorAfter);
        Sources.Add(source);
        _subscriptions.Add(source.WhenAnyValue(x => x.IsSelected).Skip(1).Subscribe(selected =>
        {
            KeySourceSelection.SetSelected(id, selected);
            UpdateCount();
        }));
    }

    private void UpdateCount()
    {
        Count = BuildKeys().Count;
        this.RaisePropertyChanged(nameof(CountText));
    }
}
