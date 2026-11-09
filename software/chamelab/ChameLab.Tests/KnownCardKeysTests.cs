using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ChameLab.Models;
using ChameLab.Protocol;
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
    public void PasswordsAreKeptPerFamilyAndReplaced()
    {
        var store = new MemoryKeyValueStore();
        var known = new KnownCardKeys(store);
        var ntag = Convert.FromHexString("04112233445566");
        known.Remember(Uid, [Key], TagSpecificType.Mifare1024);
        known.RememberPassword(ntag, [1, 2, 3, 4], TagSpecificType.Ntag215);
        known.RememberPassword(ntag, [5, 6, 7, 8]);
        known.RememberPassword(Uid, new byte[6]);
        // The same hex as a MIFARE UID, but a T55xx ID: a card of its own.
        known.RememberT55xxPassword(Uid, [9, 9, 9, 9], TagSpecificType.Em410x);
        known.Remember([9, 9, 9, 9], [Key]);

        var reloaded = new KnownCardKeys(store);
        Assert.Null(reloaded.LoadError);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, reloaded.PasswordForUid(ntag));
        Assert.Null(reloaded.PasswordForUid(Uid));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, reloaded.T55xxPasswordForId(Uid));
        Assert.Equal(Key, Assert.Single(reloaded.ForUid(Uid)));
        Assert.Equal(1, reloaded.PasswordCount);
        Assert.Equal(1, reloaded.T55xxPasswordCount);
        Assert.Equal(4, reloaded.CardCount);
        // A later remember without a type keeps the one already known.
        Assert.Equal(TagSpecificType.Ntag215, reloaded.Cards.Single(c => c.Family == KnownCardFamily.Ultralight).Tag);
        Assert.Equal(TagSpecificType.Mifare1024, reloaded.Cards.First(c => c.Family == KnownCardFamily.MifareClassic).Tag);
        Assert.Equal(TagSpecificType.Em410x, reloaded.Cards.Single(c => c.Family == KnownCardFamily.T55xx).Tag);
    }

    [Fact]
    public void LegacyFileIsReadAndRewrittenWithTypes()
    {
        var store = new MemoryKeyValueStore();
        store.Write(KnownCardKeys.Id,
            """{"01020304":["AABBCCDDEEFF"],"ntagPasswords":{"04112233445566":"01020304"}}""");
        var known = new KnownCardKeys(store);
        Assert.Null(known.LoadError);
        Assert.Equal(Key, Assert.Single(known.ForUid(Uid)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, known.PasswordForUid(Convert.FromHexString("04112233445566")));
        Assert.All(known.Cards, c => Assert.Null(c.Tag));

        known.Remember(Uid, [Key], TagSpecificType.Mifare4096);
        Assert.Contains("\"version\":2", store.Read(KnownCardKeys.Id));
        var reloaded = new KnownCardKeys(store);
        Assert.Equal(TagSpecificType.Mifare4096, reloaded.Cards.Single(c => c.Family == KnownCardFamily.MifareClassic).Tag);
        Assert.Equal(1, reloaded.PasswordCount);
    }

    [Fact]
    public void TypesFromAnotherFamilyAreNotStored()
    {
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        known.Remember(Uid, [Key], TagSpecificType.Ntag215);
        known.RememberT55xxPassword(Uid, [1, 2, 3, 4], TagSpecificType.Mifare1024);
        Assert.All(known.Cards, c => Assert.Null(c.Tag));
    }

    [Fact]
    public void BackupAddsT55xxPasswordsAndMissingTypes()
    {
        var source = new KnownCardKeys(new MemoryKeyValueStore());
        source.RememberT55xxPassword([1, 2, 3, 4, 5], [1, 2, 3, 4], TagSpecificType.Em410x);
        source.Remember(Uid, [Key], TagSpecificType.Mifare1024);
        var target = new KnownCardKeys(new MemoryKeyValueStore());
        target.Remember(Uid, [new byte[6]]);
        target.Merge(source.ToJson());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, target.T55xxPasswordForId([1, 2, 3, 4, 5]));
        Assert.Equal(2, target.ForUid(Uid).Count);
        Assert.Equal(TagSpecificType.Mifare1024, target.Cards.Single(c => c.Family == KnownCardFamily.MifareClassic).Tag);
    }

    [Fact]
    public void BackupAddsNtagPasswordsWithoutOverwritingNewerOnes()
    {
        var ntag = Convert.FromHexString("04112233445566");
        var other = Convert.FromHexString("04AABBCCDDEEFF");
        var source = new KnownCardKeys(new MemoryKeyValueStore());
        source.RememberPassword(ntag, [1, 2, 3, 4]);
        source.RememberPassword(other, [9, 9, 9, 9]);
        var target = new KnownCardKeys(new MemoryKeyValueStore());
        target.RememberPassword(ntag, [5, 6, 7, 8]);
        target.Merge(source.ToJson());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, target.PasswordForUid(ntag));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, target.PasswordForUid(other));
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
        Assert.Same(first, Assert.Single(editor.Cards));
        editor.Filter = "05 06";
        Assert.False(Assert.Single(editor.Cards).HasCardNames);
        editor.Filter = null;
        var replacement = Convert.FromHexString("112233445566");
        first.Form!.Keys.Write(0, replacement);
        editor.Filter = "05 06";
        Assert.Single(editor.Cards);
        editor.DeleteCardCommand.Execute(null);
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
        editor.Selected!.Form!.Keys.Write(0, new byte[6]);
        editor.DeleteCardCommand.Execute(null);
        editor.Reset();
        Assert.False(editor.IsDirty);
        Assert.Equal(Key, Assert.Single(editor.Selected!.Form!.Build().Keys));
    }

    [Fact]
    public void EditorFailedSaveKeepsDraftAndStoredKeysForRetry()
    {
        var store = new FailingStore { Fail = false };
        var known = new KnownCardKeys(store);
        known.Remember(Uid, [Key]);
        var editor = new KnownKeysEditorViewModel(known, new CardLibrary(store));
        editor.DeleteCardCommand.Execute(null);
        store.Fail = true;
        Assert.Throws<IOException>(editor.Save);
        Assert.True(editor.IsDirty);
        Assert.Single(known.ForUid(Uid));
        store.Fail = false;
        editor.Save();
        Assert.Empty(new KnownCardKeys(store).Cards);
    }

    [Fact]
    public void EditorFiltersByTypeAndEditsPasswords()
    {
        var store = new MemoryKeyValueStore();
        var known = new KnownCardKeys(store);
        var ntag = Convert.FromHexString("04112233445566");
        known.Remember(Uid, [Key]);
        known.RememberPassword(ntag, [1, 2, 3, 4], TagSpecificType.Ntag213);
        known.RememberT55xxPassword([1, 2, 3, 4, 5], [5, 6, 7, 8], TagSpecificType.Em410x);
        var cards = new CardLibrary(store);
        cards.Add(new CardSave { Name = "Office", Uid = Uid, Tag = TagSpecificType.Mifare1024 });
        var editor = new KnownKeysEditorViewModel(known, cards);
        Assert.Equal(3, editor.Cards.Count);
        Assert.False(editor.IsDirty);

        editor.FamilyFilter = editor.FamilyOptions.Single(o => o.Family == KnownCardFamily.T55xx);
        var t55xx = Assert.Single(editor.Cards);
        Assert.Same(t55xx, editor.Selected);
        Assert.Equal("T55XX · EM410X", t55xx.TypeText);
        Assert.Equal("EM", t55xx.ShortTypeText);
        Assert.True(t55xx.HasPassword);
        Assert.Null(t55xx.Form);
        t55xx.Password = "11 22 33";
        Assert.NotNull(t55xx.PasswordError);
        Assert.Throws<FormatException>(editor.Save);
        t55xx.Password = "11 22 33 44";
        Assert.True(editor.IsDirty);

        editor.FamilyFilter = editor.FamilyOptions.Single(o => o.Family == KnownCardFamily.MifareClassic);
        var classic = Assert.Single(editor.Cards);
        // The type comes from the saved card with this UID until the entry stores one.
        Assert.Equal(TagSpecificType.Mifare1024, classic.SelectedTag.Tag);
        Assert.Equal("MFC 1K", classic.ShortTypeText);
        Assert.False(classic.IsDirty);

        editor.FamilyFilter = editor.FamilyOptions[0];
        editor.Filter = "NTAG213";
        var ultralight = Assert.Single(editor.Cards);
        ultralight.SelectedTag = ultralight.TagOptions.Single(o => o.Tag == TagSpecificType.Ntag216);
        Assert.Equal("NTAG216", ultralight.ShortTypeText);

        editor.Save();
        var reloaded = new KnownCardKeys(store);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, reloaded.T55xxPasswordForId([1, 2, 3, 4, 5]));
        Assert.Equal(TagSpecificType.Ntag216, reloaded.Cards.Single(c => c.Family == KnownCardFamily.Ultralight).Tag);
        Assert.Equal(TagSpecificType.Mifare1024, reloaded.Cards.Single(c => c.Family == KnownCardFamily.MifareClassic).Tag);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reloaded.PasswordForUid(ntag));
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
