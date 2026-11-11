using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public partial class MifareWorkflowTests
{
    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors, true)]
    [InlineData(MifareKeySearchMethod.OneSector, true)]
    [InlineData(MifareKeySearchMethod.SingleKey, true)]
    [InlineData(MifareKeySearchMethod.AllSectors, false)]
    public async Task SeedSearch_StopsAfterFirstReplyAndLeavesConnectionUsable(MifareKeySearchMethod method, bool batch)
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (s, type) => s == 0 && type == MfKeyType.A ? Key : null);
        var reply = transport.Response!;
        if (!batch) transport.Response = r => r.Command == Command.GetDeviceCapabilities
            ? (Status.Success, Array.Empty<byte>()) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await client.FindMifareKeysAsync(dump, new[] { Key }.Concat(DistinctKeys(100)).ToArray(),
            options: new(method) { StopAfterFirstKey = true });
        await client.SetDeviceModeAsync(DeviceMode.Tag);

        Assert.Equal(Key, dump.KeyA[0]);
        Assert.All(dump.KeyA.Skip(1).Concat(dump.KeyB), Assert.Null);
        Assert.Single(transport.Requests, r => r.Command is Command.Mf1CheckKeysOfSectors or Command.Mf1AuthOneKeyBlock);
    }

    [Theory]
    [InlineData(MifareClassicPrngType.Weak, MifareKeySearchMethod.AllSectors)]
    [InlineData(MifareClassicPrngType.Weak, MifareKeySearchMethod.OneSector)]
    [InlineData(MifareClassicPrngType.Weak, MifareKeySearchMethod.SingleKey)]
    [InlineData(MifareClassicPrngType.Static, MifareKeySearchMethod.OneSector)]
    [InlineData(MifareClassicPrngType.Hard, MifareKeySearchMethod.OneSector)]
    public async Task Auto_SlowFirstDictionaryHitSpreadsAcrossWholeCardBeforeAnyAttack(
        MifareClassicPrngType prng, MifareKeySearchMethod method)
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var keys = DistinctKeys(200);
        keys[21] = Key;
        var transport = AutoTransport(prng, (_, _) => Key);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), new HardnestedEngine(), known,
            new AutoTestEngine([])).RecoverAutomaticallyAsync(dump, keys, options: new(method));

        Assert.True(result.Complete);
        Assert.All(dump.KeyA.Concat(dump.KeyB), key => Assert.Equal(Key, key));
        Assert.Equal(Key, Assert.Single(known.ForUid(dump.Uid)));
        Assert.DoesNotContain(transport.Requests, IsAttack);
        Assert.DoesNotContain(transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors),
            r => Enumerable.Range(0, (r.Data.Length - 10) / 6).Any(i => r.Data.AsSpan(10 + i * 6, 6).SequenceEqual(keys[24])));
    }

    [Theory]
    [InlineData(MifareClassicPrngType.Weak)]
    [InlineData(MifareClassicPrngType.Static)]
    public async Task Auto_EveryNewAttackKeyIsSpreadBeforeStartingAnotherAttack(MifareClassicPrngType prng)
    {
        var recovered = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        dump.KeyA[4] = Key;
        // The first unresolved sector uses a different key. Other sectors share the seed.
        // Both groups must be exhausted by propagation, with exactly one solver invocation.
        var transport = AutoTransport(prng, (s, _) => s is 1 or 4 ? Key : recovered);
        var engine = new AutoTestEngine([0x112233445566UL]);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, DistinctKeys(100));

        Assert.True(result.Complete);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(prng == MifareClassicPrngType.Weak ? KeyRecoveryOperation.Nested : KeyRecoveryOperation.StaticNested,
            Assert.Single(engine.Operations));
        Assert.Equal(1, transport.Requests.Count(r => r.Command is Command.Mf1NestedAcquire or Command.Mf1StaticNestedAcquire));
        var firstAttack = transport.Requests.FindIndex(IsAttack);
        var initialReuse = transport.Requests.FindIndex(r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.True(initialReuse >= 0 && initialReuse < firstAttack);
        var propagation = transport.Requests.Last(r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.Equal(recovered, propagation.Data[10..]);
        Assert.Equal(Key, dump.KeyA[1]);
        Assert.Equal(Key, dump.KeyB[1]);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1NestedAcquire or Command.Mf1StaticNestedAcquire
            && r.Data[9] == MifareClassic.SectorTrailerBlock(1));
        Assert.All(new[] { 0, 2, 3 }, s => Assert.Equal(recovered, dump.KeyA[s]));
    }

    [Fact]
    public async Task Auto_BackdoorRejectionFallsThroughToDictionaryAndNested()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var keys = DistinctKeys(100);
        keys[12] = Key;
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => Key, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1EncNestedAcquire ? (Status.MfErrAuth, Array.Empty<byte>()) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: new AutoTestEngine([]))
            .RecoverAutomaticallyAsync(dump, keys);

        Assert.True(result.Complete);
        Assert.Equal(3, transport.Requests.Count(r => r.Command == Command.Mf1EncNestedAcquire));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1NestedAcquire);
    }

    [Fact]
    public async Task Auto_UnstableBackdoorNeverRunsStaticEncryptedSolver()
    {
        var captures = 0;
        var engine = new AutoTestEngine([]);
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => null, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r =>
        {
            if (r.Command != Command.Mf1EncNestedAcquire) return reply(r);
            var payload = EncryptedCapture(5);
            if (++captures == 2) payload[7] ^= 1;
            return (Status.HfTagOk, payload);
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, []);

        Assert.False(result.Complete);
        Assert.Equal(10, result.MissingKeys);
        Assert.Contains("mfkey32", result.Message);
        Assert.Equal(2, captures);
        Assert.Equal(0, engine.Calls);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1HardnestedAcquire);
    }

    [Fact]
    public async Task Auto_CancellationDuringSeedBatchKeepsKeysAndDoesNotStartAttack()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        using var stop = new CancellationTokenSource();
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => Key);
        transport.OnRequest = r => { if (r.Command == Command.Mf1CheckKeysOfSectors) stop.Cancel(); };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MifareKeyRecovery(client, knownKeys: known).RecoverAutomaticallyAsync(dump, [Key], ct: stop.Token));
        await client.SetDeviceModeAsync(DeviceMode.Tag);

        Assert.Equal(Key, dump.KeyA[0]);
        Assert.Equal(Key, Assert.Single(known.ForUid(dump.Uid)));
        Assert.DoesNotContain(transport.Requests, IsAttack);
    }

    [Fact]
    public async Task Auto_ChangedCardIsRejectedBeforeSearching()
    {
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => Key);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MifareKeyRecovery(new ChameleonClient(connection))
            .RecoverAutomaticallyAsync(new(MifareClassicType.Mini, [9, 8, 7, 6]), [Key]));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors || IsAttack(r));
    }

    [Fact]
    public async Task Auto_HardnestedUnavailableStillRecoversAllDictionaryKeys()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => Key);
        var keys = DistinctKeys(100);
        keys[50] = Key;
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAutomaticallyAsync(dump, keys);
        Assert.True(result.Complete);
        Assert.DoesNotContain(transport.Requests, IsAttack);
    }

    [Fact]
    public async Task Auto_HardnestedRunsOnceAndSpreadsItsKeyBeforeAnotherCapture()
    {
        var dump = KnownCard();
        dump.KeyB[0] = dump.KeyA[1] = null;
        var engine = new HardnestedEngine();
        var counter = 0;
        var transport = AutoTransport(MifareClassicPrngType.Hard,
            (s, type) => s == 0 && type == MfKeyType.B || s == 1 && type == MfKeyType.A ? new byte[6] : Key);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1HardnestedAcquire
            ? (Status.HfTagOk, HardnestedBatch(ref counter)) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), engine)
            .RecoverAutomaticallyAsync(dump, []);

        Assert.True(result.Complete);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(new byte[6], dump.KeyB[0]);
        Assert.Equal(new byte[6], dump.KeyA[1]);
        Assert.All(transport.Requests.Where(r => r.Command == Command.Mf1HardnestedAcquire),
            r => Assert.Equal(MifareClassic.SectorTrailerBlock(0), r.Data[10]));
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1CheckKeysOfSectors));
    }

    [Fact]
    public async Task Auto_DarksideSeedIsSpreadBeforeNested()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var engine = new AutoTestEngine([0xFFFFFFFFFFFFUL]);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => Key);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DC07D407D507D6")),
            Command.Mf1DarksideAcquire => (Status.HfTagOk,
                r.Data[3] == 2 ? new byte[] { (byte)DarksideResult.Vulnerable } : new byte[33]),
            _ => reply(r),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, []);

        Assert.True(result.Complete);
        Assert.Equal(KeyRecoveryOperation.Darkside, Assert.Single(engine.Operations));
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1DarksideAcquire));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1NestedAcquire);
    }

    [Fact]
    public async Task Auto_StaticEncryptedKeyIsSpreadBeforeMoreCandidateSearches()
    {
        var recovered = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var engine = new AutoTestEngine([0x112233445566UL]);
        var hardnested = new HardnestedEngine();
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => recovered, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, EncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(recovered).ToArray()),
            _ => reply(r),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), hardnested, keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, []);

        Assert.True(result.Complete);
        Assert.Equal(0, hardnested.Calls);
        Assert.Equal(3, engine.Calls); // A/B candidates and one filter, then propagation opens the card.
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.All(dump.KeyA.Concat(dump.KeyB), key => Assert.Equal(recovered, key));
    }

    [Fact]
    public async Task Auto_FailedNestedFallsBackToDictionaryWithoutLosingExistingData()
    {
        var recovered = Convert.FromHexString("112233445566");
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var block = new byte[16];
        block[0] = 123;
        dump.SetBlock(0, block);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (s, type) => s == 4 && type == MfKeyType.B ? recovered : Key);
        var engine = new AutoTestEngine([]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known, keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [recovered]);

        Assert.True(result.Complete);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(recovered, dump.KeyB[4]);
        Assert.Equal(block, dump.GetBlock(0));
        Assert.Contains(known.ForUid(dump.Uid), key => key.SequenceEqual(recovered));
    }

    [Fact]
    public async Task Auto_MissingFirmwareCommandsDoNotRunUnsupportedAttacks()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        dump.KeyA[0] = Key;
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => new byte[6]);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.GetDeviceCapabilities
            ? (Status.Success, Convert.FromHexString("07DC")) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAutomaticallyAsync(dump, [new byte[6]]);

        Assert.True(result.Complete);
        Assert.DoesNotContain(transport.Requests, IsAttack);
    }

    [Fact]
    public async Task Auto_UnavailableHardnestedExplainsPartialResult()
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => null);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAutomaticallyAsync(dump, []);

        Assert.False(result.Complete);
        Assert.Equal(1, result.MissingKeys);
        Assert.Contains(Strings.Recovery_HardnestedUnavailable, result.Message);
        Assert.All(dump.KeyA, key => Assert.Equal(Key, key));
        Assert.DoesNotContain(transport.Requests, IsAttack);
    }

    private static bool IsAttack(ChameleonFrame r) => r.Command is Command.Mf1NestedAcquire
        or Command.Mf1StaticNestedAcquire or Command.Mf1HardnestedAcquire or Command.Mf1EncNestedAcquire or Command.Mf1DarksideAcquire;

    private static CardTransport AutoTransport(MifareClassicPrngType prng,
        Func<int, MfKeyType, byte[]?> keyFor, bool backdoor = false) => new()
    {
        Prng = prng,
        Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success,
                Convert.FromHexString(backdoor ? "07DC07DD07DE07DF07D307D507D6" : "07DC07DD07D307D507D6")),
            Command.Mf1CheckKeysOfSectors => (Status.HfTagOk, BatchKeyResponse(r, keyFor)),
            Command.Mf1AuthOneKeyBlock => (keyFor(MifareClassic.SectorOfBlock(r.Data[1]), (MfKeyType)r.Data[0]) is { } key
                && key.AsSpan().SequenceEqual(r.Data.AsSpan(2)) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1NestedAcquire => (Status.HfTagOk, new byte[18]),
            _ => null,
        },
    };

    private sealed class AutoTestEngine(IReadOnlyList<ulong> result) : IKeyRecoveryEngine
    {
        public int Calls { get; private set; }
        public List<KeyRecoveryOperation> Operations { get; } = new();
        public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Operations.Add(request.Operation);
            return Task.FromResult(result);
        }
    }
}
