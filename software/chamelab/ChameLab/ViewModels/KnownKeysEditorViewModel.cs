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
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>A choice in the type filter: one family, or every card when <see cref="Family"/> is null.</summary>
public sealed record KnownCardFamilyOption(KnownCardFamily? Family)
{
    public string Name => Family?.DisplayName() ?? Strings.Dictionaries_AllCardTypes;
    public override string ToString() => Name;
}

/// <summary>A choice of a card's exact type; null when it isn't known.</summary>
public sealed record KnownCardTagOption(TagSpecificType? Tag)
{
    public string Name => Tag?.DisplayName() ?? Strings.Dictionaries_CardTypeUnknown;
    public override string ToString() => Name;
}

public sealed class KnownCardViewModel : ViewModelBase
{
    private readonly TagSpecificType? _storedTag;
    private readonly KnownCardTagOption _baselineTag;
    private readonly string _baselinePassword;

    public KnownCardViewModel(KnownCard card, CardLibrary cards)
    {
        Family = card.Family;
        Uid = card.Uid;
        _storedTag = card.Tag;
        // Saved cards of another family only share the hex; ones without a type may be this card.
        var saved = cards.Cards.Where(c => Convert.ToHexString(c.Uid) == card.Uid
            && KnownCardFamilies.Of(c.Tag) is var family && (family is null || family == card.Family)).ToArray();
        var names = saved.Select(c => c.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
        CardNames = string.Join(", ", names);
        TagOptions = [new(null), .. card.Family.Tags().Select(t => new KnownCardTagOption(t))];
        // Cards learned before types were kept take the type of a saved card with the same UID.
        var savedTags = saved.Select(c => c.Tag).Where(t => KnownCardFamilies.Of(t) == card.Family).Distinct().ToArray();
        var tag = card.Tag ?? (savedTags.Length == 1 ? savedTags[0] : null);
        _baselineTag = TagOptions.First(o => o.Tag == tag);
        SelectedTag = _baselineTag;
        if (IsClassic)
        {
            Form = new DictionaryEditorViewModel(new KeyDictionary { Name = card.Uid, Keys = card.Keys.ToList() });
            Form.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DictionaryEditorViewModel.KeyCountText))
                    this.RaisePropertyChanged(nameof(SummaryText));
                if (e.PropertyName == nameof(DictionaryEditorViewModel.IsDirty))
                    this.RaisePropertyChanged(nameof(IsDirty));
            };
        }
        _baselinePassword = card.Password is { } pwd ? Hex.Spaced(pwd) : "";
        Password = _baselinePassword;
    }

    public KnownCardFamily Family { get; }
    public string Uid { get; }
    public string DisplayUid => Hex.Spaced(Convert.FromHexString(Uid));
    public string CardNames { get; }
    public bool HasCardNames => CardNames.Length > 0;
    public string CardNamesText => HasCardNames ? $"({CardNames})" : "";

    public bool IsClassic => Family == KnownCardFamily.MifareClassic;
    public bool HasPassword => !IsClassic;

    /// <summary>The exact type when known (“MIFARE Classic 1K”), otherwise the family.</summary>
    public string TypeText => Family switch
    {
        // A T55XX broadcasts another card's format; show both.
        KnownCardFamily.T55xx => SelectedTag.Tag is { } t ? $"T55XX · {t.DisplayName()}" : Family.DisplayName(),
        _ => SelectedTag.Tag?.DisplayName() ?? Family.DisplayName(),
    };

    /// <summary>The type in a few letters, for the list row (“MFC 1K”, “NTAG215”, “EM”).</summary>
    public string ShortTypeText => Family.ShortName(SelectedTag.Tag);

    public IReadOnlyList<KnownCardTagOption> TagOptions { get; }

    public KnownCardTagOption SelectedTag
    {
        get;
        set
        {
            // The list deselects (and writes null) while its items are replaced.
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(TypeText));
            this.RaisePropertyChanged(nameof(ShortTypeText));
            this.RaisePropertyChanged(nameof(IsDirty));
        }
    } = null!;

    /// <summary>MIFARE Classic keys, one per row; null for password cards.</summary>
    public DictionaryEditorViewModel? Form { get; }

    /// <summary>The NTAG / T55XX password, as hex.</summary>
    public string Password
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(PasswordError));
            this.RaisePropertyChanged(nameof(IsDirty));
        }
    } = "";

    public string? PasswordError => !HasPassword || ParsePassword() is not null ? null : Strings.Common_Password4Bytes;

    private byte[]? ParsePassword() => Hex.TryParse(Password, out var pwd) && pwd.Length == 4 ? pwd : null;

    public string SummaryText => IsClassic
        ? Loc.Plural(Strings.Plural_KnownKeys, Form!.Build().Count)
        : Strings.Dictionaries_KnownPassword;

    public bool IsDirty => SelectedTag != _baselineTag
        || (Form?.IsDirty ?? false)
        || (HasPassword && Password != _baselinePassword);

    /// <summary>Needs writing on Save: edited, or a type found in the card library that isn't stored yet.</summary>
    public bool NeedsSave => IsDirty || SelectedTag.Tag != _storedTag;

    public KnownCard Build() => new(Family, Uid, SelectedTag.Tag,
        Form?.Build().Keys ?? (IReadOnlyList<byte[]>)Array.Empty<byte[]>(),
        HasPassword ? ParsePassword() ?? throw new FormatException(
            $"{DisplayUid}: {Strings.Common_Password4Bytes}") : null);
}

