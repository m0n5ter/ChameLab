using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class KnownUidViewModel : ViewModelBase
{
    public KnownUidViewModel(string uid, KnownCardKeys known, CardLibrary cards)
    {
        Uid = uid;
        var names = cards.Cards.Where(c => Convert.ToHexString(c.Uid) == uid)
            .Select(c => c.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
        CardNames = string.Join(", ", names);
        Form = new DictionaryEditorViewModel(new KeyDictionary
        {
            Name = uid, Keys = known.ForUid(Convert.FromHexString(uid)).ToList(),
        });
        Form.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DictionaryEditorViewModel.KeyCountText))
                this.RaisePropertyChanged(nameof(KnownKeyCountText));
        };
    }

    public string Uid { get; }
    public string DisplayUid => Hex.Spaced(Convert.FromHexString(Uid));
    public string CardNames { get; }
    public bool HasCardNames => CardNames.Length > 0;
    public string CardNamesText => HasCardNames ? $"({CardNames})" : "";
    public string KnownKeyCountText
    {
        get
        {
            var count = Form.Build().Count;
            return count == 1 ? "1 known key" : $"{count} known keys";
        }
    }
    public DictionaryEditorViewModel Form { get; }
}

/// <summary>A working copy: switching UID keeps edits; only Save writes to the store.</summary>
public sealed class KnownKeysEditorViewModel : ViewModelBase
{
    private readonly KnownCardKeys _known;
    private readonly CardLibrary _cards;
    private readonly List<KnownUidViewModel> _items = new();
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private KnownUidViewModel? _selected;
    private string? _filter;

    public KnownKeysEditorViewModel(KnownCardKeys known, CardLibrary cards)
    {
        _known = known;
        _cards = cards;
        DeleteUidCommand = ReactiveCommand.Create(DeleteSelected,
            this.WhenAnyValue(x => x.Selected).Select(x => x is not null));
        Reset();
    }

    public ObservableCollection<KnownUidViewModel> Uids { get; } = new();
    public KnownUidViewModel? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }
    public string? Filter
    {
        get => _filter;
        set { this.RaiseAndSetIfChanged(ref _filter, value); Rebuild(); }
    }
    public bool IsDirty => _deleted.Count > 0 || _items.Any(i => i.Form.IsDirty);
    public bool NoMatches => Uids.Count == 0;
    public ICommand DeleteUidCommand { get; }

    private void Rebuild()
    {
        var selected = Selected;
        var search = (_filter ?? "").Trim();
        var uidSearch = search.Replace(" ", "");
        var matches = _items.Where(i => i.Uid.Contains(uidSearch, StringComparison.OrdinalIgnoreCase)
            || i.CardNames.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        Uids.Clear();
        foreach (var item in matches) Uids.Add(item);
        Selected = selected is not null && matches.Contains(selected) ? selected : matches.FirstOrDefault();
        this.RaisePropertyChanged(nameof(NoMatches));
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item) return;
        _deleted.Add(item.Uid);
        _items.Remove(item);
        Rebuild();
        this.RaisePropertyChanged(nameof(IsDirty));
    }

    public void Reset()
    {
        foreach (var item in _items) item.Form.PropertyChanged -= OnFormChanged;
        _items.Clear();
        _deleted.Clear();
        foreach (var uid in _known.Uids)
        {
            var item = new KnownUidViewModel(uid, _known, _cards);
            item.Form.PropertyChanged += OnFormChanged;
            _items.Add(item);
        }
        Rebuild();
        this.RaisePropertyChanged(nameof(IsDirty));
    }

    private void OnFormChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DictionaryEditorViewModel.IsDirty))
            this.RaisePropertyChanged(nameof(IsDirty));
    }

    public void Save()
    {
        var edits = _deleted.ToDictionary(uid => uid, _ => (IReadOnlyList<byte[]>?)null);
        foreach (var item in _items.Where(i => i.Form.IsDirty)) edits[item.Uid] = item.Form.Build().Keys;
        if (edits.Count > 0) _known.ApplyEdits(edits);
        Reset();
    }
}
