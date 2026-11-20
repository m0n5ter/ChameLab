using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.ViewModels;

namespace ChameLab.Controls;

/// <summary>A folder heading in the <see cref="CardPicker"/>.</summary>
public sealed class CardPickerFolder(string path)
{
    public string Name { get; } = FolderPath.Name(path);
    public string Path { get; } = path;
    public Thickness Indent { get; } = new(14 * FolderPath.Depth(path), 4, 0, 0);
}

/// <summary>A card in the <see cref="CardPicker"/>, indented under its folder.</summary>
public sealed class CardPickerCard(CardSave card)
{
    public CardItemViewModel Item { get; } = new(card);
    public CardSave Card => Item.Card;
    public Thickness Indent { get; } = new(card.Folder.Length == 0 ? 0 : 14 * (FolderPath.Depth(card.Folder) + 1), 0, 0, 0);
}

/// <summary>
/// Choosing one of the saved cards: a search line over a dense list of cards (color, name, type and UID),
/// those in folders listed under their folders (in tree order, after those in none), as in the
/// dictionary selector. The search keeps the folders that still hold a match, and starts over when
/// <see cref="ItemsSource"/> is set to another list (changes to the same list keep it).
/// </summary>
public partial class CardPicker : UserControl
{
    /// <summary>The cards to choose from (<see cref="CardSave"/>), in the order they are listed in.</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<CardPicker, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<CardSave?> SelectedCardProperty =
        AvaloniaProperty.Register<CardPicker, CardSave?>(nameof(SelectedCard), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Shown when there is no card to choose at all (a search that finds none says so itself).</summary>
    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<CardPicker, string?>(nameof(EmptyText));

    public static readonly StyledProperty<double> MaxListHeightProperty =
        AvaloniaProperty.Register<CardPicker, double>(nameof(MaxListHeight), 360);

    public static readonly DirectProperty<CardPicker, string?> FilterProperty =
        AvaloniaProperty.RegisterDirect<CardPicker, string?>(nameof(Filter), p => p.Filter, (p, v) => p.Filter = v);

    public static readonly DirectProperty<CardPicker, string?> EmptyMessageProperty =
        AvaloniaProperty.RegisterDirect<CardPicker, string?>(nameof(EmptyMessage), p => p.EmptyMessage);

    private INotifyCollectionChanged? _watched;
    private string? _filter;
    private string? _emptyMessage;
    private bool _syncing;

    public CardPicker()
    {
        InitializeComponent();
        List.ContainerPrepared += (_, e) => e.Container.Classes.Set("folder", e.Container.DataContext is CardPickerFolder);
        List.SelectionChanged += (_, _) => OnListSelectionChanged();
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public CardSave? SelectedCard
    {
        get => GetValue(SelectedCardProperty);
        set => SetValue(SelectedCardProperty, value);
    }

    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public double MaxListHeight
    {
        get => GetValue(MaxListHeightProperty);
        set => SetValue(MaxListHeightProperty, value);
    }

    /// <summary>The search line: name, type or UID.</summary>
    public string? Filter
    {
        get => _filter;
        set
        {
            if (!SetAndRaise(FilterProperty, ref _filter, value)) return;
            RebuildRows();
        }
    }

    /// <summary>Why the list is empty, if it is.</summary>
    public string? EmptyMessage
    {
        get => _emptyMessage;
        private set => SetAndRaise(EmptyMessageProperty, ref _emptyMessage, value);
    }

    /// <summary>What the list shows: the cards, with a heading for each folder.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    /// <summary>Puts the caret in the search line (the picker just opened).</summary>
    public void FocusFilter() => Dispatcher.UIThread.Post(() => FilterBox.Focus(), DispatcherPriority.Background);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_watched is not null) _watched.CollectionChanged -= OnSourceChanged;
            _watched = ItemsSource as INotifyCollectionChanged;
            if (_watched is not null) _watched.CollectionChanged += OnSourceChanged;
            Restart();
        }
        else if (change.Property == SelectedCardProperty) SyncSelection();
        else if (change.Property == EmptyTextProperty) UpdateEmptyMessage();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    /// <summary>A new list to choose from: the search starts over.</summary>
    private void Restart()
    {
        SetAndRaise(FilterProperty, ref _filter, null);
        RebuildRows();
    }

    private IReadOnlyList<CardSave> Cards => (ItemsSource?.OfType<CardSave>() ?? []).ToList();

    private void RebuildRows()
    {
        var cards = Cards;
        // A card no longer offered is no longer chosen; picking it again must count as a change.
        if (SelectedCard is { } selected && !cards.Contains(selected)) SelectedCard = null;

        _syncing = true;
        Rows.Clear();
        var matching = cards.Select(c => new CardPickerCard(c)).Where(c => c.Item.Matches(_filter)).ToList();
        // Those in no folder keep their order; folders follow in tree order.
        var ordered = matching.OrderBy(c => c.Card.Folder.Length == 0 ? 0 : 1)
            .ThenBy(c => c.Card.Folder, FolderPath.TreeOrder);
        var shown = new HashSet<string>();
        foreach (var card in ordered)
        {
            foreach (var folder in FolderPath.SelfAndAncestors(card.Card.Folder))
                if (shown.Add(folder)) Rows.Add(new CardPickerFolder(folder));
            Rows.Add(card);
        }
        _syncing = false;
        SyncSelection();
        UpdateEmptyMessage();
    }

    private void UpdateEmptyMessage() => EmptyMessage = Rows.Count > 0 ? null
        : Cards.Count == 0 ? EmptyText : Strings.Dictionaries_NoCardsMatchTheSearch;

    private void SyncSelection()
    {
        _syncing = true;
        List.SelectedItem = SelectedCard is { } card
            ? Rows.OfType<CardPickerCard>().FirstOrDefault(r => ReferenceEquals(r.Card, card))
            : null;
        _syncing = false;
    }

    private void OnListSelectionChanged()
    {
        if (_syncing) return;
        switch (List.SelectedItem)
        {
            case CardPickerCard row:
                SelectedCard = row.Card;
                break;
            case CardPickerFolder:
                // A heading reached from the keyboard: it is not a choice.
                SyncSelection();
                break;
        }
    }
}