/// <summary>A working copy: switching cards keeps edits; only Save writes to the store.</summary>
public sealed class KnownKeysEditorViewModel : ViewModelBase
{
    private readonly KnownCardKeys _known;
    private readonly CardLibrary _cards;
    private readonly List<KnownCardViewModel> _items = new();
    private readonly HashSet<KnownCardId> _deleted = new();
    private string? _filter;
    private KnownCardFamilyOption _family;

    public KnownKeysEditorViewModel(KnownCardKeys known, CardLibrary cards)
    {
        _known = known;
        _cards = cards;
        FamilyOptions = [new(null), .. KnownCardFamilies.All.Select(f => new KnownCardFamilyOption(f))];
        _family = FamilyOptions[0];
        DeleteCardCommand = ReactiveCommand.Create(DeleteSelected,
            this.WhenAnyValue(x => x.Selected).Select(x => x is not null));
        Reset();
    }

    public ObservableCollection<KnownCardViewModel> Cards { get; } = new();

    public KnownCardViewModel? Selected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Filter
    {
        get => _filter;
        set { this.RaiseAndSetIfChanged(ref _filter, value); Rebuild(); }
    }

    public IReadOnlyList<KnownCardFamilyOption> FamilyOptions { get; }

    /// <summary>The card type the list shows; “All types” shows every card.</summary>
    public KnownCardFamilyOption FamilyFilter
    {
        get => _family;
        set
        {
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref _family, value);
            Rebuild();
        }
    }

    public bool IsDirty => _deleted.Count > 0 || _items.Any(i => i.IsDirty);
    public bool NoMatches => Cards.Count == 0 && _items.Count > 0;
    public bool HasNoCards => _items.Count == 0;
    public ICommand DeleteCardCommand { get; }

    private void Rebuild()
    {
        var selected = Selected;
        var search = (_filter ?? "").Trim();
        var uidSearch = search.Replace(" ", "");
        var matches = _items.Where(i => _family.Family is not { } family || i.Family == family)
            .Where(i => i.Uid.Contains(uidSearch, StringComparison.OrdinalIgnoreCase)
                || i.CardNames.Contains(search, StringComparison.OrdinalIgnoreCase)
                || i.TypeText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || i.ShortTypeText.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        Cards.Clear();
        foreach (var item in matches) Cards.Add(item);
        Selected = selected is not null && matches.Contains(selected) ? selected : matches.FirstOrDefault();
        this.RaisePropertyChanged(nameof(NoMatches));
        this.RaisePropertyChanged(nameof(HasNoCards));
    }

    private void DeleteSelected()
    {
        if (Selected is not { } item) return;
        _deleted.Add(new(item.Family, item.Uid));
        _items.Remove(item);
        Rebuild();
        this.RaisePropertyChanged(nameof(IsDirty));
    }

    public void Reset()
    {
        foreach (var item in _items) item.PropertyChanged -= OnItemChanged;
        _items.Clear();
        _deleted.Clear();
        foreach (var card in _known.Cards)
        {
            var item = new KnownCardViewModel(card, _cards);
            item.PropertyChanged += OnItemChanged;
            _items.Add(item);
        }
        Rebuild();
        this.RaisePropertyChanged(nameof(IsDirty));
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KnownCardViewModel.IsDirty))
            this.RaisePropertyChanged(nameof(IsDirty));
    }

    public void Save()
    {
        var changed = _items.Where(i => i.NeedsSave).Select(i => i.Build()).ToArray();
        if (changed.Length > 0 || _deleted.Count > 0) _known.ApplyEdits(changed, _deleted);
        Reset();
    }
}
