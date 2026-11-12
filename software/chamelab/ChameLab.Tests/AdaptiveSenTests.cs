using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public partial class MifareWorkflowTests
{
    [Fact]
    public async Task AdaptiveSen_OfflineDictionarySkipsHeavyRecoveryAndOnlyAuthenticatesMatches()
    {
        const ulong value = 0x112233445566;
        var key = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => key, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, ValidSenCapture(value)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(key).ToArray()),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation == KeyRecoveryOperation.MatchStaticEncrypted
            ? ManagedKeyRecoveryEngine.Execute(r) : throw new InvalidOperationException("Heavy recovery was unnecessary."));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, DistinctKeys(100).Concat(new[] { key }).ToArray());
        Assert.True(result.Complete);
        Assert.All(engine.Operations, op => Assert.Equal(KeyRecoveryOperation.MatchStaticEncrypted, op));
        var check = Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Equal(key, check.Data[3..9]);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
    }

    [Fact]
    public async Task AdaptiveSen_CheapLaterSectorWinsOverFirstSector()
    {
        var key = Convert.FromHexString("112233445566");
        var data = AutoEncryptedCapture(5);
        // Distinct encrypted nonces require separate candidate generation.
        for (var i = 0; i < 10; i++) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(7 + i * 7), (uint)i);
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => key, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, data),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(key).ToArray()),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation == KeyRecoveryOperation.StaticEncryptedNested
            ? new[] { 0x112233445566UL }.Concat(Enumerable.Range(1, r.Nonces[0].NtEnc is 2 or 3 ? 1 : 99).Select(i => (ulong)i)).ToArray()
            : r.Keys);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(new(MifareClassicType.Mini, [1, 2, 3, 4]), []);
        Assert.True(result.Complete);
        var firstCheck = transport.Requests.First(r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Equal(7, firstCheck.Data[0]); // Sector 1 has two candidates, sector 0 has 100.
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
    }

    [Fact]
    public async Task AdaptiveSen_FourthBackdoorIsTriedAndCaptureIsReusedAfterCancellation()
    {
        var key = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        using var stop = new CancellationTokenSource();
        var captures = 0;
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => key, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (++captures <= 3 ? Status.MfErrAuth : Status.HfTagOk, AutoEncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(key).ToArray()),
            _ => reply(r),
        };
        transport.OnRequest = r => { if (r.Command == Command.Mf1CheckKeysOnBlock) stop.Cancel(); };
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var recovery = new MifareKeyRecovery(client, knownKeys: known, keyRecovery: new AdaptiveEngine(_ => [0x112233445566UL]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverAutomaticallyAsync(dump, [], ct: stop.Token));
        Assert.Equal(5, captures); // Three rejected keys, then capture and confirmation with key 4.
        Assert.Contains(dump.KeyA.Concat(dump.KeyB), k => k is not null && k.SequenceEqual(key));
        Assert.Equal(key, Assert.Single(known.ForUid(dump.Uid)));
        transport.OnRequest = null;
        var result = await recovery.RecoverAutomaticallyAsync(dump, []);
        Assert.True(result.Complete);
        Assert.Equal(5, captures);
        Assert.All(transport.Requests.Where(r => r.Command == Command.Mf1EncNestedAcquire).Skip(3),
            r => Assert.Equal(Convert.FromHexString("73B9836CF168"), r.Data[..6]));
    }

    [Fact]
    public async Task AdaptiveSen_NormalKeyCollectionWorksWithoutBackdoorAndUsesAdvertisedCommand()
    {
        var key = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        dump.KeyA[4] = Key;
        var transport = AutoTransport(MifareClassicPrngType.Weak, (s, _) => s == 4 ? Key : key);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DC07DF07E7")),
            Command.Mf1EncNestedWithKey => (Status.HfTagOk, ValidSenCapture(0xFFFFFFFFFFFFUL)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(key).ToArray()),
            _ => reply(r),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: new AdaptiveEngine(_ => [0x112233445566UL]))
            .RecoverAutomaticallyAsync(dump, []);
        Assert.True(result.Complete);
        var captures = transport.Requests.Where(r => r.Command == Command.Mf1EncNestedWithKey).ToArray();
        Assert.Equal(2, captures.Length);
        Assert.All(captures, r => Assert.Equal(Key.Concat(new byte[] { 5, 0, 19, (byte)MfKeyType.A }), r.Data));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1EncNestedAcquire or Command.Mf1NestedAcquire);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdaptiveSen_NormalKeyRejectsUnstableOrIncompleteEvidence(bool incomplete)
    {
        var dump = KnownCard(); dump.KeyB[4] = null;
        var captures = 0;
        var engine = new AdaptiveEngine(_ => throw new InvalidOperationException("Unstable evidence must not reach calculation."));
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => null);
        var reply = transport.Response!;
        transport.Response = r =>
        {
            if (r.Command == Command.GetDeviceCapabilities) return (Status.Success, Convert.FromHexString("07DC07DF07E7"));
            if (r.Command != Command.Mf1EncNestedWithKey) return reply(r);
            var data = ValidSenCapture(0xFFFFFFFFFFFFUL);
            if (++captures == 2) data[4 + 4 * 14 + 7] ^= 1;
            return (Status.HfTagOk, incomplete ? data[..^1] : data);
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var recovery = new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine);
        if (incomplete) await Assert.ThrowsAsync<ChameleonException>(() => recovery.RecoverAutomaticallyAsync(dump, []));
        else Assert.False((await recovery.RecoverAutomaticallyAsync(dump, [])).Complete);
        Assert.Empty(engine.Operations);
    }
}

