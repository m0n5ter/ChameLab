using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Input;
using ReactiveUI;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;

namespace ChameLab.ViewModels;

/// <summary>
/// The nested folders of a library page (Cards, Dictionaries): the folder shown, the rows of the
/// navigation pane (a folder's folders listed under it while it is expanded), the tiles of the
/// folders in the one shown, the folder pickers, and creating, renaming, moving and deleting folders.
/// The page lists the items of <see cref="Current"/> and rebuilds when <see cref="CurrentChanged"/>.
/// </summary>
public sealed class FolderBrowser : ViewModelBase
{
    private readonly IFolderLibrary _library;
    private readonly string _expandedSetting;
    private readonly Func<string> _rootTitle;
    private readonly Func<int, string> _countText;
    private readonly Func<int, string, string, string> _deletedText;
    private readonly Action<string> _notify;
    private readonly Action<string> _reportError;
    private readonly HashSet<string> _expanded;
    // The folder shown; null shows the items in no folder.
    private string? _current;

    /// <param name="expandedSetting">Where the folders expanded in the navigation pane are remembered.</param>
    /// <param name="rootTitle">The page's own title (Cards), for the items in no folder.</param>
    /// <param name="countText">“3 cards”.</param>
    /// <param name="deletedText">Deleting a folder with <c>{0}</c> items in it, named <c>{1}</c>; they went to <c>{2}</c>.</param>
    public FolderBrowser(IFolderLibrary library, string expandedSetting, Func<string> rootTitle,
        Func<int, string> countText, Func<int, string, string, string> deletedText,
        Action<string> notify, Action<string> reportError)
    {
        _library = library;
        _expandedSetting = expandedSetting;
        _rootTitle = rootTitle;
        _countText = countText;
        _deletedText = deletedText;
        _notify = notify;
        _reportError = reportError;
        _expanded = LoadExpanded();

        NewFolderCommand = ReactiveCommand.Create(() => { if (AddFolder(string.Empty) is { } f) BeginRename(f); });
        OpenNewFolderCommand = ReactiveCommand.Create(OpenNewFolder);
        ShowParentCommand = ReactiveCommand.Create(() => Show(_current is null ? null : FolderPath.Parent(_current)));
        RenameCurrentCommand = ReactiveCommand.Create(BeginRenameCurrent);
        DeleteCurrentCommand = ReactiveCommand.Create(() => { if (CurrentFolder is { } f) AskDelete(f); });
        ConfirmDeleteCommand = ReactiveCommand.Create(ConfirmDelete);
        CancelDeleteCommand = ReactiveCommand.Create(() => { FolderToDelete = null; });
    }

    /// <summary>The folder shown changed; the page lists its items.</summary>
    public event Action? CurrentChanged;

    /// <summary>Every folder, in tree order.</summary>
    public ObservableCollection<FolderViewModel> Folders { get; } = new();

    /// <summary>The folders listed in the navigation pane: those whose folders are all expanded.</summary>
    public ObservableCollection<FolderViewModel> NavFolders { get; } = new();

    /// <summary>The folders in the one shown, listed after its items.</summary>
    public ObservableCollection<FolderViewModel> Subfolders { get; } = new();

    /// <summary>Where an item can be moved: every folder (no folder stands apart, in <see cref="RootOptions"/>).</summary>
    public ObservableCollection<FolderOption> Options { get; } = new();

    /// <summary>Where an item can be moved apart from the folders: no folder.</summary>
    public IReadOnlyList<FolderOption> RootOptions => [new FolderOption(string.Empty, Strings.Cards_NoFolder)];

    /// <summary>Where the open folder can be moved: the top, or a folder outside it.</summary>
    public ObservableCollection<FolderOption> MoveOptions { get; } = new();

    /// <summary>The path of the folder shown; null for the items in no folder.</summary>
    public string? Current => _current;

    /// <summary>The path new items are filed in: the folder shown, or none.</summary>
    public string Location => _current ?? string.Empty;

    public FolderViewModel? CurrentFolder => _current is null ? null : Folders.FirstOrDefault(f => f.Path == _current);

    public bool IsInFolder => _current is not null;

    public bool HasFolders => Folders.Count > 0;

    /// <summary>The folder shown holds folders: their tiles follow its items.</summary>
    public bool ShowsTiles => Subfolders.Count > 0;

