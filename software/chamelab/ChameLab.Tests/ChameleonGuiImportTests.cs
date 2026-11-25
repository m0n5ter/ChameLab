using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class ChameleonGuiImportTests
{
    private const string Card1 = """{"id":"c1","uid":"5F 00 62 E5 C2","sak":0,"atqa":[],"ats":[],"name":"Office","tag":100,"color":"#ff5722","data":[],"extra":{},"folderId":"f2"}""";
    private const string Card2 = """{"id":"c2","uid":"01 02 03 04","sak":8,"atqa":[4,0],"ats":[],"name":"Loose","tag":1001,"color":"#2196f3","data":[],"extra":{}}""";
    private const string Card3 = """{"id":"c3","uid":"0A 0B 0C 0D","sak":8,"atqa":[4,0],"ats":[],"name":"Gone","tag":1001,"color":"#2196f3","data":[],"extra":{},"folderId":"missing"}""";
    private const string Work = """{"id":"f1","name":"Work","color":"#ff5722"}""";
    private const string Doors = """{"id":"f2","name":"Doors","color":"#ff5722","parentId":"f1"}""";
    private const string Empty = """{"id":"f3","name":"Empty","color":"#ff5722"}""";
    private const string Dict = """{"id":"d1","name":"FF","color":"#2196f3","keys":[[255,255,255,255,255,255]],"keyLength":12}""";

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SettingsExportKeepsNestedFoldersAsPaths()
    {
        var json = $$"""{"app_theme_color":5,"cards":[{{Card1}},{{Card2}},{{Card3}}],"card_folders":[{{Work}},{{Doors}},{{Empty}}],"dictionaries":[{{Dict}}]}""";
        var lib = ChameleonGuiImport.TryRead(Bytes(json));

        Assert.NotNull(lib);
        Assert.Equal(["Work/Doors", "", ""], lib.Cards.Select(c => c.Folder));
        Assert.Equal(["Work", "Work/Doors", "Empty"], lib.Folders);
        Assert.Equal("FF", Assert.Single(lib.Dictionaries).Name);
    }

    [Fact]
    public void DesktopSharedPreferencesHoldsEntriesAsStrings()
    {
        var root = new JsonObject
        {
            ["flutter.sidebar_expanded"] = true,
            ["flutter.cards"] = new JsonArray(Card1, Card2),
            ["flutter.card_folders"] = new JsonArray(Work, Doors),
            ["flutter.dictionaries"] = new JsonArray(Dict),
        };
        var lib = ChameleonGuiImport.TryRead(Bytes(root.ToJsonString()));

        Assert.NotNull(lib);
        Assert.Equal("Office", lib.Cards[0].Name);
        Assert.Equal("Work/Doors", lib.Cards[0].Folder);
        Assert.Single(lib.Dictionaries);
    }

    [Fact]
    public void FolderExportStartsAtItsTopFolder()
    {
        var doors = Doors.Replace("\"f1\"", "\"outside\"");
        var json = $$"""{"format":"chameleon-ultra-gui-folder","version":1,"rootFolderId":"f2","folders":[{{doors}}],"cards":[{{Card1}}]}""";
        var lib = ChameleonGuiImport.TryRead(Bytes(json));

        Assert.NotNull(lib);
        Assert.Equal("Doors", Assert.Single(lib.Cards).Folder);
        Assert.Equal(["Doors"], lib.Folders);
    }

    [Fact]
    public void OtherFilesAreNotGuiLibraries()
    {
        Assert.Null(ChameleonGuiImport.TryRead(Bytes("""{"format":"chamelab-backup","cards":[]}""")));
        Assert.Null(ChameleonGuiImport.TryRead(Bytes(Card2)));
        Assert.Null(ChameleonGuiImport.TryRead(Bytes("Filetype: Flipper NFC device")));
    }

    [Fact]
    public void MergeFilesCardsImportedBeforeAndSkipsDuplicates()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.Add(CardSave.FromJson(Card1)); // imported earlier, without its folder
        var json = $$"""{"cards":[{{Card1}},{{Card2}}],"card_folders":[{{Work}},{{Doors}},{{Empty}}]}""";
        var lib = ChameleonGuiImport.TryRead(Bytes(json))!;

        var (added, refiled) = library.Merge(lib.Cards, lib.Folders);
        Assert.Equal((1, 1), (added, refiled));
        Assert.Equal(2, library.Cards.Count);
        Assert.Equal("Work/Doors", library.Cards.Single(c => c.Id == "c1").Folder);
        Assert.Equal(["Empty", "Work", "Work/Doors"], library.Folders);

        Assert.Equal((0, 0), library.Merge(lib.Cards, lib.Folders));
        Assert.Equal(2, library.Cards.Count);
    }

    [Fact]
    public void SettingsBackupImportTakesGuiExport()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        var dicts = new DictionaryLibrary(store);
        var json = $$"""{"cards":[{{Card1}},{{Card2}}],"card_folders":[{{Work}},{{Doors}}],"dictionaries":[{{Dict}}]}""";

        Assert.Equal((2, 1), BackupService.Import(Bytes(json), cards, dicts));
        Assert.Equal((0, 0), BackupService.Import(Bytes(json), cards, dicts));
        Assert.Equal(["Work", "Work/Doors"], cards.Folders);
    }

    [Fact]
    public void PlanImportClassifiesExactPartialAndNewAcrossBothFormats()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        var dicts = new DictionaryLibrary(store);

        // Existing items: one exact match (c1), one partial match by Id (c2 modified), one partial dict by Name (d1 with different key).
        var c1 = CardSave.FromJson(Card1);
        c1.Folder = "Work/Doors";
        var c2 = CardSave.FromJson(Card2);
        c2.Name = "Loose (local)";
        cards.AddRange([c1, c2]);

        var d1 = KeyDictionary.FromJson(Dict);
        d1.Id = "different-id";
        d1.Keys.Add([0, 0, 0, 0, 0, 0]);
        dicts.Add(d1);

        var json = $$"""{"cards":[{{Card1}},{{Card2}},{{Card3}}],"card_folders":[{{Work}},{{Doors}}],"dictionaries":[{{Dict}}]}""";
        var plan = BackupService.PlanImport(Bytes(json), cards, dicts);

        Assert.True(plan.HasConflicts);
        Assert.Equal(1, plan.SkippedCards);
        Assert.Single(plan.NewCards);
        Assert.Equal("c3", plan.NewCards[0].Id);
        Assert.Single(plan.CardConflicts);
        Assert.Equal("c2", plan.CardConflicts[0].Imported.Id);
        Assert.Single(plan.DictionaryConflicts);
        Assert.Equal("FF", plan.DictionaryConflicts[0].Imported.Name);

        // Overwrite c2, KeepBoth for d1.
        plan.CardConflicts[0].Resolution = BackupConflictResolution.Overwrite;
        plan.DictionaryConflicts[0].Resolution = BackupConflictResolution.KeepBoth;

        var (addedCards, addedDicts) = BackupService.ApplyImport(plan, cards, dicts);
        Assert.Equal((2, 1), (addedCards, addedDicts));
        Assert.Equal(3, cards.Cards.Count);
        Assert.Equal("Loose", cards.Cards.Single(c => c.Id == "c2").Name);
        Assert.Equal(2, dicts.Dictionaries.Count);
    }

    [Fact]
    public void PlanImportSkipsExactMatchesAndResolvesSkipOnChameLabBackup()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        var dicts = new DictionaryLibrary(store);

        var c2 = CardSave.FromJson(Card2);
        cards.Add(c2);
        var d1 = KeyDictionary.FromJson(Dict);
        dicts.Add(d1);

        var backup = BackupService.Export(cards, dicts);

        // Mutate local c2 so it's a partial match against the backup.
        c2.Name = "Changed";
        cards.Update(c2);

        var plan = BackupService.PlanImport(backup, cards, dicts);
        Assert.True(plan.HasConflicts);
        Assert.Equal(0, plan.SkippedCards);
        Assert.Equal(1, plan.SkippedDictionaries);
        Assert.Single(plan.CardConflicts);
        Assert.Empty(plan.DictionaryConflicts);

        // Default resolution is Skip.
        var (addedCards, addedDicts) = BackupService.ApplyImport(plan, cards, dicts);
        Assert.Equal((0, 0), (addedCards, addedDicts));
        Assert.Equal("Changed", cards.Cards.Single(c => c.Id == "c2").Name);
    }
}
