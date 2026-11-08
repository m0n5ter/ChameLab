using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

[Collection(AppSettingsCollection.Name)]
public class DictionaryLengthTests
{
    public static IEnumerable<object[]> Lengths => Enumerable.Range(4, 13).Select(length => new object[] { length });
    public static IEnumerable<object[]> EditorLengths => new[] { 4, 6, 8, 12, 16 }.Select(length => new object[] { length });

    [Theory]
    [MemberData(nameof(Lengths))]
    public void EveryLengthSurvivesTextJsonCloneStorageAndBackup(int length)
    {
        var hex = Convert.ToHexString(Enumerable.Range(1, length).Select(i => (byte)i).ToArray());
        var dictionary = KeyDictionary.FromText("Keys", $"# comment\n{hex}\n{hex.ToLowerInvariant()}");
        Assert.Equal(length, dictionary.KeyLength);
        Assert.Single(dictionary.Keys);
        Assert.Equal(length * 2, dictionary.ToJson()["keyLength"]!.GetValue<int>());

        var clone = dictionary.Clone();
        Assert.NotEqual(dictionary.Id, clone.Id);
        Assert.Equal(length, clone.KeyLength);
        Assert.Equal(dictionary.ToText(), clone.ToText());
        Assert.Equal(dictionary.ToText(), KeyDictionary.FromText("Reload", clone.ToText()).ToText());

        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.Add(dictionary);
        var saved = Assert.Single(new DictionaryLibrary(store).Dictionaries);
        Assert.Equal(length, saved.KeyLength);
        Assert.Equal(dictionary.ToText(), saved.ToText());

        var restored = new DictionaryLibrary(new MemoryKeyValueStore());
        BackupService.Import(BackupService.Export(new CardLibrary(store), library), new CardLibrary(new MemoryKeyValueStore()), restored);
        Assert.Equal(length, Assert.Single(restored.Dictionaries).KeyLength);
        Assert.Equal(dictionary.ToText(), restored.Dictionaries[0].ToText());
    }

    [Theory]
    [MemberData(nameof(EditorLengths))]
    public void EditorKeysTakeTheTypedLength(int length)
    {
        var editor = new DictionaryEditorViewModel(new KeyDictionary { Name = "Keys", KeyLength = 8 });
        Assert.True(editor.Keys.VariableRowLength);
        Assert.Equal(KeyDictionary.MaxKeyLength, editor.Keys.RowLength);
        Assert.False(editor.IsDirty);
        // Empty, it keeps the length it was made for.
        Assert.Equal(8, editor.Build().KeyLength);

        var bytes = Enumerable.Repeat((byte)0xAB, length).ToArray();
        editor.Keys.Write(0, bytes);
        var built = editor.Build();
        Assert.Equal(length, built.KeyLength);
        Assert.Equal(bytes, Assert.Single(built.Keys));
    }

    [Fact]
    public void EditorHoldsKeysOfMixedLengthsAndRejectsShortOnes()
    {
        var editor = new DictionaryEditorViewModel(KeyDictionary.FromText("Mixed", "11223344\n112233445566\n112233445566"));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, editor.Keys.Rows[0]);
        // Lengthen the first key to 6 bytes: it now repeats the second.
        editor.Keys.Write(4, [0x55, 0x66]);
        Assert.Equal(1, editor.Build().Count);

        // Paste two keys of different lengths after them.
        editor.Keys.WriteRows(2, [Convert.FromHexString("A1A2A3A4A5A6A7A8"), Convert.FromHexString("B1B2B3B4")]);
        var built = editor.Build();
        Assert.Equal(new[] { 4, 6, 8 }, built.KeyLengths);
        Assert.Equal(6, built.KeyLength);
        Assert.True(editor.IsDirty);

