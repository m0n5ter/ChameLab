using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

[Collection(AppSettingsCollection.Name)]
public class KnownCardKeysTests
{
    private static readonly byte[] Uid = Convert.FromHexString("01020304");
    private static readonly byte[] Key = Convert.FromHexString("AABBCCDDEEFF");

    [Fact]
    public void KeysPersistSeparatelyAndAreIsolatedByFullUid()
    {
        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.KnownKeys.Remember(Uid, [Key, Key, new byte[5]]);
        library.KnownKeys.Remember([1, 2, 3, 4, 5, 6, 7], [new byte[6]]);
        var reloaded = new DictionaryLibrary(store);

        Assert.Equal(2, reloaded.KnownKeys.CardCount);
        Assert.Equal(2, reloaded.KnownKeys.KeyCount);
        Assert.Equal(Key, Assert.Single(reloaded.KnownKeys.ForUid(Uid)));
        Assert.Equal(new byte[6], Assert.Single(reloaded.KnownKeys.ForUid([1, 2, 3, 4, 5, 6, 7])));
        Assert.Empty(reloaded.KnownKeys.ForUid([4, 3, 2, 1]));
        Assert.Empty(reloaded.Dictionaries);
        Assert.Null(store.Read("dictionaries"));
        Assert.NotNull(store.Read(KnownCardKeys.Id));
    }

    [Fact]
    public void ReturnedKeysCannotModifyStoredKeys()
    {
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var input = (byte[])Key.Clone();
        known.Remember(Uid, [input]);
        input[0] = 0;
        known.ForUid(Uid)[0][0] = 0;
        Assert.Equal(Key, Assert.Single(known.ForUid(Uid)));
    }

    [Fact]
    public void BrokenFileIsPreserved()
    {
        var store = new MemoryKeyValueStore();
        store.Write(KnownCardKeys.Id, "broken");
        var known = new KnownCardKeys(store);
        Assert.NotNull(known.LoadError);
        Assert.Throws<InvalidOperationException>(() => known.Remember(Uid, [Key]));
        Assert.Equal("broken", store.Read(KnownCardKeys.Id));
    }

    [Fact]
    public void FailedWriteCanBeRetriedWithoutLosingKeys()
    {
        var store = new FailingStore();
        var known = new KnownCardKeys(store);
        Assert.Throws<IOException>(() => known.Remember(Uid, [Key]));
        Assert.Empty(known.ForUid(Uid));
        store.Fail = false;
        known.Remember(Uid, [Key]);
        Assert.Equal(Key, Assert.Single(new KnownCardKeys(store).ForUid(Uid)));
    }

    [Fact]
    public void DumpUsesOnlyMatchingUidKeysFirstAndHonorsToggle()
    {
        const string setting = "dumpUncheckedDictionaries";
        var original = AppSettings.Get(setting);
        try
        {
            // Every source checked, whatever earlier runs saved.
            AppSettings.Set(setting, null);
            var store = new MemoryKeyValueStore();
            AppServices.Register([], store);
            var library = new DictionaryLibrary(store);
            library.KnownKeys.Remember(Uid, [Key]);
            var otherKey = Convert.FromHexString("123456ABCDEF");
            library.KnownKeys.Remember([4, 3, 2, 1], [otherKey]);
            var vm = new CardsViewModel(new DeviceService([]), new CardLibrary(store), library);
            typeof(CardsViewModel).GetField("_classicDump", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, new MifareDump(MifareClassicType.Mini, Uid));
            var build = typeof(CardsViewModel).GetMethod("BuildKeyDictionary", BindingFlags.Instance | BindingFlags.NonPublic)!;
            byte[][] Keys() => ((System.Collections.Generic.IReadOnlyList<byte[]>)build.Invoke(vm, null)!).ToArray();

            Assert.Equal(KnownCardKeys.Id, vm.DumpKeySources[0].Id);
            Assert.Equal(Key, Keys()[0]);
            Assert.Single(Keys(), k => k.SequenceEqual(Key));
            Assert.DoesNotContain(Keys(), k => k.SequenceEqual(otherKey));
            vm.DumpKeySources[0].IsSelected = false;
            Assert.Equal(MifareClassic.DefaultKeys[0], Keys()[0]);
            vm.DumpKeySources[0].IsSelected = true;
            Assert.Equal(Key, Keys()[0]);
        }
        finally { AppSettings.Set(setting, original); }
    }

