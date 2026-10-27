using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

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
