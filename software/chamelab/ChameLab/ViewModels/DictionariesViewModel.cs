using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using ChameLab.Behaviors;
using ChameLab.Controls;
using ChameLab.Models;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>One dictionary in the list.</summary>
public sealed class DictionaryItemViewModel(KeyDictionary dict) : ViewModelBase
{
    public KeyDictionary Dictionary { get; } = dict;
    public string Name => string.IsNullOrWhiteSpace(Dictionary.Name) ? "No name" : Dictionary.Name;
    public string CountLabel => Dictionary.Count == 1 ? "1 key" : $"{Dictionary.Count} keys";
    public IBrush ColorBrush => Color.TryParse(Dictionary.Color, out var c) ? new SolidColorBrush(c) : Brushes.OrangeRed;

    /// <summary>The name contains the search, or (two hex digits or more) one of the keys does.</summary>
    public bool Matches(string? f)
    {
        if (string.IsNullOrWhiteSpace(f) || Name.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
        var hex = f.Replace(" ", string.Empty);
        return hex.Length >= 2 && hex.All(Uri.IsHexDigit)
            && Dictionary.Keys.Any(k => Convert.ToHexString(k).Contains(hex, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Editable working copy of a dictionary: name, colour and keys, edited in a hex editor with one
/// key (6 bytes) per row. <see cref="IsDirty"/> tells whether anything differs from what the
/// editor was opened with.
/// </summary>
public sealed class DictionaryEditorViewModel : ViewModelBase
{
    private const int KeyLength = 6;

    private readonly string _id;
    private readonly string _baseline;
    private readonly List<byte[]> _rows;
    // Rows whose key already appears higher up, with the row it repeats.
    private readonly Dictionary<int, int> _repeats = new();
    private string _name;
    private string _color;
    private int _emptyRows;
    private bool _isDirty;

    public DictionaryEditorViewModel(KeyDictionary source)
    {
        _id = source.Id;
        _name = source.Name;
        _color = source.Color;
        _rows = source.Keys.Select(k => (byte[])k.Clone()).ToList();
        Keys = new HexBuffer(_rows, KeyLength)
        {
            CanResize = true,
            RowLabel = row => (row + 1).ToString(),
            // Repeated keys show in the access-bits colour (see the legend).
            Classify = (row, _) => _repeats.ContainsKey(row) ? HexCellKind.AccessBits : HexCellKind.Data,
            Describe = (row, _) => Describe(row),
        };
        Keys.Changed += (_, _) => { Analyse(); Touch(); };
        Analyse();
        _baseline = Fingerprint();
    }

    public string Name { get => _name; set { this.RaiseAndSetIfChanged(ref _name, value); Touch(); } }

    public string Color
    {
        get => _color;
        set
        {
            // The swatch list deselects (and writes null) when the colour isn't in the palette.
            if (string.IsNullOrWhiteSpace(value)) return;
            this.RaiseAndSetIfChanged(ref _color, value);
            Touch();
        }
    }

    /// <summary>
    /// The keys, one per row. Typing into the empty row at the end adds a key; Enter opens a row,
    /// Delete removes rows.
    /// </summary>
    public HexBuffer Keys { get; }

    /// <summary>“12 keys · 2 repeated · 1 empty”.</summary>
    public string KeyCountText
    {
        get
        {
            var unique = _rows.Count - _repeats.Count - _emptyRows;
            var parts = new List<string> { unique == 1 ? "1 key" : $"{unique} keys" };
            if (_repeats.Count > 0) parts.Add($"{_repeats.Count} repeated");
            if (_emptyRows > 0) parts.Add(_emptyRows == 1 ? "1 empty" : $"{_emptyRows} empty");
            return string.Join(" · ", parts);
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    private string? Describe(int row)
    {
        if (row >= _rows.Count) return "New key: type or paste to add";
        if (_rows[row].Length == 0) return $"Key {row + 1} · empty, dropped on save";
        return _repeats.TryGetValue(row, out var first)
            ? $"Key {row + 1} · repeats key {first + 1}, dropped on save"
            : $"Key {row + 1}";
    }

    private void Analyse()
    {
        _repeats.Clear();
        _emptyRows = 0;
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Length == 0) { _emptyRows++; continue; }
            var hex = Convert.ToHexString(_rows[i]);
            if (seen.TryGetValue(hex, out var first)) _repeats[i] = first;
            else seen[hex] = i;
        }
        this.RaisePropertyChanged(nameof(KeyCountText));
    }

    private void Touch()
    {
        // Called from setters before the constructor sets the baseline.
        if (_baseline is null) return;
        IsDirty = Fingerprint() != _baseline;
    }

    private string Fingerprint() =>
        _name.Trim() + "|" + CardSave.NormalizeColor(_color) + "|"
        + string.Join(",", _rows.Select(r => r.Length == 0 ? "-" : Convert.ToHexString(r)));

    /// <summary>
    /// The dictionary as edited (same id; repeats and empty rows dropped); throws with a
    /// user-facing message when it can't be saved.
    /// </summary>
    public KeyDictionary Build()
    {
        if (string.IsNullOrWhiteSpace(_name)) throw new FormatException("Please enter a name.");
        return new KeyDictionary
        {
            Id = _id,
            Name = _name.Trim(),
            Color = CardSave.NormalizeColor(_color),
            Keys = _rows.Where((r, i) => r.Length > 0 && !_repeats.ContainsKey(i)).Select(r => (byte[])r.Clone()).ToList(),
        };
    }
}

/// <summary>What the right-hand pane of the Dictionaries page shows.</summary>
public enum DictionariesPane { None, New, Dictionary }

/// <summary>Order of the dictionary list; <see cref="Manual"/> is the library's own order, changed by dragging.</summary>
/// <remarks><see cref="Size"/> is largest first, <see cref="SizeAsc"/> smallest first.</remarks>
public enum DictionarySort { Manual, Name, NameDesc, Size, SizeAsc }

public static class DictionarySortExtensions
{
    public static string Label(this DictionarySort s) => s switch
    {
        DictionarySort.Name => "Name A–Z",
        DictionarySort.NameDesc => "Name Z–A",
        DictionarySort.Size => "Most keys",
        DictionarySort.SizeAsc => "Fewest keys",
        _ => "Manual",
    };
}

/// <summary>
/// Key-dictionary library in one place, laid out like the Cards page: the list of dictionaries
/// (search, sort, drag into place) and an editor for the open one (name, colour and keys edited
/// in place, saved from the bar below) with duplicate / export / delete actions. Dumps use the
/// dictionaries (plus the built-in defaults) when recovering a card.
/// </summary>
public sealed class DictionariesViewModel : PageViewModelBase, IReorderableList
{
    private readonly DictionaryLibrary _library;
    private DictionaryItemViewModel? _selected;
    private DictionaryEditorViewModel? _form;
    private DictionariesPane _pane;
    private string? _filter;
    private DictionarySort _sort = LoadSort();
    private string? _formSource;
    private bool _confirmingDelete;
    private bool _rebuilding;

    // Page margins 56 + list 280 + gap 18 + details 420 (see DictionariesView).
    public override double MinContentWidth => 774;

    public DictionariesViewModel(DictionaryLibrary library) : base("Dictionaries", "key")
    {
        _library = library;

        var open = this.WhenAnyValue(x => x.Selected).Select(s => s is not null);
        var hasForm = this.WhenAnyValue(x => x.Form).Select(f => f is not null);

        NewCommand = ReactiveCommand.Create(() => LeaveThen(BeginNew));
        ImportCommand = ReactiveCommand.Create(() => LeaveThen(() => _ = ImportAsync()));
        CloseCommand = ReactiveCommand.Create(() => LeaveThen(ClosePane));
        SaveAndLeaveCommand = ReactiveCommand.Create(() => ResolveLeave(run: Save()));
        DiscardAndLeaveCommand = ReactiveCommand.Create(() => ResolveLeave(run: true));
        StayCommand = ReactiveCommand.Create(() => ResolveLeave(run: false));

        SaveCommand = ReactiveCommand.Create(() => { Save(); }, hasForm);
        DiscardCommand = ReactiveCommand.Create(Discard);
        CancelNewCommand = ReactiveCommand.Create(ClosePane);
        DuplicateCommand = ReactiveCommand.Create(Duplicate, open);
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync, hasForm);
        DeleteCommand = ReactiveCommand.Create(() => { IsConfirmingDelete = true; }, open);
        CancelDeleteCommand = ReactiveCommand.Create(() => { IsConfirmingDelete = false; });
        ConfirmDeleteCommand = ReactiveCommand.Create(ConfirmDelete);
        ResetFiltersCommand = ReactiveCommand.Create(() => { Filter = null; },
            this.WhenAnyValue(x => x.Filter).Select(f => !string.IsNullOrWhiteSpace(f)));

        // Library edits made on the UI thread rebuild synchronously so a new dictionary can be selected right away.
        _library.Changed += () =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Rebuild();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };
        Rebuild();
        if (_library.LoadError is { } err) SetStatus(err, isError: true);
    }

    // ---- List ----------------------------------------------------------

    public ObservableCollection<DictionaryItemViewModel> Dictionaries { get; } = new();

    public bool IsEmpty => _library.Dictionaries.Count == 0;
    public bool NoMatches => !IsEmpty && Dictionaries.Count == 0;
    public string CountText => _library.Dictionaries.Count == 1 ? "1 dictionary" : $"{_library.Dictionaries.Count} dictionaries";
    public bool CanUseFiles => StorageService.IsAvailable;

    public string? Filter
    {
        get => _filter;
        set { this.RaiseAndSetIfChanged(ref _filter, value); Rebuild(); }
    }

    public ICommand ResetFiltersCommand { get; }

    public IReadOnlyList<DictionarySort> SortModes { get; } = Enum.GetValues<DictionarySort>();

    public DictionarySort Sort
    {
        get => _sort;
        set
        {
            if (_sort == value) return;
            this.RaiseAndSetIfChanged(ref _sort, value);
            this.RaisePropertyChanged(nameof(CanReorder));
            SaveSort(value);
            Rebuild();
        }
    }

    /// <summary>Dictionaries can be dragged into place only in the library's own order.</summary>
    public bool CanReorder => _sort == DictionarySort.Manual;

    private const string SortSetting = "dictionariesSort";

    private static DictionarySort LoadSort()
    {
        var saved = AppSettings.GetString(SortSetting);
        return Enum.TryParse<DictionarySort>(saved, out var sort) ? sort : DictionarySort.Name;
    }

    private static void SaveSort(DictionarySort sort)
    {
        AppSettings.Set(SortSetting, sort.ToString());
    }

    IList IReorderableList.ReorderItems => Dictionaries;
    string IReorderableList.ReorderKey(object item) => ((DictionaryItemViewModel)item).Dictionary.Id;
    void IReorderableList.OpenItem(object item) => Selected = (DictionaryItemViewModel)item;

    public void MoveItem(int from, int to)
    {
        if (!CanReorder || from == to || from < 0 || to < 0 || from >= Dictionaries.Count || to >= Dictionaries.Count) return;
        _rebuilding = true;
        try { Dictionaries.Move(from, to); }
        finally { _rebuilding = false; }
        // The list may have dropped its highlight while the item moved.
        this.RaisePropertyChanged(nameof(Selected));
    }

    public void CommitOrder()
    {
        if (CanReorder) _library.Reorder(Dictionaries.Select(d => d.Dictionary.Id).ToList());
    }

    private void Rebuild()
    {
        var selectedId = _selected?.Dictionary.Id;
        _rebuilding = true;
        try
        {
            var items = _library.Dictionaries
                .Select(d => new DictionaryItemViewModel(d))
                .Where(d => d.Matches(_filter));
            items = _sort switch
            {
                DictionarySort.Name => items.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                DictionarySort.NameDesc => items.OrderByDescending(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                DictionarySort.Size => items.OrderByDescending(d => d.Dictionary.Count)
                    .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                DictionarySort.SizeAsc => items.OrderBy(d => d.Dictionary.Count)
                    .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => items,
            };
            Dictionaries.Clear();
            foreach (var d in items) Dictionaries.Add(d);
        }
        finally { _rebuilding = false; }

        var reselect = selectedId is null ? null : Dictionaries.FirstOrDefault(d => d.Dictionary.Id == selectedId);
        if (reselect is not null && _form is not null && _pane == DictionariesPane.Dictionary)
        {
            // Same dictionary, new list item: keep the editor (and any unsaved edits) as they are.
            _selected = reselect;
            this.RaisePropertyChanged(nameof(Selected));
        }
        else if (!ReferenceEquals(reselect, _selected))
        {
            SetSelected(reselect);
        }

        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(NoMatches));
        this.RaisePropertyChanged(nameof(CountText));
    }

    private void SelectById(string id) => SetSelected(Dictionaries.FirstOrDefault(d => d.Dictionary.Id == id));

    /// <summary>The saved dictionary open in the editor; picking another one with unsaved edits asks first.</summary>
    public DictionaryItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(value, _selected)) return;
            // Clearing the list during a rebuild resets the ListBox selection; Rebuild restores it itself.
            if (_rebuilding) return;
            if (HasUnsavedWork)
            {
                LeaveThen(() => SetSelected(value));
                // The list has already moved its highlight; put it back on the open dictionary.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(Selected)));
                return;
            }
            SetSelected(value);
        }
    }

