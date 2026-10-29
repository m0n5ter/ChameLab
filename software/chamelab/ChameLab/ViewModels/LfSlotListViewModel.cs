using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>The ordered LF identities. The first row is the firmware's primary card.</summary>
public sealed class LfSlotListViewModel : ViewModelBase
{
    private readonly SlotSideViewModel _side;
    private readonly CardLibrary _library;
    private readonly Action _changed;
    private readonly Action<CardSave> _openCard;
    private CardSave[] _saved = [];
    private bool _syncing, _supportsCycle;

    public LfSlotListViewModel(SlotSideViewModel side, CardLibrary library, Action changed, Action<CardSave> openCard)
    {
        _side = side;
        _library = library;
        _changed = changed;
        _openCard = openCard;
        Load(false, new LfCardCycle(1, []));
    }

    public ObservableCollection<LfSlotCardViewModel> Cards { get; } = new();
    public bool HasCards => Cards.Count > 0;
    public string ChooseLabel => HasCards ? "Add saved" : "Choose saved";
    public string CountLabel => $"{Cards.Count} / 8";
    public bool CanAdd => !HasCards || (_supportsCycle && Cards.Count < 8 &&
        ChameleonClient.SlotTypeFor(Cards[0].Card.Tag) == TagSpecificType.Em410x);
    public bool IsDirty => !Cards.Select(c => Key(c.Card)).SequenceEqual(_saved.Select(Key));
    private static string Key(CardSave card) => $"{(int)ChameleonClient.SlotTypeFor(card.Tag)}:{Convert.ToHexString(card.Uid)}";

    public void Load(bool supportsCycle, LfCardCycle cycle)
    {
        _supportsCycle = supportsCycle;
        Cards.Clear();
        if (_side.HasCard) Cards.Add(Row(_side.CurrentCard()));
        foreach (var uid in cycle.ExtraIds)
            Cards.Add(Row(new CardSave { Tag = TagSpecificType.Em410x, Uid = uid.ToArray(), Name = $"EM410x {Convert.ToHexString(uid)}" }));
        MarkSaved();
    }

    public bool CanChoose(CardSave card) => CanAdd && card.Sense == TagSenseType.Lf &&
        (!HasCards || (ChameleonClient.SlotTypeFor(card.Tag) == TagSpecificType.Em410x && card.Uid.Length == 5)) &&
        Cards.All(row => Key(row.Card) != Key(card));

    public void Add(CardSave card)
    {
        if (!CanChoose(card)) return;
        Cards.Add(Row(card));
        SyncPrimary();
    }

    public void Remove(LfSlotCardViewModel card)
    {
        if (!Cards.Remove(card)) return;
        SyncPrimary();
    }

    public void Move(LfSlotCardViewModel card, int delta)
    {
        var index = Cards.IndexOf(card);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Cards.Count) return;
        Cards.Move(index, target);
        SyncPrimary();
    }

    private LfSlotCardViewModel Row(CardSave card) => new(card, _library, _openCard, Remove, Move);

    private void SyncPrimary()
    {
        var hadCard = _side.HasCard;
        var mode = _side.ColorMode;
        var color = _side.CustomColor;
        _syncing = true;
        try
        {
            if (!HasCards) _side.ClearCommand.Execute(null);
            else if (Key(Cards[0].Card) != Key(_side.CurrentCard()))
            {
                if (_saved.Length > 0 && Key(Cards[0].Card) == Key(_saved[0])) _side.Discard();
                else _side.Pick(Cards[0].Card);
                // Reordering changes the primary identity, while the slot keeps its LED setting.
                if (hadCard) { _side.ColorMode = mode; _side.CustomColor = color; }
            }
        }
        finally { _syncing = false; }
        Notify();
    }

    public void RefreshPrimary()
    {
        if (_syncing) return;
        if (!_side.HasCard) Cards.Clear();
        else if (Cards.Count == 0) Cards.Add(Row(_side.CurrentCard()));
        else Cards[0] = Row(_side.CurrentCard());
        Notify();
    }

    public void OnLibraryChanged()
    {
        foreach (var row in Cards) row.Refresh();
    }

    public void MarkSaved()
    {
        _saved = Cards.Select(c => c.Card).ToArray();
        Notify();
    }

    public void Discard()
    {
        Cards.Clear();
        foreach (var card in _saved) Cards.Add(Row(card));
        Notify();
    }

    public LfCardCycle Cycle(int bursts)
    {
        var cycle = new LfCardCycle(bursts, Cards.Skip(1).Select(c => c.Card.Uid.ToArray()).ToArray());
        cycle.Encode(); // Validate before any slot writes.
        return cycle;
    }

    private void Notify()
    {
        for (var i = 0; i < Cards.Count; i++) Cards[i].Position(i, Cards.Count);
        foreach (var name in new[] { nameof(HasCards), nameof(ChooseLabel), nameof(CountLabel), nameof(CanAdd), nameof(IsDirty) })
            this.RaisePropertyChanged(name);
        _changed();
    }
}

public sealed class LfSlotCardViewModel : ViewModelBase
{
    private readonly CardLibrary _library;
    private CardSave? _match;
    private CardItemViewModel _item;
    private bool _canMoveUp, _canMoveDown;

    public LfSlotCardViewModel(CardSave card, CardLibrary library, Action<CardSave> open,
        Action<LfSlotCardViewModel> remove, Action<LfSlotCardViewModel, int> move)
    {
        Card = card;
        _library = library;
        _item = new CardItemViewModel(card);
        OpenCommand = ReactiveCommand.Create(() => { if (_match is { } match) open(match); });
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
        MoveUpCommand = ReactiveCommand.Create(() => move(this, -1));
        MoveDownCommand = ReactiveCommand.Create(() => move(this, 1));
        SaveCommand = ReactiveCommand.Create(() => { _library.Add(Card.Clone()); Refresh(); });
        Refresh();
    }

    public CardSave Card { get; }
    public CardItemViewModel Item => _item;
    public bool HasMatch => _match is not null;
    public bool CanMoveUp => _canMoveUp;
    public bool CanMoveDown => _canMoveDown;
    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SaveCommand { get; }

    public void Position(int index, int count)
    {
        this.RaiseAndSetIfChanged(ref _canMoveUp, index > 0, nameof(CanMoveUp));
        this.RaiseAndSetIfChanged(ref _canMoveDown, index < count - 1, nameof(CanMoveDown));
    }

    public void Refresh()
    {
        var candidates = _library.Cards.Where(c => c.Sense == TagSenseType.Lf &&
            ChameleonClient.SlotTypeFor(c.Tag) == ChameleonClient.SlotTypeFor(Card.Tag) && c.Uid.AsSpan().SequenceEqual(Card.Uid)).ToList();
        _match = candidates.FirstOrDefault(c => c.Id == Card.Id || c.Name == Card.Name) ?? candidates.FirstOrDefault();
        _item = new CardItemViewModel(_match ?? Card);
        this.RaisePropertyChanged(nameof(Item));
        this.RaisePropertyChanged(nameof(HasMatch));
    }
}
