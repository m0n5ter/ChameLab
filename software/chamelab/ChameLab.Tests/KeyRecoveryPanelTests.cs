using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

[Collection(AppSettingsCollection.Name)]
public class KeyRecoveryPanelTests
{
    [Fact]
    public void DictionaryChangesMergeAcrossWindowsAndPreserveUnavailableDictionaries()
    {
        const string setting = "dumpUncheckedDictionaries";
        var original = AppSettings.Get(setting);
        try
        {
            var store = new MemoryKeyValueStore();
            var dictionaries = new DictionaryLibrary(store);
            var first = new KeyDictionary { Name = "First" };
            var second = new KeyDictionary { Name = "Second" };
            dictionaries.AddRange([first, second]);
            CardsViewModel Window(DictionaryLibrary library) =>
                new(new DeviceService([]), new CardLibrary(store), library);
            var window = Window(dictionaries);
            var otherWindow = Window(dictionaries);

            window.DumpKeySources.Single(s => s.Id == first.Id).IsSelected = false;
            otherWindow.DumpKeySources.Single(s => s.Id == second.Id).IsSelected = false;
            // A temporarily empty library must not prune either saved choice.
            var emptyWindow = Window(new DictionaryLibrary(new MemoryKeyValueStore()));
            var builtIn = emptyWindow.DumpKeySources.Single(s => s.Id == DumpKeySourceViewModel.BuiltInId);
            builtIn.IsSelected = !builtIn.IsSelected;

            var reloaded = Window(new DictionaryLibrary(store));
            Assert.False(reloaded.DumpKeySources.Single(s => s.Id == first.Id).IsSelected);
            Assert.False(reloaded.DumpKeySources.Single(s => s.Id == second.Id).IsSelected);
            var saved = System.Text.Json.Nodes.JsonNode.Parse(AppServices.Store.Read("settings")!)!;
            var ids = saved[setting]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.Contains(first.Id, ids);
            Assert.Contains(second.Id, ids);
        }
        finally { AppSettings.Set(setting, original); }
    }

    [Fact]
    public void KnownKeySelectionIsSharedAndSurvivesResetAndRecreation()
    {
        const string setting = "dumpUncheckedDictionaries";
        var original = AppSettings.Get(setting);
        try
        {
            var store = new MemoryKeyValueStore();
            var library = new DictionaryLibrary(store);
            CardsViewModel Window() => new(new DeviceService([]), new CardLibrary(store), library);
            var vm = Window();
            vm.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected = true;
            vm.WriteRecovery.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected = false;
            Assert.False(vm.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected);
            vm.WriteRecovery.Reset();
            Assert.False(vm.WriteRecovery.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected);
            var reloaded = Window();
            Assert.False(reloaded.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected);
            Assert.False(reloaded.WriteRecovery.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected);
            vm.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected = true;
            Assert.True(vm.WriteRecovery.DumpKeySources.Single(s => s.IsKnownCardKeys).IsSelected);
        }
        finally { AppSettings.Set(setting, original); }
    }

