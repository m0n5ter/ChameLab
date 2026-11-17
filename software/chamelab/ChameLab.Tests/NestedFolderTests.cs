using System.Linq;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

[Collection(AppSettingsCollection.Name)]
public class NestedFolderTests
{
    [Fact]
    public void PathsAreCleanedUpAndNamesNeverHoldTheSeparator()
    {
        Assert.Equal("Work/Doors", FolderPath.Normalize(" Work / Doors "));
        Assert.Equal("Work/Doors", FolderPath.Normalize("/Work//Doors/"));
        Assert.Equal(string.Empty, FolderPath.Normalize(" / "));
        Assert.Equal("In-Out", FolderPath.NormalizeName(" In/Out "));
        Assert.Equal(new string('x', FolderPath.MaxNameLength), FolderPath.NormalizeName(new string('x', 80)));
        Assert.True(FolderPath.IsWithin("Work/Doors", "work"));
        Assert.False(FolderPath.IsWithin("Workshop", "Work"));
        Assert.Equal("Home/Doors/Back", FolderPath.Rebase("Work/Doors/Back", "Work", "Home"));
    }

    [Fact]
    public void FoldersNestAndListInTreeOrder()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.AddFolder("Folder 10");
        library.AddFolder("Work/Doors/Back");
        library.AddFolder("Folder 2/Inside");
        library.AddFolder("work/Gates");