    private void SetSelected(DictionaryItemViewModel? value)
    {
        if (ReferenceEquals(value, _selected)) return;
        this.RaiseAndSetIfChanged(ref _selected, value, nameof(Selected));
        IsConfirmingDelete = false;
        if (value is not null)
        {
            Form = new DictionaryEditorViewModel(value.Dictionary);
            Pane = DictionariesPane.Dictionary;
        }
        else if (_pane == DictionariesPane.Dictionary)
        {
            Form = null;
            Pane = DictionariesPane.None;
        }
    }

    private void ClosePane()
    {
        if (_pane == DictionariesPane.Dictionary) { SetSelected(null); return; }
        Form = null;
        Pane = DictionariesPane.None;
    }

    // ---- Pane ----------------------------------------------------------

    public DictionaryEditorViewModel? Form
    {
        get => _form;
        private set
        {
            if (_form is not null) _form.PropertyChanged -= OnFormPropertyChanged;
            this.RaiseAndSetIfChanged(ref _form, value);
            if (_form is not null) _form.PropertyChanged += OnFormPropertyChanged;
            this.RaisePropertyChanged(nameof(IsDirty));
        }
    }

    private void OnFormPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DictionaryEditorViewModel.IsDirty))
            this.RaisePropertyChanged(nameof(IsDirty));
    }

    public DictionariesPane Pane
    {
        get => _pane;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pane, value);
            this.RaisePropertyChanged(nameof(HasPane));
            this.RaisePropertyChanged(nameof(IsNew));
            this.RaisePropertyChanged(nameof(IsOpen));
            this.RaisePropertyChanged(nameof(IsDirty));
            IsConfirmingDelete = false;
        }
    }

    /// <summary>The right-hand pane is in use; on phones it replaces the list.</summary>
    public bool HasPane => _pane != DictionariesPane.None;

    /// <summary>A new (or imported) dictionary is open; the list is locked until it is saved or discarded.</summary>
    public bool IsNew => _pane == DictionariesPane.New;

    public bool IsOpen => _pane == DictionariesPane.Dictionary;

    /// <summary>A saved dictionary is open and has unsaved edits (drives the save bar).</summary>
    public bool IsDirty => _pane == DictionariesPane.Dictionary && _form?.IsDirty == true;

    /// <summary>Where the new dictionary came from (“New dictionary”, “Imported from x.dic”).</summary>
    public string? FormSource
    {
        get => _formSource;
        private set => this.RaiseAndSetIfChanged(ref _formSource, value);
    }

    public static IReadOnlyList<string> Palette => CardEditorViewModel.Palette;

    public ICommand NewCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand CancelNewCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }

    private void BeginNew() => OpenNew(new KeyDictionary { Name = "New dictionary" }, "New dictionary");

    private void OpenNew(KeyDictionary dict, string source)
    {
        if (_selected is not null) SetSelected(null);
        Form = new DictionaryEditorViewModel(dict);
        FormSource = source;
        Pane = DictionariesPane.New;
    }

    // ---- Leaving unsaved work -------------------------------------------

    private Action? _pendingLeave;

    /// <summary>The open dictionary (saved and edited, or new) would be lost by navigating away.</summary>
    private bool HasUnsavedWork => _pane switch
    {
        DictionariesPane.Dictionary => _form?.IsDirty == true,
        DictionariesPane.New => _form is not null,
        _ => false,
    };

    /// <summary>Runs <paramref name="next"/> now, or holds it until the user saves or discards.</summary>
    private void LeaveThen(Action next)
    {
        if (!HasUnsavedWork) { next(); return; }
        _pendingLeave = next;
        this.RaisePropertyChanged(nameof(IsConfirmingLeave));
        this.RaisePropertyChanged(nameof(LeaveText));
        this.RaisePropertyChanged(nameof(IsAsking));
    }

    private void ResolveLeave(bool run)
    {
        var next = _pendingLeave;
        _pendingLeave = null;
        this.RaisePropertyChanged(nameof(IsConfirmingLeave));
        this.RaisePropertyChanged(nameof(IsAsking));
        if (run) next?.Invoke();
    }

    /// <summary>Asking whether to save before switching dictionary, closing or starting a new one.</summary>
    public bool IsConfirmingLeave => _pendingLeave is not null;

    public string LeaveText => _pane == DictionariesPane.New
        ? "Save the new dictionary before leaving?"
        : $"Save changes to “{_selected?.Name}”?";

    public ICommand SaveAndLeaveCommand { get; }
    public ICommand DiscardAndLeaveCommand { get; }
    public ICommand StayCommand { get; }

    public bool IsConfirmingDelete
    {
        get => _confirmingDelete;
        private set
        {
            this.RaiseAndSetIfChanged(ref _confirmingDelete, value);
            this.RaisePropertyChanged(nameof(IsAsking));
        }
    }

    /// <summary>A question waits for an answer; the editor is dimmed until then.</summary>
    public bool IsAsking => IsConfirmingLeave || IsConfirmingDelete;

    // ---- Save / actions ---------------------------------------------------

    /// <returns>False when the dictionary could not be saved (it stays open).</returns>
    private bool Save()
    {
        if (_form is not { } form) return false;
        KeyDictionary dict;
        try { dict = form.Build(); }
        catch (FormatException ex) { SetStatus(ex.Message, isError: true); return false; }

        var isNew = _pane == DictionariesPane.New;
        try
        {
            if (isNew) _library.Add(dict); else _library.Update(dict);
        }
        catch (Exception ex) { SetStatus($"Could not save: {ex.Message}", isError: true); return false; }

        if (isNew)
        {
            Pane = DictionariesPane.None;
            Form = null;
            SelectById(dict.Id);
            SetStatus($"Created “{dict.Name}” ({dict.Count} keys).");
            return true;
        }

        // Fresh editor so the saved state becomes the new baseline.
        Form = new DictionaryEditorViewModel(dict);
        SetStatus($"Saved “{dict.Name}”.");
        return true;
    }

    private void Discard()
    {
        if (_selected is { } s) Form = new DictionaryEditorViewModel(s.Dictionary);
    }

    /// <summary>The open dictionary as edited (throws with a user-facing message when incomplete).</summary>
    private KeyDictionary? ActionDictionary() => _form?.Build();

    private void Duplicate()
    {
        KeyDictionary? dict;
        try { dict = ActionDictionary(); }
        catch (FormatException ex) { SetStatus(ex.Message, isError: true); return; }
        if (dict is null) return;
        // The copy takes the unsaved edits; the original stays as last saved.
        var copy = dict.Clone();
        copy.Name = dict.Name + " (copy)";
        _library.Add(copy);
        SelectById(copy.Id);
        SetStatus($"Duplicated as “{copy.Name}”.");
    }

    private void ConfirmDelete()
    {
        if (_selected is not { } s) return;
        SetSelected(null);
        _library.Remove(s.Dictionary);
        SetStatus($"Deleted “{s.Name}”.");
    }

    private Task ImportAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync("Import dictionary", "json", "dic", "txt");
        if (picked is not { } file) return;

        KeyDictionary dict;
        var name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        var text = Encoding.UTF8.GetString(file.Data).TrimStart('﻿');
        try
        {
            dict = file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? KeyDictionary.FromJson(text)
                : KeyDictionary.FromText(name, text);
        }
        catch (Exception ex) { throw new FormatException($"{file.Name}: {ex.Message}"); }

        if (dict.Count == 0) throw new FormatException($"{file.Name} has no valid keys.");
        if (string.IsNullOrWhiteSpace(dict.Name)) dict.Name = name;
        // A fresh id, so a re-import never stands in for the saved original.
        dict.Id = Guid.NewGuid().ToString();

        // Reviewed (name, colour, keys) before it is saved, like a new one.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenNew(dict, $"Imported from {file.Name}"));
    });

    private Task ExportAsync() => RunAsync(async () =>
    {
        if (ActionDictionary() is not { } dict) return;
        var safe = string.Concat(dict.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var data = Encoding.UTF8.GetBytes(dict.ToText());
        var name = await StorageService.SaveAsync("Export dictionary (.dic)", $"{safe}.dic", data, "dic");
        if (name is not null) SetStatus($"Saved {name}.");
    });
}
