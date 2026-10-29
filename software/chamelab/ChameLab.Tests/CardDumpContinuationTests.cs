using System;
using System.Linq;
using System.Reflection;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class CardDumpContinuationTests
{
    private static CardSave Card(TagSpecificType tag = TagSpecificType.Mifare1024) => new()
    {
        Name = "Saved card", Tag = tag, Uid = [1, 2, 3, 4], Sak = 8, Atqa = [4, 0],
    };

    private static CardsViewModel Open(CardSave card)
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        library.Add(card);
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        vm.OpenCard(card.Id);
        return vm;
    }

    private static MifareDump Dump(CardsViewModel vm) => (MifareDump)typeof(CardsViewModel)
        .GetField("_classicDump", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    [Fact]
    public void NewClassicCardOffersKeysAndCheckUntilRecoveryIsChecked()
    {
        var vm = Open(Card());
        typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.Pane))!.SetValue(vm, CardsPane.Add);

        Assert.True(vm.CanDump);
        Assert.True(vm.ShowDumpActions);
        Assert.True(vm.ShowCheckRecovery);
        Assert.False(vm.ShowDarkside);
        Assert.False(vm.ShowNested);
        Assert.False(vm.ShowStaticNested);
        Assert.False(vm.ShowStaticEncryptedNested);
        Assert.False(vm.ShowHardnested);
        Assert.False(vm.ShowMfkey32);

        typeof(CardsViewModel).GetField("_cardPrng", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, MifareClassicPrngType.Weak);
        typeof(CardsViewModel).GetField("_darksideAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, true);
        Assert.False(vm.ShowCheckRecovery);
        Assert.True(vm.ShowDarkside);
        Assert.True(vm.ShowStaticEncryptedNested);
        Assert.True(vm.ShowMfkey32);

        // Replacing the identity drops the previous card's recovery results.
        typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.Form))!
            .SetValue(vm, new CardEditorViewModel(Card(TagSpecificType.Mifare4096)));
        Assert.True(vm.ShowCheckRecovery);
        Assert.False(vm.ShowDarkside);
        Assert.False(vm.ShowStaticEncryptedNested);
        Assert.False(vm.ShowMfkey32);
    }

    [Theory]
    [InlineData(TagSpecificType.MifareMini, 20, 5)]
    [InlineData(TagSpecificType.Mifare1024, 64, 16)]
    [InlineData(TagSpecificType.Mifare2048, 128, 32)]
    [InlineData(TagSpecificType.Mifare4096, 256, 40)]
    public void SavedPartialCardRestoresMemoryAndOffersContinuation(TagSpecificType tag, int blocks, int sectors)
    {
        var card = Card(tag);
        card.Data = Enumerable.Range(0, blocks).Select(_ => new byte[16]).ToList();
        card.Data[blocks - 2] = [];
        var vm = Open(card);

        Assert.True(vm.CanDump);
        Assert.False(vm.IsDumpComplete);
        Assert.True(vm.ShowCheckRecovery);
        Assert.Equal(sectors, vm.DumpSectors.Count);
        Assert.All(vm.DumpSectors.Take(sectors - 1), sector => Assert.True(sector.IsRead));
        Assert.False(vm.DumpSectors[sectors - 1].IsRead);
        Assert.All(vm.DumpSectors, sector => Assert.Equal(KeyCellState.Unknown, sector.KeyAState));
        Assert.Equal(blocks - 1, Dump(vm).RecoveredBlocks);
        Assert.Equal(card.Data[0], Dump(vm).GetBlock(0));
        Assert.Null(Dump(vm).GetBlock(blocks - 2));
        // Zero bytes in saved key positions can mean unread keys; they need authentication.
        Assert.All(Dump(vm).KeyA, key => Assert.Null(key));
    }

    [Fact]
    public void CompletingSectorUpdatesReadStateWithoutAuthenticatingKeys()
    {
        var dump = new MifareDump(MifareClassicType.Classic4K);
        var sector = new DumpSectorRow(32);
        for (var block = sector.FirstBlock; block < sector.LastBlock; block++)
            dump.SetBlock(block, new byte[16]);
        sector.UpdateReadState(dump);
        Assert.False(sector.IsRead);
        dump.SetBlock(sector.LastBlock, new byte[16]);
        sector.UpdateReadState(dump);
        Assert.True(sector.IsRead);
        Assert.Equal(KeyCellState.Unknown, sector.KeyAState);
        Assert.Equal(KeyCellState.Unknown, sector.KeyBState);
    }

    [Theory]
    [InlineData("AABBCCDDEEFF", "123456789ABC")]
    [InlineData("000000000000", "FFFFFFFFFFFF")]
    public void SavedSectorTooltipShowsStoredKeysWithoutAuthenticating(string keyA, string keyB)
    {
        var card = Card();
        card.Data = Enumerable.Range(0, 64).Select(_ => Array.Empty<byte>()).ToList();
        for (var block = 0; block < 4; block++) card.Data[block] = new byte[16];
        card.Data[3] = Convert.FromHexString(keyA + "FF078069" + keyB);
        var vm = Open(card);
        var sector = vm.DumpSectors[0];
        Assert.True(sector.IsRead);
        Assert.Equal(keyA, sector.KeyAText);
        Assert.Equal(keyB, sector.KeyBText);
        Assert.Equal(KeyCellState.Unknown, sector.KeyAState);
        Assert.Equal(KeyCellState.Unknown, sector.KeyBState);
        Assert.Null(Dump(vm).KeyA[0]);
        Assert.Null(Dump(vm).KeyB[0]);
        Assert.Equal("—", vm.DumpSectors[1].KeyAText);
        Assert.Equal("—", vm.DumpSectors[1].KeyBText);
    }

    [Fact]
    public void SelectingSectorShowsOnlyItsBlocksAndKeepsEditsWhenSwitching()
    {
        var editor = new CardEditorViewModel(Card(TagSpecificType.Mifare4096));
        Assert.False(editor.IsDirty);
        Assert.True(editor.HasData);
        editor.SelectedSector = 32;
        var sector = editor.SelectedMemory!;
        Assert.Equal(16, sector.RowCount);
        Assert.Equal("128", sector.RowLabel(0));
        Assert.False(editor.IsDirty);
        sector.Write(0, [0xAB]);
        Assert.True(editor.IsDirty);
        editor.SelectedSector = 1;
        Assert.Equal(4, editor.SelectedMemory!.RowCount);
        Assert.Equal("004", editor.SelectedMemory.RowLabel(0));
        editor.SelectedSector = 32;
        Assert.Equal(0xAB, editor.SelectedMemory![0]);
        Assert.Equal(0xAB, editor.Build().Data[128][0]);
        Assert.Empty(editor.Build().Data[127]);
        Assert.True(editor.SelectedMemory.Undo());
        Assert.False(editor.IsDirty);
        Assert.True(editor.HasData);
    }

    [Theory]
    [InlineData(TagSpecificType.Mifare1024, 64, 16)]
    [InlineData(TagSpecificType.Ntag213, 45, 4)]
    public void EmptyCardGetsBlankMemoryWithoutPretendingItWasRead(TagSpecificType tag, int count, int size)
    {
        var editor = new CardEditorViewModel(Card(tag));
        Assert.Equal(count, editor.Memory!.RowCount);
        Assert.Equal(size, editor.Memory.RowLength);
        Assert.Empty(editor.Build().Data);
        Assert.False(editor.IsDirty);
        editor.SelectedMemory!.Write(1, [0xAB]);
        Assert.Equal(0xAB, editor.Build().Data[0][1]);
        Assert.Empty(editor.Build().Data[1]);
        Assert.True(editor.SelectedMemory.Undo());
        Assert.Empty(editor.Build().Data);
        // Saved untouched, it reopens as the same blank memory instead of as read sectors.
        var reopened = new CardEditorViewModel(editor.Build(), restrictType: true);
        Assert.Equal(count, reopened.Memory!.RowCount);
        Assert.Empty(reopened.Build().Data);
    }

    [Fact]
    public void DumpThatReadNothingSavesAsUnread()
    {
        var editor = new CardEditorViewModel(Card());
        var dump = editor.Build().ToMifareDump();
        editor.ReplaceContent(CardFormats.FromMifareDump(dump));
        Assert.Equal("0/16 sectors", editor.DataSummary);
        var saved = editor.Build();
        Assert.Equal(64, saved.Data.Count);
        Assert.All(saved.Data, row => Assert.Empty(row));
        Assert.All(new CardEditorViewModel(saved, restrictType: true).Build().Data, row => Assert.Empty(row));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearOnlySelectedSectorAndUndoRestoresIt(bool generated)
    {
        var card = Card(TagSpecificType.Mifare4096);
        if (!generated)
            card.Data = Enumerable.Range(0, 256).Select(MifareClassic.BlankBlock).ToList();
        var editor = new CardEditorViewModel(card);
        // Untouched generated memory is saved as no memory at all.
        static byte[] Row(CardSave c, int block) => block < c.Data.Count ? c.Data[block] : [];
        var before = editor.Build();
        editor.SelectedSector = 32;
        editor.ClearSectorCommand.Execute(null);
        var cleared = editor.Build();
        Assert.True(editor.IsDirty);
        for (var block = 0; block < 256; block++)
        {
            if (block >= 128 && block < 144) Assert.Empty(Row(cleared, block));
            else Assert.Equal(Row(before, block), Row(cleared, block));
        }
        Assert.True(editor.SelectedMemory!.Undo());
        Assert.False(editor.IsDirty);
        Assert.Equal(Row(before, 128), Row(editor.Build(), 128));
        Assert.Equal(Row(before, 143), Row(editor.Build(), 143));
    }

    [Fact]
    public void DumpToolbarAndNoticeHideWhenReadingCompletes()
    {
        var vm = Open(Card());
        void Set(string name, object? value) => typeof(CardsViewModel).GetProperty(name)!.SetValue(vm, value);
        Assert.True(vm.ShowDumpNotice);
        Assert.Contains("Connect a device", vm.DumpNotice);
        Assert.True(vm.ShowDumpActions);
        Set(nameof(CardsViewModel.IsConnected), true);
        Assert.True(vm.ShowDumpActions);
        Assert.False(vm.ShowDumpNotice);
        Set(nameof(CardsViewModel.DumpProgressText), "Reading…");
        Assert.True(vm.IsDumpRunning);
        Assert.False(vm.ShowDumpActions);
        Assert.False(vm.ShowDumpNotice);
        Set(nameof(CardsViewModel.IsDumpComplete), true);
        Assert.True(vm.IsDumpRunning);
        Assert.False(vm.ShowDumpNotice);
        Set(nameof(CardsViewModel.DumpProgressText), null);
        Assert.False(vm.IsDumpRunning);
        Assert.False(vm.ShowDumpActions);
        Assert.False(vm.ShowRecoveryTools);
        Assert.False(vm.ShowDumpNotice);
        Set(nameof(CardsViewModel.IsConnected), false);
        Assert.False(vm.ShowDumpNotice);
        Set(nameof(CardsViewModel.IsDumpComplete), false);
        Assert.True(vm.ShowDumpActions);
        Assert.True(vm.ShowRecoveryTools);
        Assert.Contains("Connect a device", vm.DumpNotice);
    }

    [Fact]
    public void SavedUidOnlyCardCanBeRead()
    {
        var vm = Open(Card());
        Assert.True(vm.CanDump);
        Assert.False(vm.IsDumpComplete);
        Assert.Equal(0, Dump(vm).RecoveredBlocks);
    }

    [Fact]
    public void OpeningAndClosingClassicCardNotifiesRecoveryPanelVisibility()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        var card = Card();
        library.Add(card);
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        var visibilityChanged = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardsViewModel.ShowRecoveryTools)) visibilityChanged++;
        };
        Assert.False(vm.ShowRecoveryTools);
        vm.OpenCard(card.Id);
        Assert.True(vm.ShowRecoveryTools);
        Assert.True(visibilityChanged > 0);
        visibilityChanged = 0;
        vm.CloseCommand.Execute(null);
        Assert.False(vm.ShowRecoveryTools);
        Assert.True(visibilityChanged > 0);
    }

    [Fact]
    public void ManualCardCanBeReadOnceIdentityIsEntered()
    {
        var store = new MemoryKeyValueStore();
        var vm = new CardsViewModel(new DeviceService([]), new CardLibrary(store), new DictionaryLibrary(store));
        vm.AddCommand.Execute(null);
        vm.ManualCommand.Execute(null);
        Assert.True(vm.CanDump);
        vm.Form!.Uid = "01020304";
        Assert.True(vm.ShowCheckRecovery);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Dump(vm).Uid);
    }

    [Theory]
    [InlineData(TagSpecificType.Mifare1024, 64, 16)]
    [InlineData(TagSpecificType.Ntag213, 45, 4)]
    public void CompleteMemoryHidesAttackPanelAndNotice(TagSpecificType tag, int count, int size)
    {
        var card = Card(tag);
        card.Data = Enumerable.Range(0, count).Select(_ => new byte[size]).ToList();
        var vm = Open(card);
        Assert.True(vm.IsDumpComplete);
        Assert.False(vm.ShowCheckRecovery);
        Assert.False(vm.ShowDumpActions);
        Assert.False(vm.ShowRecoveryTools);
        Assert.False(vm.ShowDumpNotice);
    }

    [Fact]
    public void SwitchingCardsClearsPreviousDump()
    {
        var vm = Open(Card());
        vm.CloseCommand.Execute(null);
        Assert.False(vm.CanDump);
        Assert.False(vm.ShowCheckRecovery);
        Assert.Empty(vm.DumpSectors);
    }

    [Fact]
    public void ReplacingReadContentKeepsSavedCardIdentityAndFolder()
    {
        var card = Card();
        card.Folder = "Office";
        var editor = new CardEditorViewModel(card);
        var read = Card();
        read.Data = [new byte[16]];
        editor.ReplaceContent(read);
        var result = editor.Build();
        Assert.Equal(card.Id, result.Id);
        Assert.Equal("Office", result.Folder);
        Assert.Equal(card.Name, result.Name);
        Assert.True(editor.IsDirty);
    }
}