        // Shortened below 4 bytes, a key can't be saved.
        editor.Keys.DeleteBytes(editor.Keys.RowStart(3), editor.Keys.RowStart(3) + 1);
        Assert.Throws<FormatException>(() => editor.Build());
        Assert.True(editor.Keys.Undo());
        Assert.Equal(3, editor.Build().Count);
    }

    [Fact]
    public void LengthAndTextFiltersCombineAndResetWithoutDiscardingEdits()
    {
        var library = new DictionaryLibrary(new MemoryKeyValueStore());
        library.AddRange([
            KeyDictionary.FromText("Short", "11223344"),
            KeyDictionary.FromText("Classic", "112233445566"),
            KeyDictionary.FromText("Long", "1122334455667788"),
        ]);
        var vm = new DictionariesViewModel(library);
        var selected = vm.Dictionaries.Single(d => d.Dictionary.KeyLength == 6);
        vm.Selected = selected;
        var form = vm.Form!;
        form.Name = "Unsaved";

        vm.KeyLengthFilter = vm.KeyLengthFilters.Single(f => f.Length == 8);
        Assert.Equal(8, Assert.Single(vm.Dictionaries).Dictionary.KeyLength);
        Assert.Same(form, vm.Form);
        Assert.True(vm.IsDirty);
        vm.Filter = "Short";
        Assert.True(vm.NoMatches);
        Assert.Same(form, vm.Form);
        vm.Filter = "77 88";
        Assert.Single(vm.Dictionaries);
        Assert.True(vm.ResetFiltersCommand.CanExecute(null));

        vm.ResetFiltersCommand.Execute(null);
        Assert.Equal(3, vm.Dictionaries.Count);
        Assert.Null(vm.Filter);
        Assert.Equal(0, vm.KeyLengthFilter.Length);
        Assert.Same(form, vm.Form);
        Assert.Equal(selected.Dictionary.Id, vm.Selected!.Dictionary.Id);
        Assert.False(vm.ResetFiltersCommand.CanExecute(null));
    }

    [Fact]
    public void SavingHiddenDictionaryKeepsTheUpdatedSavedBaseline()
    {
        var library = new DictionaryLibrary(new MemoryKeyValueStore());
        library.Add(KeyDictionary.FromText("Original", "112233445566"));
        var vm = new DictionariesViewModel(library);
        vm.Selected = Assert.Single(vm.Dictionaries);
        vm.Form!.Name = "Saved";
        vm.KeyLengthFilter = vm.KeyLengthFilters.Single(f => f.Length == 8);
        vm.SaveCommand.Execute(null);
        Assert.Equal("Saved", Assert.Single(library.Dictionaries).Name);
        Assert.False(vm.IsDirty);
        vm.Form!.Name = "Discard me";
        vm.DiscardCommand.Execute(null);
        Assert.Equal("Saved", vm.Form.Name);
        Assert.True(vm.IsOpen);
    }

    [Fact]
    public void CreatingDictionaryUsesFilterAndSavingAnotherLengthRevealsIt()
    {
        var vm = new DictionariesViewModel(new DictionaryLibrary(new MemoryKeyValueStore()));
        vm.KeyLengthFilter = vm.KeyLengthFilters.Single(f => f.Length == 8);
        vm.Filter = "Hidden";
        vm.NewCommand.Execute(null);
        Assert.Equal(8, vm.Form!.Build().KeyLength);
        vm.Form.Name = "New";
        vm.Form.Keys.Write(0, new byte[16]);
        vm.SaveCommand.Execute(null);
        Assert.True(vm.IsOpen);
        Assert.Equal(16, Assert.Single(vm.Dictionaries).Dictionary.KeyLength);
        Assert.Equal(vm.Dictionaries[0], vm.Selected);
        Assert.Null(vm.Filter);
        Assert.Equal(0, vm.KeyLengthFilter.Length);
    }

    [Fact]
    public void RecoveryOffersOnlyKeysOfTheRequiredLength()
    {
        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        foreach (var length in Enumerable.Range(4, 13))
            library.Add(new KeyDictionary { Name = $"Keys {length}", KeyLength = length, Keys = [new byte[length]] });
        Assert.Equal(6, Assert.Single(library.AllKeys()).Length);
        Assert.Equal(4, Assert.Single(library.AllKeys(KeyDictionaryType.T55xx)).Length);
        var cards = new CardsViewModel(new DeviceService([]), new CardLibrary(store), library);
        var tools = new ToolsViewModel(new DeviceService([]), library);
        var dictionaryIds = library.Dictionaries.Select(d => d.Id).ToHashSet();
        Assert.Equal(library.Dictionaries.Single(d => d.KeyLength == 6).Id,
            Assert.Single(cards.DumpKeySources, s => dictionaryIds.Contains(s.Id)).Id);
        Assert.Equal(library.Dictionaries.Single(d => d.KeyLength == 4).Id,
            Assert.Single(tools.T55xxKeySources, s => dictionaryIds.Contains(s.Id)).Id);
    }

    [Fact]
    public void InvalidLengthsAreRejectedAndMixedLengthsKept()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyDictionary { KeyLength = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyDictionary { KeyLength = 17 });
        Assert.Throws<FormatException>(() => KeyDictionary.FromJson("{\"keyLength\":15}"));
        Assert.Throws<FormatException>(() => KeyDictionary.FromJson("{\"keyLength\":34}"));
        // Lines that aren't a 4- to 16-byte key are skipped; the rest keep their lengths.
        var mixed = KeyDictionary.FromText("Mixed", "1122334455667788\n112233445566778899\n11223344\n112233");
        Assert.Equal(new[] { 4, 8, 9 }, mixed.KeyLengths);
        Assert.Equal(new[] { 8, 9, 4 }, mixed.Keys.Select(k => k.Length));
        Assert.True(mixed.HasKeysOfLength(9));
        Assert.False(mixed.HasKeysOfLength(6));
        Assert.Equal(4, Assert.Single(mixed.KeysOfLength(4)).Length);
        Assert.Equal(mixed.ToText(), KeyDictionary.FromJson(mixed.ToJson().ToJsonString()).ToText());
        Assert.Equal(4, Assert.Single(KeyDictionary.FromJson("{\"keyLength\":16,\"keys\":[[1,2,3,4]]}").Keys).Length);
        Assert.Throws<FormatException>(() => KeyDictionary.FromJson("{\"keyLength\":16,\"keys\":[[1,2,3]]}"));
        var keys = KeyDictionary.ParseKeys("1122334455667788\n1122334455667788", out var duplicates, 8);
        Assert.Single(keys);
        Assert.Equal(1, duplicates);
        Assert.Throws<FormatException>(() => KeyDictionary.ParseKeys("112233445566", out _, 8));
    }
}