    /// <summary>The list's title: the open folder's name, or the page's.</summary>
    public string Title => _current is null ? _rootTitle() : FolderPath.Name(_current);

    /// <summary>The back button's tip: back to the folder that holds the one shown.</summary>
    public string BackTip => string.Format(Strings.Common_BackTo, TitleOf(_current is null ? string.Empty : FolderPath.Parent(_current)));

    /// <summary>A folder's name, or the page's title for no folder.</summary>
    public string TitleOf(string path) => path.Length == 0 ? _rootTitle() : FolderPath.Name(path);

    public string CountText(int count) => _countText(count);

    public ICommand NewFolderCommand { get; }

    /// <summary>The list's New folder: a folder in the one shown, opened with its title being renamed, as the
    /// navigation pane (where <see cref="NewFolderCommand"/> renames it) may be hidden.</summary>
    public ICommand OpenNewFolderCommand { get; }

    /// <summary>Up to the folder that holds the one shown (the list's own back button).</summary>
    public ICommand ShowParentCommand { get; }

    // ---- Showing a folder -------------------------------------------------

    /// <summary>Shows the items of a folder (null or empty for those in none), expanding the folders it is in.</summary>
    public void Show(string? path)
    {
        var found = string.IsNullOrEmpty(path) ? null : _library.FindFolder(path);
        if (found is not null) ExpandAncestors(found);
        if (found == _current) return;
        SetCurrent(found);
        CurrentChanged?.Invoke();
    }

    private void SetCurrent(string? path)
    {
        if (_current == path) return;
        _current = path;
        IsRenamingFolder = false;
        RebuildViews();
    }

    /// <summary>Syncs the folders with the library, in place, so selection and an edit in progress survive.</summary>
    public void Rebuild()
    {
        var paths = _library.Folders;
        // The folder shown is gone (deleted elsewhere): show the nearest folder that holds it.
        if (_current is not null && !paths.Contains(_current))
        {
            var at = _current;
            while (at.Length > 0 && !paths.Contains(at)) at = FolderPath.Parent(at);
            SetCurrent(at.Length == 0 ? null : at);
        }

        var counts = new Dictionary<string, int>();
        foreach (var folder in _library.ItemFolders)
            foreach (var path in FolderPath.SelfAndAncestors(folder))
                counts[path] = counts.GetValueOrDefault(path) + 1;
        var parents = paths.Select(FolderPath.Parent).ToHashSet();

        Sync(Folders, paths.Select(p => Folders.FirstOrDefault(f => f.Path == p) ?? new FolderViewModel(this, p)).ToList());
        foreach (var folder in Folders)
        {
            folder.Count = counts.GetValueOrDefault(folder.Path);
            folder.HasChildren = parents.Contains(folder.Path);
            folder.IsExpanded = _expanded.Contains(folder.Path);
        }

        var options = Folders.Select(f => new FolderOption(f.Path, f.Name, f.Count)).ToList();
        // Rebuilt when a name or a count changed (options compare by path alone).
        if (!options.Select(o => (o.Path, o.Count)).SequenceEqual(Options.Select(o => (o.Path, o.Count))))
        {
            Options.Clear();
            foreach (var option in options) Options.Add(option);
        }
        RebuildViews();
    }