    [Fact]
    public void DetachedSourcesCannotOverwriteSelectionAfterListRebuild()
    {
        const string setting = "dumpUncheckedDictionaries";
        var original = AppSettings.Get(setting);
        try
        {
            var store = new MemoryKeyValueStore();
            var library = new DictionaryLibrary(store);
            var dictionary = new KeyDictionary { Name = "Test" };
            library.Add(dictionary);
            var vm = new CardsViewModel(new DeviceService([]), new CardLibrary(store), library);
            var old = vm.DumpKeySources.Single(s => s.Id == dictionary.Id);
            old.IsSelected = false;
            // Rebuild synchronously, just as opening a different card does.
            typeof(CardsViewModel).GetMethod("RebuildKeySources",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
            old.IsSelected = true;
            var reloaded = new CardsViewModel(new DeviceService([]), new CardLibrary(store), library);
            Assert.False(reloaded.DumpKeySources.Single(s => s.Id == dictionary.Id).IsSelected);
        }
        finally { AppSettings.Set(setting, original); }
    }

    [Fact]
    public void ActiveSectorDoesNotSwitchToPartialBetweenEveryBlock()
    {
        var row = new DumpSectorRow(0);
        row.BeginWrite([0, 1]);
        row.ApplyWrite(new MifareBlockProgress(0, 1, MifareBlockState.Reading));
        row.ApplyWrite(new MifareBlockProgress(0, 1, MifareBlockState.Read));
        Assert.Equal(SectorWriteState.Writing, row.WriteState);
        Assert.Equal(0.5, row.WriteProgress);
        row.ApplyWrite(new MifareBlockProgress(1, 1, MifareBlockState.Reading));
        Assert.Equal(SectorWriteState.Writing, row.WriteState);
        row.ApplyWrite(new MifareBlockProgress(1, 1, MifareBlockState.Read));
        Assert.Equal(SectorWriteState.Written, row.WriteState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gen2WriteRequiresBothTargetKeysInEverySector(bool writeBlock0)
    {
        var originalGeneration = AppSettings.Get("cardsWriteGeneration");
        var originalBlock0 = AppSettings.Get("cardsWriteGen2Block0");
        try
        {
            var store = new MemoryKeyValueStore();
            var cards = new CardLibrary(store);
            var source = new CardSave
            {
                Name = "Source", Tag = TagSpecificType.MifareMini, Uid = [9, 8, 7, 6], Sak = 9, Atqa = [4, 0],
                Data = Enumerable.Range(0, 20).Select(MifareClassic.BlankBlock).ToList(),
            };
            cards.Add(source);
            var vm = new CardsViewModel(new DeviceService([]), cards, new DictionaryLibrary(store));
            vm.OpenCard(source.Id);
            vm.SelectedGeneration = MagicGeneration.Gen2;
            vm.WriteGen2Block0 = writeBlock0;
            typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.IsConnected))!.SetValue(vm, true);
            vm.WriteCommand.Execute(null);
            Assert.False(vm.HasRequiredWriteKeys);
            Assert.False(vm.WriteNowCommand.CanExecute(null));
            Assert.True(vm.WriteRecovery.ShowRecoveryTools);
            Assert.True(vm.WriteRecovery.ShowDumpActions);
            Assert.True(vm.ShowMfkey32);
            Assert.False(vm.WriteRecovery.ShowMfkey32);
            Assert.False(vm.WriteRecovery.Mfkey32Command.CanExecute(null));

            var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
            typeof(MifareKeyRecoveryViewModel).GetProperty(nameof(MifareKeyRecoveryViewModel.Target))!
                .SetValue(vm.WriteRecovery, target);
            var key = Convert.FromHexString("112233445566");
            for (var sector = 0; sector < target.SectorCount; sector++)
            {
                target.KeyA[sector] = key;
                vm.WriteRecovery.ApplyKey(new MifareKeyProgress(sector, MfKeyType.A, MifareKeyPhase.Found, 1, 1, key));
            }
            Assert.False(vm.HasRequiredWriteKeys);
            for (var sector = 0; sector < target.SectorCount - 1; sector++)
            {
                target.KeyB[sector] = key;
                vm.WriteRecovery.ApplyKey(new MifareKeyProgress(sector, MfKeyType.B, MifareKeyPhase.Found, 1, 1, key));
            }
            Assert.False(vm.WriteNowCommand.CanExecute(null));
            Assert.NotNull(vm.WriteKeyHint);

            target.KeyB[^1] = key;
            vm.WriteRecovery.ApplyKey(new MifareKeyProgress(target.SectorCount - 1, MfKeyType.B,
                MifareKeyPhase.Found, 1, 1, key));
            Assert.True(vm.WriteRecovery.AllTargetKeysRecovered);
            Assert.True(vm.WriteNowCommand.CanExecute(null));
            Assert.Null(vm.WriteKeyHint);
            Assert.False(vm.WriteRecovery.ShowDumpActions);
            Assert.False(vm.WriteRecovery.ShowRecoveryTools);

            vm.WriteRecovery.PrepareWrite(source.Tag, source.ToMifareDump(), writeBlock0);
            Assert.True(vm.WriteRecovery.ShowRecoveryTools);
            Assert.False(vm.WriteRecovery.ShowDumpActions);
            vm.WriteRecovery.ApplyWriteProgress(new MifareBlockProgress(1, 1, MifareBlockState.Read));
            vm.WriteRecovery.ApplyWriteProgress(new MifareBlockProgress(2, 1, MifareBlockState.Reading));
            vm.WriteRecovery.FinishWrite();
            Assert.Equal(SectorWriteState.Partial, vm.WriteRecovery.DumpSectors[0].WriteState);
            Assert.True(vm.WriteNowCommand.CanExecute(null));
            Assert.True(vm.WriteRecovery.AllTargetKeysRecovered);
            Assert.False(vm.WriteRecovery.ShowRecoveryTools);
            Assert.False(vm.WriteRecovery.ShowDumpActions);
            vm.WriteRecovery.PrepareWrite(source.Tag, source.ToMifareDump(), writeBlock0, resume: true);
            Assert.Equal(1.0 / (writeBlock0 ? 4 : 3), vm.WriteRecovery.DumpSectors[0].WriteProgress);
            vm.WriteRecovery.ApplyWriteProgress(new MifareBlockProgress(0, 20, MifareBlockState.Read));
            vm.WriteRecovery.FinishWrite();
            Assert.All(vm.WriteRecovery.DumpSectors, sector => Assert.Equal(SectorWriteState.Written, sector.WriteState));
            Assert.True(vm.WriteNowCommand.CanExecute(null));
            Assert.False(vm.WriteRecovery.ShowRecoveryTools);
            Assert.False(vm.WriteRecovery.ShowDumpActions);
            Assert.Null(vm.WriteKeyHint);
            typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.IsWriteSuccessful))!.SetValue(vm, true);
            Assert.False(vm.WriteNowCommand.CanExecute(null));
            Assert.True(vm.WriteAnotherCommand.CanExecute(null));
            Assert.Equal("Close", vm.CancelWriteLabel);
            vm.WriteAnotherCommand.Execute(null);
            Assert.False(vm.IsWriteSuccessful);
            Assert.False(vm.WriteNowCommand.CanExecute(null));
            Assert.True(vm.WriteRecovery.ShowRecoveryTools);
            Assert.True(vm.WriteRecovery.ShowDumpActions);
            Assert.NotNull(vm.WriteKeyHint);
        }
        finally
        {
            AppSettings.Set("cardsWriteGeneration", originalGeneration);
            AppSettings.Set("cardsWriteGen2Block0", originalBlock0);
        }
    }

    [Theory]
    [InlineData(MagicGeneration.Gen1a)]
    [InlineData(MagicGeneration.Gen3)]
    public void OtherGenerationsDoNotRequireRecoveredTargetKeys(MagicGeneration generation)
    {
        var original = AppSettings.Get("cardsWriteGeneration");
        try
        {
            var store = new MemoryKeyValueStore();
            var cards = new CardLibrary(store);
            var source = new CardSave { Name = "Source", Tag = TagSpecificType.Mifare1024, Uid = [1, 2, 3, 4], Sak = 8, Atqa = [4, 0] };
            cards.Add(source);
            var vm = new CardsViewModel(new DeviceService([]), cards, new DictionaryLibrary(store));
            vm.OpenCard(source.Id);
            vm.SelectedGeneration = generation;
            typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.IsConnected))!.SetValue(vm, true);
            Assert.True(vm.HasRequiredWriteKeys);
            Assert.True(vm.WriteNowCommand.CanExecute(null));
        }
        finally { AppSettings.Set("cardsWriteGeneration", original); }
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public void SectorProgressExcludesBlock0WhenItIsSkipped(bool enabled, int count)
    {
        var session = new MifareKeyRecoveryViewModel(() => null,
            new DictionaryLibrary(new MemoryKeyValueStore()), () => [], () => [], work => work());
        session.PrepareWrite(TagSpecificType.Mifare1024, writeBlock0: enabled);
        for (var block = 1; block <= 3; block++)
            session.ApplyWriteProgress(new MifareBlockProgress(block, 1, MifareBlockState.Read));

        Assert.Equal(enabled ? SectorWriteState.Partial : SectorWriteState.Written, session.DumpSectors[0].WriteState);
        Assert.Contains($"3/{count} blocks written", session.DumpSectors[0].WriteText);
    }

    [Fact]
    public void Gen2Block0OptionDefaultsOffAndPersistsInSettings()
    {
        const string setting = "cardsWriteGen2Block0";
        var original = AppSettings.Get(setting);
        CardsViewModel Create() => new(new DeviceService([]), new CardLibrary(new MemoryKeyValueStore()),
            new DictionaryLibrary(new MemoryKeyValueStore()));
        try
        {
            AppSettings.Set(setting, null);
            var vm = Create();
            Assert.False(vm.WriteGen2Block0);
            vm.WriteGen2Block0 = true;
            Assert.True(AppSettings.Get(setting)!.GetValue<bool>());
            Assert.True(Create().WriteGen2Block0);
            vm.WriteGen2Block0 = false;
            Assert.False(Create().WriteGen2Block0);
        }
        finally { AppSettings.Set(setting, original); }
    }

    [Fact]
    public void LargeSectorWriteProgressCountsOnlySelectedBlocksAndRetainsErrors()
    {
        var row = new DumpSectorRow(32);
        row.BeginWrite([128, 143]);
        Assert.Equal(SectorWriteState.Pending, row.WriteState);
        row.ApplyWrite(new MifareBlockProgress(128, 1, MifareBlockState.Read));
        Assert.Equal(SectorWriteState.Partial, row.WriteState);
        Assert.Equal(0.5, row.WriteProgress);
        row.ApplyWrite(new MifareBlockProgress(143, 1, MifareBlockState.Reading));
        Assert.Equal(SectorWriteState.Writing, row.WriteState);
        row.ApplyWrite(new MifareBlockProgress(143, 1, MifareBlockState.ReadError));
        Assert.Equal(SectorWriteState.Error, row.WriteState);
        Assert.Contains("1 failed", row.WriteText);
        row.StopWrite();
        Assert.Equal(SectorWriteState.Error, row.WriteState);
        Assert.Equal(0.5, row.WriteProgress);
        Assert.Equal(KeyCellState.Unknown, row.KeyAState);
        Assert.Equal(KeyCellState.Unknown, row.KeyBState);
    }

    [Fact]
    public void StoppingWriteDoesNotCountUnconfirmedBlockAsWritten()
    {
        var row = new DumpSectorRow(0);
        row.BeginWrite([0, 1, 2, 3]);
        row.ApplyWrite(new MifareBlockProgress(0, 1, MifareBlockState.Read));
        row.ApplyWrite(new MifareBlockProgress(1, 1, MifareBlockState.Reading));
        row.StopWrite();
        Assert.Equal(SectorWriteState.Partial, row.WriteState);
        Assert.Equal(0.25, row.WriteProgress);
        Assert.Contains("1/4 blocks written", row.WriteText);
    }

    [Theory]
    [InlineData(MagicGeneration.Gen1a, TagSpecificType.MifareMini, MifareClassicType.Mini, 5)]
    [InlineData(MagicGeneration.Gen2, TagSpecificType.MifareMini, MifareClassicType.Mini, 5)]
    [InlineData(MagicGeneration.Gen3, TagSpecificType.MifareMini, MifareClassicType.Mini, 5)]
    [InlineData(MagicGeneration.Gen1a, TagSpecificType.Mifare1024, MifareClassicType.Classic1K, 16)]
    [InlineData(MagicGeneration.Gen2, TagSpecificType.Mifare1024, MifareClassicType.Classic1K, 16)]
    [InlineData(MagicGeneration.Gen3, TagSpecificType.Mifare1024, MifareClassicType.Classic1K, 16)]
    [InlineData(MagicGeneration.Gen1a, TagSpecificType.Mifare2048, MifareClassicType.Classic2K, 32)]
    [InlineData(MagicGeneration.Gen2, TagSpecificType.Mifare2048, MifareClassicType.Classic2K, 32)]
    [InlineData(MagicGeneration.Gen3, TagSpecificType.Mifare2048, MifareClassicType.Classic2K, 32)]
    [InlineData(MagicGeneration.Gen1a, TagSpecificType.Mifare4096, MifareClassicType.Classic4K, 40)]
    [InlineData(MagicGeneration.Gen2, TagSpecificType.Mifare4096, MifareClassicType.Classic4K, 40)]
    [InlineData(MagicGeneration.Gen3, TagSpecificType.Mifare4096, MifareClassicType.Classic4K, 40)]
    public void AllMagicGenerationsPreviewSectorsAndKeepSparseWriteResults(
        MagicGeneration generation, TagSpecificType tag, MifareClassicType type, int sectors)
    {
        var session = new MifareKeyRecoveryViewModel(() => null,
            new DictionaryLibrary(new MemoryKeyValueStore()), () => [], () => [], work => work());
        session.SetGeneration(generation);
        var source = new MifareDump(type);
        source.SetBlock(0, new byte[16]);
        source.SetBlock(3, new byte[16]);
        session.PreviewWrite(tag, source);
        Assert.Equal(sectors, session.DumpSectors.Count);
        Assert.False(session.IsDumpRunning);
        Assert.Equal(generation != MagicGeneration.Gen1a, session.ShowRecoveryTools);
        Assert.Equal(generation == MagicGeneration.Gen1a, session.ShowWriteProgress);
        session.PrepareWrite(tag, source);
        Assert.True(session.ShowWriteProgress);
        Assert.Equal(sectors, session.DumpSectors.Count);
        session.ApplyWriteProgress(new MifareBlockProgress(0, 1, MifareBlockState.Read));
        session.ApplyWriteProgress(new MifareBlockProgress(3, 1, MifareBlockState.Read));
        session.FinishWrite();
        Assert.True(session.ShowWriteProgress);
        Assert.Equal(SectorWriteState.Written, session.DumpSectors[0].WriteState);
        Assert.Equal(1, session.DumpSectors[0].WriteProgress);
        Assert.All(session.DumpSectors.Skip(1), row => Assert.Equal(SectorWriteState.None, row.WriteState));
        session.PrepareWrite(tag, source);
        Assert.Equal(SectorWriteState.Pending, session.DumpSectors[0].WriteState);
    }

    [Fact]
    public void ReadingAndWritingShareDictionarySelectionButKeepSectorStateSeparate()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        var dictionaries = new DictionaryLibrary(store);
        var card = new CardSave { Name = "Source", Tag = TagSpecificType.Mifare1024, Uid = [1, 2, 3, 4], Sak = 8, Atqa = [4, 0] };
        cards.Add(card);
        var vm = new CardsViewModel(new DeviceService([]), cards, dictionaries);
        vm.OpenCard(card.Id);
        var sourceData = vm.Form!.Build().Data.Select(Convert.ToHexString).ToArray();
        Assert.IsAssignableFrom<IKeyRecoveryContext>(vm);
        Assert.IsAssignableFrom<IKeyRecoveryContext>(vm.WriteRecovery);

        var readSource = vm.DumpKeySources.Single(s => s.Id == DumpKeySourceViewModel.BuiltInId);
        var writeSource = vm.WriteRecovery.DumpKeySources.Single(s => s.Id == DumpKeySourceViewModel.BuiltInId);
        Assert.Same(readSource, writeSource);
        var initialSelection = readSource.IsSelected;
        writeSource.IsSelected = !initialSelection;
        Assert.Equal(!initialSelection, readSource.IsSelected);
        writeSource.IsSelected = initialSelection;

        vm.WriteRecovery.PreviewWrite(card.Tag);
        vm.WriteRecovery.ApplyKey(new MifareKeyProgress(0, MfKeyType.A, MifareKeyPhase.Found, 1, 1,
            Convert.FromHexString("112233445566")));
        Assert.Equal(KeyCellState.Found, vm.WriteRecovery.DumpSectors[0].KeyAState);
        Assert.Equal(KeyCellState.Unknown, vm.DumpSectors[0].KeyAState);
        Assert.Equal(sourceData, vm.Form!.Build().Data.Select(Convert.ToHexString));
        vm.WriteRecovery.Reset();
        Assert.Empty(vm.WriteRecovery.DumpSectors);
        Assert.Equal(16, vm.DumpSectors.Count);
    }

    [Fact]
    public void SharedStopActionCancelsWritingAndClearsInterruptedSearchWhenFinished()
    {
        var stopped = 0;
        var session = new MifareKeyRecoveryViewModel(() => null,
            new DictionaryLibrary(new MemoryKeyValueStore()), () => [], () => [], work => work(),
            cancelWrite: () => stopped++);
        session.SetAvailability(true, false);
        session.PrepareWrite(TagSpecificType.Mifare4096);
        Assert.Equal(40, session.DumpSectors.Count);
        Assert.True(session.IsDumpRunning);
        Assert.False(session.CanOperate);
        session.ApplyKey(new MifareKeyProgress(32, MfKeyType.B, MifareKeyPhase.Searching, 2, 10, null));

        session.CancelDumpCommand.Execute(null);
        Assert.Equal(1, stopped);
        session.FinishWrite();

        Assert.False(session.IsDumpRunning);
        Assert.False(session.CanOperate);
        Assert.False(session.ShowDumpActions);
        Assert.False(session.ShowRecoveryTools);
        Assert.Null(session.Target);
        Assert.False(session.HasDumpKeyProgress);
        Assert.Equal(KeyCellState.Unknown, session.DumpSectors[32].KeyBState);
    }
}
