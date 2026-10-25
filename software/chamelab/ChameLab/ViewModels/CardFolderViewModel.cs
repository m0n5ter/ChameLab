using System;
using System.Windows.Input;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>A folder of saved cards in the navigation pane; renamed in place.</summary>
public sealed class CardFolderViewModel : ViewModelBase
{
    private string _name;
    private int _count;
    private bool _isEditing;
    private string? _editName;

    public CardFolderViewModel(string name, Action<CardFolderViewModel> open, Action<CardFolderViewModel> rename,
        Action<CardFolderViewModel> delete)
    {
        _name = name;
        OpenCommand = ReactiveCommand.Create(() => open(this));
        RenameCommand = ReactiveCommand.Create(() => rename(this));
        DeleteCommand = ReactiveCommand.Create(() => delete(this));
    }

    /// <summary>Shows the folder's cards (its tile in the Cards list).</summary>
    public ICommand OpenCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    /// <summary>Cards in the folder.</summary>
    public int Count
    {
        get => _count;
        set
        {
            this.RaiseAndSetIfChanged(ref _count, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(CountText));
        }
    }

    public string CountText => _count == 1 ? "1 card" : $"{_count} cards";

    /// <summary>No cards in the folder; its row is shown dimmed.</summary>
    public bool IsEmpty => _count == 0;

    /// <summary>The name is being edited in its row.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    public string? EditName
    {
        get => _editName;
        set => this.RaiseAndSetIfChanged(ref _editName, value);
    }
}

/// <summary>
/// A folder a card can be moved to; an empty name is no folder. Options are the same folder by
/// name alone, whatever their card count says.
/// </summary>
public sealed record CardFolderOption(string Name, int Count = 0)
{
    public string Label => Name.Length == 0 ? "No folder" : Name;

    public bool Equals(CardFolderOption? other) => other is not null && other.Name == Name;
    public override int GetHashCode() => Name.GetHashCode();
}