    [Fact]
    public void BackupRestoresUidGroupsAndMergesWithoutDuplicates()
    {
        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.KnownKeys.Remember(Uid, [Key]);
        var backup = BackupService.Export(new CardLibrary(store), library);
        var targetStore = new MemoryKeyValueStore();
        var target = new DictionaryLibrary(targetStore);
        target.KnownKeys.Remember(Uid, [new byte[6]]);
        BackupService.Import(backup, new CardLibrary(targetStore), target);
        BackupService.Import(backup, new CardLibrary(targetStore), target);
        Assert.Equal(2, target.KnownKeys.KeyCount);
        Assert.Contains(target.KnownKeys.ForUid(Uid), k => k.SequenceEqual(Key));
    }

    [Fact]
    public void EditorKeepsEditsAcrossUidSearchAndCommitsDeletionOnlyOnSave()
    {
        var store = new MemoryKeyValueStore();
        var known = new KnownCardKeys(store);
        known.Remember(Uid, [Key]);
        known.Remember([5, 6, 7, 8], [new byte[6]]);
        var cards = new CardLibrary(store);
        cards.Add(new CardSave { Name = "Office", Uid = Uid });
        cards.Add(new CardSave { Name = "Office copy", Uid = Uid });
        var editor = new KnownKeysEditorViewModel(known, cards);
        var first = editor.Selected!;
        Assert.Equal("01 02 03 04", first.DisplayUid);
        Assert.Contains("Office copy", first.CardNames);
        Assert.True(first.HasCardNames);
        editor.Filter = "oFFice cOpy";
        Assert.Same(first, Assert.Single(editor.Uids));
        editor.Filter = "05 06";
        Assert.False(Assert.Single(editor.Uids).HasCardNames);
        editor.Filter = null;
        var replacement = Convert.FromHexString("112233445566");
        first.Form.Keys.Write(0, replacement);
        editor.Filter = "05 06";
        Assert.Single(editor.Uids);
        editor.DeleteUidCommand.Execute(null);
        Assert.Equal(2, known.CardCount);
        Assert.Equal(Key, Assert.Single(known.ForUid(Uid)));
        editor.Filter = null;
        Assert.Same(first, editor.Selected);
        Assert.True(editor.IsDirty);

        // A different UID learned while editing must survive Save.
        known.Remember([9, 9, 9, 9], [Key]);
        editor.Save();
        var reloaded = new KnownCardKeys(store);
        Assert.Equal(replacement, Assert.Single(reloaded.ForUid(Uid)));
        Assert.Empty(reloaded.ForUid([5, 6, 7, 8]));
        Assert.Single(reloaded.ForUid([9, 9, 9, 9]));
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void EditorDiscardRestoresDeletedUidsAndOriginalKeys()
    {
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        known.Remember(Uid, [Key]);
        var editor = new KnownKeysEditorViewModel(known, new CardLibrary(new MemoryKeyValueStore()));
        editor.Selected!.Form.Keys.Write(0, new byte[6]);
        editor.DeleteUidCommand.Execute(null);
        editor.Reset();
        Assert.False(editor.IsDirty);
        Assert.Equal(Key, Assert.Single(editor.Selected!.Form.Build().Keys));
    }

    [Fact]
    public void EditorFailedSaveKeepsDraftAndStoredKeysForRetry()
    {
        var store = new FailingStore { Fail = false };
        var known = new KnownCardKeys(store);
        known.Remember(Uid, [Key]);
        var editor = new KnownKeysEditorViewModel(known, new CardLibrary(store));
        editor.DeleteUidCommand.Execute(null);
        store.Fail = true;
        Assert.Throws<IOException>(editor.Save);
        Assert.True(editor.IsDirty);
        Assert.Single(known.ForUid(Uid));
        store.Fail = false;
        editor.Save();
        Assert.Empty(new KnownCardKeys(store).Uids);
    }

    private sealed class FailingStore : IKeyValueStore
    {
        private readonly MemoryKeyValueStore _inner = new();
        public bool Fail = true;
        public string? Read(string key) => _inner.Read(key);
        public void Write(string key, string value)
        {
            if (Fail) throw new IOException("Disk full");
            _inner.Write(key, value);
        }
    }
}
