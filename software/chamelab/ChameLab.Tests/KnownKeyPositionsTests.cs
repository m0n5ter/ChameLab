using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class KnownKeyPositionsTests
{
    [Fact]
    public void PositionsSurviveReloadBackupAndKeyListEdits()
    {
        byte[] uid = [1, 2, 3, 4];
        var first = new byte[6];
        var second = Convert.FromHexString("112233445566");
        var store = new MemoryKeyValueStore();
        var known = new KnownCardKeys(store);
        known.RememberPosition(uid, 39, MfKeyType.B, first, TagSpecificType.Mifare4096);
        known.RememberPosition(uid, 1, MfKeyType.A, second);
        known.Remember(uid, [first]); // Adding flat keys must preserve their locations.
        var reloaded = new KnownCardKeys(store);
        Assert.Equal(2, reloaded.PositionsForUid(uid).Count);
        Assert.Equal(first, reloaded.PositionsForUid(uid).Single(p => p.Sector == 39).Key);
        Assert.Empty(reloaded.PositionsForUid([1, 2, 3, 4, 5, 6, 7]));
        var backup = new KnownCardKeys(new MemoryKeyValueStore());
        backup.Merge(reloaded.ToJson());
        Assert.Equal(2, backup.PositionsForUid(uid).Count);
        var card = backup.Cards.Single();
        backup.ApplyEdits([card with { Keys = [second] }], []);
        Assert.Equal(second, Assert.Single(backup.PositionsForUid(uid)).Key);
    }

    [Fact]
    public void RememberDumpReplacesChangedPositionAndKeepsHistoricFlatKeys()
    {
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        dump.KeyA[2] = new byte[6];
        known.Remember(dump);
        dump.KeyA[2] = Convert.FromHexString("FFFFFFFFFFFF");
        known.Remember(dump);
        Assert.Equal(dump.KeyA[2], Assert.Single(known.PositionsForUid(dump.Uid)).Key);
        Assert.Equal(2, known.ForUid(dump.Uid).Count);
        var copy = new KnownCardKeys(new MemoryKeyValueStore());
        copy.RememberPosition(dump.Uid, 2, MfKeyType.A, new byte[6]);
        copy.Merge(known.ToJson()); // Local location is newer than the imported backup.
        Assert.Equal(new byte[6], Assert.Single(copy.PositionsForUid(dump.Uid)).Key);
    }
}
