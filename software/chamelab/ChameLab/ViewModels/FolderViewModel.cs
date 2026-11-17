using System.Windows.Input;
using Avalonia;
using ReactiveUI;
using ChameLab.Localization;
using ChameLab.Models;

namespace ChameLab.ViewModels;

/// <summary>
/// A folder of saved cards or dictionaries (see <see cref="FolderBrowser"/>): a row of the navigation
/// pane, indented by how deep it is, and a tile in the list of the folder that holds it. Renamed in place.
/// </summary>
public sealed class FolderViewModel : ViewModelBase
{
    private string _path;
    private int _count;

    public FolderViewModel(FolderBrowser owner, string path)
    {
        Owner = owner;
        _path = path;
        OpenCommand = ReactiveCommand.Create(() => owner.Show(Path));
        RenameCommand = ReactiveCommand.Create(() => owner.BeginRename(this));
        DeleteCommand = ReactiveCommand.Create(() => owner.AskDelete(this));
        NewSubfolderCommand = ReactiveCommand.Create(() => owner.NewSubfolder(this));
        ToggleExpandedCommand = ReactiveCommand.Create(() => owner.SetExpanded(this, !IsExpanded));
    }

    /// <summary>The cards' or the dictionaries' folders this one is one of.</summary>
    public FolderBrowser Owner { get; }

    /// <summary>Shows the folder's items (its tile, its row).</summary>
    public ICommand OpenCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand NewSubfolderCommand { get; }
    public ICommand ToggleExpandedCommand { get; }

    /// <summary>Where the folder is: the names from the top (see <see cref="FolderPath"/>).</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (_path == value) return;
            this.RaiseAndSetIfChanged(ref _path, value);
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Depth));
            this.RaisePropertyChanged(nameof(NavIndent));
        }
    }

    /// <summary>The folder's own name.</summary>
    public string Name => FolderPath.Name(_path);

    public int Depth => FolderPath.Depth(_path);

    /// <summary>The navigation pane indents a folder under Cards (or Dictionaries), and further under its own folder.</summary>
    public Thickness NavIndent => new(14 + 16 * Depth, 0, 2, 0);

    /// <summary>Items in the folder and the folders in it.</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            this.RaiseAndSetIfChanged(ref _count, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(CountText));
        }
    }

    public string CountText => Owner.CountText(_count);

    /// <summary>Nothing in the folder; its row is shown dimmed.</summary>
    public bool IsEmpty => _count == 0;

    /// <summary>The folder holds folders: its row has an arrow that shows or hides them.</summary>
    public bool HasChildren
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The folders in it are listed under its row in the navigation pane.</summary>
    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The name is being edited in its row.</summary>
    public bool IsEditing
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? EditName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Language changed: the count is worded again.</summary>
    internal void RefreshText() => this.RaisePropertyChanged(nameof(CountText));
}

/// <summary>
/// A folder something can be moved to; the empty path is no folder. Options are the same folder by
/// path alone, whatever their label or count says.
/// </summary>
public sealed record FolderOption(string Path, string Label, int Count = 0)
{
    public int Depth => Path.Length == 0 ? 0 : FolderPath.Depth(Path);

    /// <summary>No folder: the page itself (Cards, Dictionaries).</summary>
    public bool IsRoot => Path.Length == 0;

    /// <summary>Folders inside others are indented in the picker.</summary>
    public Thickness Indent => new(16 * Depth, 0, 0, 0);

    public bool Equals(FolderOption? other) => other is not null && other.Path == Path;
    public override int GetHashCode() => Path.GetHashCode();
}
