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

    [Fact]
    public void CurrentFolderStaysMarkedAcrossOtherPages()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        cards.AddFolder("Work/Doors");
        var dicts = new DictionaryLibrary(store);
        dicts.AddFolder("Hotel");

        var device = new DeviceService([]);
        var main = new MainViewModel(device, cards, dicts, new FirmwareUpdateService(device, [], new WakeLockService()));
        // At startup the active page is Connect while both library roots are the current folders.
        Assert.True(main.Cards.IsRootCurrent);
        Assert.True(main.Dictionaries.IsRootCurrent);
        Assert.Null(main.SelectedNavLibraryItem);

        main.Cards.FolderTree.Show("Work/Doors");
        main.Dictionaries.FolderTree.Show("Hotel");

        var work = main.Cards.FolderTree.Folders.Single(f => f.Path == "Work");
        var doors = main.Cards.FolderTree.Folders.Single(f => f.Path == "Work/Doors");
        var hotel = main.Dictionaries.FolderTree.Folders.Single(f => f.Path == "Hotel");

        Assert.False(main.Cards.IsRootCurrent);
        Assert.False(main.Dictionaries.IsRootCurrent);
        Assert.False(work.IsCurrent);
        Assert.True(doors.IsCurrent);
        Assert.True(hotel.IsCurrent);

        main.NavigateCommand.Execute(main.Cards);
        Assert.Same(doors, main.SelectedNavLibraryItem);

        main.NavigateCommand.Execute(main.Slots);
        Assert.Null(main.SelectedNavLibraryItem);
        Assert.True(doors.IsCurrent);
        Assert.True(hotel.IsCurrent);

        main.Cards.FolderTree.Show(null);
        Assert.True(main.Cards.IsRootCurrent);
        Assert.False(doors.IsCurrent);
        Assert.True(hotel.IsCurrent);
    }

    [Fact]
    public void ImportingCardsSavesIntoCurrentFolder()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        cards.AddFolder("Work/Doors");
        var dicts = new DictionaryLibrary(store);
        var vm = new CardsViewModel(new DeviceService([]), cards, dicts);
        vm.FolderTree.Show("Work/Doors");

        // Single card JSON carrying a stale folder still saves into the folder currently open.
        var single = new CardSave
        {
            Name = "Imported",
            Tag = ChameLab.Protocol.TagSpecificType.Mifare1024,
            Uid = [1, 2, 3, 4],
            Sak = 8,
            Atqa = [4, 0],
            Folder = "OldFolder",
        };
        vm.AddCommand.Execute(null);
        vm.ApplyImportedCards("card.json", [single]);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Work/Doors", cards.Cards.Single(c => c.Name == "Imported").Folder);

        // Switching folder while reviewing saves into the folder open at Save time.
        vm.FolderTree.Show("Work");
        var shifted = new CardSave
        {
            Name = "Shifted",
            Tag = ChameLab.Protocol.TagSpecificType.Mifare1024,
            Uid = [5, 6, 7, 8],
            Sak = 8,
            Atqa = [4, 0],
        };
        vm.FolderTree.Show("Work/Doors");
        vm.AddCommand.Execute(null);
        vm.ApplyImportedCards("shifted.json", [shifted]);
        vm.FolderTree.Show("Work");
        vm.SaveCommand.Execute(null);
        Assert.Equal("Work", cards.Cards.Single(c => c.Name == "Shifted").Folder);

        // Multi-card import nests under the current folder.
        vm.ApplyImportedCards("batch.json",
        [
            new CardSave { Name = "LooseInWork", Tag = ChameLab.Protocol.TagSpecificType.Mifare1024, Uid = [9, 10, 11, 12] },
            new CardSave { Name = "NestedInWork", Tag = ChameLab.Protocol.TagSpecificType.Mifare1024, Uid = [13, 14, 15, 16], Folder = "Sub" },
        ]);
        Assert.Equal("Work", cards.Cards.Single(c => c.Name == "LooseInWork").Folder);
        Assert.Equal("Work/Sub", cards.Cards.Single(c => c.Name == "NestedInWork").Folder);

        // ChameleonUltraGUI library/folder import also nests under the current folder.
        var gui = new ChameleonGuiLibrary(
        [
            new CardSave { Name = "GuiLoose", Tag = ChameLab.Protocol.TagSpecificType.Mifare1024, Uid = [17, 18, 19, 20] },
            new CardSave { Name = "GuiDoors", Tag = ChameLab.Protocol.TagSpecificType.Mifare1024, Uid = [21, 22, 23, 24], Folder = "Doors" },
        ],
        ["Doors", "EmptyGui"],
        []);
        vm.ApplyImportedGuiLibrary(gui);
        Assert.Equal("Work", cards.Cards.Single(c => c.Name == "GuiLoose").Folder);
        Assert.Equal("Work/Doors", cards.Cards.Single(c => c.Name == "GuiDoors").Folder);
        Assert.Contains("Work/EmptyGui", cards.Folders);
    }

    [Fact]
    public void ImportingDictionariesSavesIntoCurrentFolder()
    {
        var store = new MemoryKeyValueStore();
        var dicts = new DictionaryLibrary(store);
        dicts.AddFolder("Travel/Hotels");
        var vm = new DictionariesViewModel(dicts, new CardLibrary(store));
        vm.FolderTree.Show("Travel/Hotels");

        // Single JSON dictionary with a stale folder saves into the current folder.
        var json = """{"name":"RoomKeys","folder":"StaleFolder","keyLength":12,"keys":[[1,2,3,4,5,6]]}""";
        var parsedSingle = DictionariesViewModel.ParseImportedDictionaries("room.json", System.Text.Encoding.UTF8.GetBytes(json));
        vm.ApplyImportedDictionaries("room.json", parsedSingle);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Travel/Hotels", dicts.Dictionaries.Single(d => d.Name == "RoomKeys").Folder);

        // Importing on root then moving to another folder before Save files it into the open folder.
        vm.FolderTree.Show(null);
        var dicBytes = System.Text.Encoding.UTF8.GetBytes("FFFFFFFFFFFF\nA0A1A2A3A4A5\n");
        var parsedDic = DictionariesViewModel.ParseImportedDictionaries("hotel.dic", dicBytes);
        vm.ApplyImportedDictionaries("hotel.dic", parsedDic);
        vm.FolderTree.Show("Travel");
        vm.SaveCommand.Execute(null);
        Assert.Equal("Travel", dicts.Dictionaries.Single(d => d.Name == "hotel").Folder);

        // Multi-dictionary JSON array nests under the current folder.
        var batchJson = """
        [
          {"name":"BatchLoose","keyLength":12,"keys":[[1,1,1,1,1,1]]},
          {"name":"BatchNested","folder":"Sub","keyLength":12,"keys":[[2,2,2,2,2,2]]}
        ]
        """;
        var parsedBatch = DictionariesViewModel.ParseImportedDictionaries("batch.json", System.Text.Encoding.UTF8.GetBytes(batchJson));
        vm.ApplyImportedDictionaries("batch.json", parsedBatch);
        Assert.Equal("Travel", dicts.Dictionaries.Single(d => d.Name == "BatchLoose").Folder);
        Assert.Equal("Travel/Sub", dicts.Dictionaries.Single(d => d.Name == "BatchNested").Folder);
    }

    [Fact]
    public void DeepNestingScalesIndentStepSoTotalShiftNeverExceeds60()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.AddFolder("L0/L1/L2/L3"); // max Depth = 3
        var browser = new FolderBrowser(library, "testExp", () => "Cards", _ => "", (_, _, _) => "", _ => { }, _ => { });
        browser.Rebuild();

        var f3 = browser.Folders.Single(f => f.Name == "L3");
        Assert.Equal(16, f3.IndentStep);
        Assert.Equal(14 + 48, f3.NavIndent.Left);

        library.AddFolder("L0/L1/L2/L3/L4"); // max Depth = 4 -> 60 / 4 = 15
        browser.Rebuild();
        Assert.Equal(15, f3.IndentStep);
        var f4 = browser.Folders.Single(f => f.Name == "L4");
        Assert.Equal(14 + 60, f4.NavIndent.Left);
        Assert.Equal(60, browser.Options.Single(o => o.Path == f4.Path).Indent.Left);

        library.AddFolder("L0/L1/L2/L3/L4/L5"); // max Depth = 5 -> 60 / 5 = 12
        browser.Rebuild();
        var f5 = browser.Folders.Single(f => f.Name == "L5");
        Assert.Equal(12, f5.IndentStep);
        Assert.Equal(14 + 60, f5.NavIndent.Left);
        Assert.Equal(14 + 12, browser.Folders.Single(f => f.Name == "L1").NavIndent.Left);
        Assert.Equal(60, browser.Options.Single(o => o.Path == f5.Path).Indent.Left);
    }
}