        Assert.Equal(["Folder 2", "Folder 2/Inside", "Folder 10", "Work", "Work/Doors", "Work/Doors/Back", "Work/Gates"],
            library.Folders);
        Assert.Null(library.AddFolder("WORK/doors"));
    }

    [Fact]
    public void OlderNamesWithTheSeparatorLoadAsNestedFolders()
    {
        var store = new MemoryKeyValueStore();
        store.Write("cards", """[{"id":"c1","uid":"01 02 03 04","name":"Office","folder":"Work / Doors"}]""");
        store.Write("cardFolders", """["Work / Doors","Empty"]""");

        var library = new CardLibrary(store);

        Assert.Equal(["Empty", "Work", "Work/Doors"], library.Folders);
        Assert.Equal("Work/Doors", library.Cards[0].Folder);
    }

    [Fact]
    public void RenamingAndMovingRefileWhatIsInside()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        library.Add(new CardSave { Name = "Back door", Folder = "Work/Doors/Back" });
        library.Add(new CardSave { Name = "Front door", Folder = "Work/Doors" });
        library.AddFolder("Home/Doors");

        Assert.Equal("Work/Entrances", library.RenameFolder("Work/Doors", "Entrances"));
        Assert.Equal(["Home", "Home/Doors", "Work", "Work/Entrances", "Work/Entrances/Back"], library.Folders);
        Assert.Equal(["Work/Entrances/Back", "Work/Entrances"], library.Cards.Select(c => c.Folder));

        // A folder beside it has the name; only the spelling of its own may change.
        Assert.Null(library.RenameFolder("Work", "home"));
        Assert.Equal("Work/entrances", library.RenameFolder("Work/Entrances", "entrances"));

        // Not into itself or a folder of its own; not where a folder has its name.
        Assert.Null(library.MoveFolder("Work", "Work/entrances/Back"));
        library.AddFolder("Home/Back");
        Assert.Null(library.MoveFolder("Work/entrances/Back", "Home"));
        library.RemoveFolder("Home/Back");
        Assert.Equal("Home/entrances", library.MoveFolder("Work/entrances", "Home"));
        Assert.Equal(["Home", "Home/Doors", "Home/entrances", "Home/entrances/Back", "Work"], library.Folders);
        Assert.Equal(["Home/entrances/Back", "Home/entrances"], library.Cards.Select(c => c.Folder));
        Assert.Equal("Back", library.MoveFolder("Home/entrances/Back", string.Empty));

        // Saved: a reload sees the same.
        var reloaded = new CardLibrary(store);
        Assert.Equal(library.Folders, reloaded.Folders);
        Assert.Equal(["Back", "Home/entrances"], reloaded.Cards.Select(c => c.Folder));
    }

    [Fact]
    public void DeletingAFolderMovesItsCardsUpALevel()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.Add(new CardSave { Name = "Back door", Folder = "Work/Doors/Back" });
        library.Add(new CardSave { Name = "Front door", Folder = "Work/Doors" });
        library.Add(new CardSave { Name = "Badge", Folder = "Work" });

        library.RemoveFolder("Work/Doors");

        Assert.Equal(["Work"], library.Folders);
        Assert.All(library.Cards, c => Assert.Equal("Work", c.Folder));
    }

    [Fact]
    public void DictionariesKeepTheirFoldersAndEmptyFolders()
    {
        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.Add(new KeyDictionary { Name = "Hotel", Folder = "Travel/Hotels", Keys = [[1, 2, 3, 4, 5, 6]] });
        library.AddFolder("Empty");

        var reloaded = new DictionaryLibrary(store);

        Assert.Equal(["Empty", "Travel", "Travel/Hotels"], reloaded.Folders);
        Assert.Equal("Travel/Hotels", reloaded.Dictionaries[0].Folder);
        Assert.Equal("Travel/Hotels", KeyDictionary.FromJson(reloaded.Dictionaries[0].ToJson()).Folder);

        reloaded.RemoveFolder("Travel");
        Assert.Equal(["Empty"], reloaded.Folders);
        Assert.Equal(string.Empty, reloaded.Dictionaries[0].Folder);
    }

    [Fact]
    public void BackupsKeepEmptyFolders()
    {
        var cards = new CardLibrary(new MemoryKeyValueStore());
        var dicts = new DictionaryLibrary(new MemoryKeyValueStore());
        cards.AddFolder("Work/Doors");
        dicts.AddFolder("Travel");
        var backup = BackupService.Export(cards, dicts);

        var cardsBack = new CardLibrary(new MemoryKeyValueStore());
        var dictsBack = new DictionaryLibrary(new MemoryKeyValueStore());
        BackupService.Import(backup, cardsBack, dictsBack);

        Assert.Equal(["Work", "Work/Doors"], cardsBack.Folders);
        Assert.Equal(["Travel"], dictsBack.Folders);
    }

    [Fact]
    public void DictionariesPageListsTheFolderShownAndFilesNewOnesThere()
    {
        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.Add(new KeyDictionary { Name = "Loose", Keys = [[1, 2, 3, 4, 5, 6]] });
        library.Add(new KeyDictionary { Name = "Hotel", Folder = "Travel/Hotels", Keys = [[1, 2, 3, 4, 5, 7]] });
        var vm = new DictionariesViewModel(library, new CardLibrary(store));

        Assert.Equal(["Loose"], vm.Dictionaries.Select(d => d.Name));
        Assert.Equal(["Travel"], vm.FolderTree.Subfolders.Select(f => f.Name));
        Assert.Equal(1, vm.FolderTree.Subfolders[0].Count);

        vm.ShowFolder(vm.FolderTree.Subfolders[0]);
        Assert.Empty(vm.Dictionaries);
        Assert.Equal(["Hotels"], vm.FolderTree.Subfolders.Select(f => f.Name));
        Assert.False(vm.IsFolderEmpty);

        vm.ShowFolder(vm.FolderTree.Subfolders[0]);
        Assert.Equal("Hotels", vm.FolderTree.Title);
        Assert.Equal(["Hotel"], vm.Dictionaries.Select(d => d.Name));
        // The pane lists the folder shown, the folders it is in expanded.
        Assert.Contains(vm.FolderTree.NavFolders, f => f.Path == "Travel/Hotels");

        vm.NewCommand.Execute(null);
        vm.Form!.Name = "Spa";
        vm.Form.Keys.Write(0, [9, 9, 9, 9, 9, 9]);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Travel/Hotels", library.Dictionaries.Single(d => d.Name == "Spa").Folder);

        vm.FolderTree.ShowParentCommand.Execute(null);
        Assert.Equal("Travel", vm.FolderTree.Current);
    }
}