    private void RebuildViews()
    {
        SyncNav();
        var location = Location;
        Sync(Subfolders, Folders.Where(f => FolderPath.Parent(f.Path) == location && f.Path.Length > 0).ToList());
        MoveOptions.Clear();
        if (_current is { } current)
        {
            MoveOptions.Add(new FolderOption(string.Empty, _rootTitle()));
            foreach (var option in Options.Where(o => !FolderPath.IsWithin(o.Path, current))) MoveOptions.Add(option);
        }
        this.RaisePropertyChanged(nameof(Current));
        this.RaisePropertyChanged(nameof(CurrentFolder));
        this.RaisePropertyChanged(nameof(IsInFolder));
        this.RaisePropertyChanged(nameof(HasFolders));
        this.RaisePropertyChanged(nameof(ShowsTiles));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(BackTip));
        this.RaisePropertyChanged(nameof(MoveTarget));
    }

    private void SyncNav() =>
        Sync(NavFolders, Folders.Where(f => FolderPath.SelfAndAncestors(f.Path).SkipLast(1).All(_expanded.Contains)).ToList());

    /// <summary>Makes <paramref name="list"/> hold <paramref name="wanted"/> in that order, moving rows rather than replacing them.</summary>
    private static void Sync<T>(ObservableCollection<T> list, IReadOnlyList<T> wanted) where T : class
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (!wanted.Contains(list[i])) list.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = list.IndexOf(wanted[i]);
            if (at < 0) list.Insert(i, wanted[i]);
            else if (at != i) list.Move(at, i);
        }
    }

    // ---- Expanding folders in the navigation pane -------------------------

    public void SetExpanded(FolderViewModel folder, bool expanded)
    {
        folder.IsExpanded = expanded;
        if (expanded ? _expanded.Add(folder.Path) : _expanded.Remove(folder.Path)) SaveExpanded();
        SyncNav();
    }

    private void ExpandAncestors(string path)
    {
        var changed = false;
        foreach (var at in FolderPath.SelfAndAncestors(path).SkipLast(1))
        {
            changed |= _expanded.Add(at);
            if (Folders.FirstOrDefault(f => f.Path == at) is { } folder) folder.IsExpanded = true;
        }
        if (!changed) return;
        SaveExpanded();
        SyncNav();
    }

    private HashSet<string> LoadExpanded()
    {
        var set = new HashSet<string>();
        if (AppSettings.Get(_expandedSetting) is JsonArray list)
            foreach (var node in list)
                if (node is JsonValue v && v.TryGetValue<string>(out var path)) set.Add(path);
        return set;
    }

    private void SaveExpanded() =>
        AppSettings.Set(_expandedSetting, new JsonArray(_expanded.Order().Select(p => (JsonNode)p).ToArray()));

    // ---- New folders --------------------------------------------------------

    private void OpenNewFolder()
    {
        if (AddFolder(Location) is not { } folder) return;
        Show(folder.Path);
        BeginRenameCurrent();
    }

    /// <summary>A new folder in this one, named in its row.</summary>
    public void NewSubfolder(FolderViewModel parent)
    {
        SetExpanded(parent, true);
        if (AddFolder(parent.Path) is { } folder) BeginRename(folder);
    }

    private FolderViewModel? AddFolder(string parent)
    {
        var name = Strings.Common_NewFolder;
        for (var n = 2; _library.FindFolder(FolderPath.Combine(parent, name)) is not null; n++)
            name = string.Format(Strings.Cards_NewFolderN, n);
        if (_library.AddFolder(FolderPath.Combine(parent, name)) is not { } path) return null;
        // The page rebuilds on the library's change; make sure, should it not have yet.
        if (Folders.All(f => f.Path != path)) Rebuild();
        return Folders.FirstOrDefault(f => f.Path == path);
    }

    // ---- Renaming -----------------------------------------------------------

    public void BeginRename(FolderViewModel folder)
    {
        foreach (var other in Folders.Where(f => f.IsEditing && f != folder)) other.IsEditing = false;
        ExpandAncestors(folder.Path);
        folder.EditName = folder.Name;
        folder.IsEditing = true;
    }

    /// <summary>Ends an edit of a folder's name in its row; a name a folder beside it has is refused.</summary>
    public void CommitRename(FolderViewModel folder)
    {
        if (!folder.IsEditing) return;
        folder.IsEditing = false;
        Rename(folder, folder.EditName);
    }

    public static void CancelRename(FolderViewModel folder) => folder.IsEditing = false;

    private void Rename(FolderViewModel folder, string? newName)
    {
        var old = folder.Path;
        var name = FolderPath.NormalizeName(newName);
        if (name.Length == 0 || name == folder.Name) return;
        var to = FolderPath.Combine(FolderPath.Parent(old), name);
        if (_library.FindFolder(to) is { } taken && !FolderPath.Same(taken, old))
        {
            _reportError(string.Format(Strings.Cards_FolderExists, FolderPath.Name(taken)));
            return;
        }
        // Renamed in place first, so the rows (and the selection on them) are kept.
        Relocate(old, to);
        if (_library.RenameFolder(old, name) is null) Relocate(to, old);
    }

    /// <summary>The rows, the expanded folders and the folder shown follow a folder to its new path.</summary>
    private void Relocate(string old, string to)
    {
        foreach (var f in Folders.Where(f => FolderPath.IsWithin(f.Path, old)).ToList())
            f.Path = FolderPath.Rebase(f.Path, old, to);
        var expanded = _expanded.Where(p => FolderPath.IsWithin(p, old)).ToList();
        foreach (var p in expanded) _expanded.Remove(p);
        foreach (var p in expanded) _expanded.Add(FolderPath.Rebase(p, old, to));
        if (expanded.Count > 0) SaveExpanded();
        if (_current is not null && FolderPath.IsWithin(_current, old)) _current = FolderPath.Rebase(_current, old, to);
        this.RaisePropertyChanged(nameof(Title));
    }

    /// <summary>The open folder's title is a field.</summary>
    public bool IsRenamingFolder
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? FolderRenameText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand RenameCurrentCommand { get; }

    private void BeginRenameCurrent()
    {
        if (CurrentFolder is not { } folder) return;
        FolderRenameText = folder.Name;
        IsRenamingFolder = true;
    }

    public void CommitFolderRename()
    {
        if (!IsRenamingFolder) return;
        IsRenamingFolder = false;
        if (CurrentFolder is { } folder) Rename(folder, FolderRenameText);
    }

    public void CancelFolderRename() => IsRenamingFolder = false;

    // ---- Moving the open folder ---------------------------------------------

    /// <summary>Where the open folder is: picking another place moves it there, with what is in it.</summary>
    public FolderOption? MoveTarget
    {
        get => _current is { } c ? new FolderOption(FolderPath.Parent(c), string.Empty) : null;
        set
        {
            if (value is null || _current is not { } old || FolderPath.Parent(old) == value.Path) return;
            var to = FolderPath.Combine(value.Path, FolderPath.Name(old));
            if (_library.FindFolder(to) is { } taken)
            {
                _reportError(string.Format(Strings.Cards_FolderExists, FolderPath.Name(taken)));
                this.RaisePropertyChanged();
                return;
            }
            Relocate(old, to);
            if (_library.MoveFolder(old, value.Path) is null)
            {
                Relocate(to, old);
                return;
            }
            ExpandAncestors(to);
            _notify(string.Format(Strings.Common_MovedFolder, FolderPath.Name(to), TitleOf(value.Path)));
            CurrentChanged?.Invoke();
        }
    }

    // ---- Deleting -------------------------------------------------------------

    public ICommand DeleteCurrentCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    /// <summary>The folder the delete dialog asks about; null while it is closed.</summary>
    public FolderViewModel? FolderToDelete
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsConfirmingDelete));
            this.RaisePropertyChanged(nameof(DeletePrompt));
            this.RaisePropertyChanged(nameof(DeleteDetail));
        }
    }

    public bool IsConfirmingDelete => FolderToDelete is not null;

    public string DeletePrompt =>
        FolderToDelete is { } folder ? string.Format(Strings.Common_DeleteFolderPrompt, folder.Name) : "";

    /// <summary>Where what is in the folder goes: the folder that holds it.</summary>
    public string DeleteDetail => FolderToDelete is { } folder
        ? string.Format(Strings.Common_FolderContentsMoveTo, TitleOf(FolderPath.Parent(folder.Path)))
        : "";

    public void AskDelete(FolderViewModel folder) => FolderToDelete = folder;

    private void ConfirmDelete()
    {
        if (FolderToDelete is not { } folder) return;
        FolderToDelete = null;
        // Gone already (removed meanwhile): nothing to delete.
        if (Folders.Contains(folder)) Delete(folder);
    }

    /// <summary>Deletes a folder and those in it; what is in them moves to the folder that held it.</summary>
    private void Delete(FolderViewModel folder)
    {
        var path = folder.Path;
        var parent = FolderPath.Parent(path);
        var count = folder.Count;
        var showsIt = _current is not null && FolderPath.IsWithin(_current, path);
        if (showsIt) SetCurrent(parent.Length == 0 ? null : parent);
        _library.RemoveFolder(path);
        if (showsIt) CurrentChanged?.Invoke();
        _notify(count == 0
            ? string.Format(Strings.Cards_DeletedFolder, folder.Name)
            : _deletedText(count, folder.Name, TitleOf(parent)));
    }
}