public class SenPlannerTests
{
    [Fact]
    public void RejectionsShareOneMaskPerKeyAndKeepLargeSectorGeometry()
    {
        var rejected = new MifareKeyRejections();
        for (ulong key = 0; key < 1000; key++)
            for (var sector = 0; sector < 40; sector++)
                foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                    Assert.True(rejected.Add((MifareClassic.SectorFirstBlock(sector), type, key)));
        Assert.Equal(80000, rejected.Count);
        Assert.Equal(1000, rejected.DistinctKeyCount);
        Assert.False(rejected.Add((255, MfKeyType.B, 999)));
        Assert.True(rejected.Contains((255, MfKeyType.B, 999)));
        Assert.False(rejected.Contains((255, MfKeyType.B, 1000)));
        Assert.Equal(80000, rejected.Distinct().Count());
    }

    [Fact]
    public void ChoosesBenefitAndCostRatherThanSectorNumberAndUpdatesAfterNewKey()
    {
        var planner = new SenRecoveryPlanner();
        var dump = new MifareDump(MifareClassicType.Mini);
        var session = new MifareRecoverySession();
        session.RecordCandidates(TimeSpan.FromMilliseconds(500), 10);
        var slow = new SenRecoveryPlanner.Path(0, MfKeyType.B, new(0, 0, 0), Enumerable.Range(1, 100).Select(i => (ulong)i).ToArray());
        var fast = new SenRecoveryPlanner.Path(3, MfKeyType.A, new(0, 0, 0), [200, 201, 202]);
        planner.Add(slow); planner.Add(fast);
        Assert.Equal(3, planner.Next(dump, session)!.Sector);
        planner.Add(slow with { PreferredCount = 1, CascadeBenefit = 2 });
        Assert.Equal(0, planner.Next(dump, session)!.Sector);
        dump.KeyB[0] = new byte[6];
        Assert.Equal(3, planner.Next(dump, session)!.Sector);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x112233445566UL)]
    [InlineData(0xFFFFFFFFFFFFUL)]
    public void OfflineMatchingRejectsWrongCiphertextAndParity(ulong key)
    {
        const uint uid = 0xA13E4902, nt = 0xD14191B3;
        var state = new ChameLab.Crypto.Crypto1State(key);
        var enc = nt ^ ChameLab.Crypto.Crypto1.Crypto1Word(ref state, uid ^ nt, false);
        var stream = (ChameLab.Crypto.Crypto1.Crypto1Word(ref state, 0, false) >> 24) & 1;
        var parity = (byte)(ChameLab.Crypto.Crypto1.OddParity8((byte)(nt & 255)) ^ ChameLab.Crypto.Crypto1.OddParity8((byte)enc) ^ stream);
        var nonce = new NestedNonce(nt, enc, parity);
        Assert.True(StaticEncryptedNestedRecovery.Matches(uid, nonce, key));
        Assert.Equal(new[] { key }, KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.MatchStaticEncrypted)
            { Uid = uid, Nonces = [nonce], Keys = new[] { key, key ^ 1 } }));
        Assert.False(StaticEncryptedNestedRecovery.Matches(uid, nonce with { NtEnc = enc ^ 1 }, key));
        Assert.False(StaticEncryptedNestedRecovery.Matches(uid, nonce with { Parity = (byte)(parity ^ 1) }, key));
    }
}
