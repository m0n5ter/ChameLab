using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChameLab.Models;
using ChameLab.ViewModels;

namespace ChameLab.Controls;

/// <summary>A folder heading in the <see cref="DictionarySelector"/>, with All/None for the dictionaries in it and its folders.</summary>
public sealed class DictionarySelectorFolder(string path, IReadOnlyList<DumpKeySourceViewModel> sources)
{
    public string Name { get; } = FolderPath.Name(path);
    public string Path { get; } = path;
    public Thickness Indent { get; } = new(14 * FolderPath.Depth(path), 6, 0, 0);
    public IReadOnlyList<DumpKeySourceViewModel> Sources { get; } = sources;
}

/// <summary>A dictionary in the <see cref="DictionarySelector"/>, indented under its folder.</summary>
public sealed class DictionarySelectorItem(DumpKeySourceViewModel source)
{
    public DumpKeySourceViewModel Source { get; } = source;
    public Thickness Indent { get; } = new(source.Folder.Length == 0 ? 0 : 14 * (FolderPath.Depth(source.Folder) + 1), 2, 0, 2);
}

/// <summary>The dictionary picker shared by the MIFARE recovery and T55xx panels: a checklist of
/// key sources with All/None shortcuts and the running count of unique keys. Dictionaries in folders
/// are listed under their folders (in tree order, after those in none), each folder with its own All/None.</summary>
public partial class DictionarySelector : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DictionarySelector, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> CountTextProperty =
        AvaloniaProperty.Register<DictionarySelector, string?>(nameof(CountText));

    private INotifyCollectionChanged? _watched;

    public DictionarySelector() => InitializeComponent();

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? CountText
    {
        get => GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    /// <summary>What the list shows: the dictionaries, with a heading for each folder.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsSourceProperty) return;
        if (_watched is not null) _watched.CollectionChanged -= OnSourcesChanged;
        _watched = ItemsSource as INotifyCollectionChanged;
        if (_watched is not null) _watched.CollectionChanged += OnSourcesChanged;
        RebuildRows();
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    private void RebuildRows()
    {
        Rows.Clear();
        var sources = (ItemsSource?.OfType<DumpKeySourceViewModel>() ?? []).ToList();
        // Those in no folder (the known and built-in keys first) keep their order; folders follow in tree order.
        var ordered = sources.OrderBy(s => s.Folder.Length == 0 ? 0 : 1).ThenBy(s => s.Folder, FolderPath.TreeOrder);
        var shown = new HashSet<string>();
        foreach (var source in ordered)
        {
            foreach (var folder in FolderPath.SelfAndAncestors(source.Folder))
                if (shown.Add(folder))
                    Rows.Add(new DictionarySelectorFolder(folder,
                        sources.Where(s => FolderPath.IsWithin(s.Folder, folder)).ToArray()));
            Rows.Add(new DictionarySelectorItem(source));
        }
    }

    private void OnAll(object? sender, RoutedEventArgs e) => Set(ItemsSource?.OfType<DumpKeySourceViewModel>(), true);
    private void OnNone(object? sender, RoutedEventArgs e) => Set(ItemsSource?.OfType<DumpKeySourceViewModel>(), false);

    private void OnFolderAll(object? sender, RoutedEventArgs e) =>
        Set((sender as Control)?.DataContext is DictionarySelectorFolder f ? f.Sources : null, true);

    private void OnFolderNone(object? sender, RoutedEventArgs e) =>
        Set((sender as Control)?.DataContext is DictionarySelectorFolder f ? f.Sources : null, false);

    private static void Set(IEnumerable<DumpKeySourceViewModel>? sources, bool selected)
    {
        foreach (var source in sources ?? []) source.IsSelected = selected;
    }
}
