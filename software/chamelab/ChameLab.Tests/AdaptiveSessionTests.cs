using System;
using System.Buffers.Binary;
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
    public async Task Adaptive_SingleDictionaryChecksReadableBBeforeItsDictionary()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, type) => type == MfKeyType.A ? Key : null);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1ReadOneBlock
            ? (Status.HfTagOk, Convert.FromHexString("000000000000FF078069000000000000")) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await client.FindMifareKeysAsync(dump, [Key], options: new(MifareKeySearchMethod.SingleKey)
            { Session = MifareRecoverySession.For(dump, client) });
        Assert.All(dump.KeyB, b => Assert.Equal(new byte[6], b));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock && r.Data[0] == (byte)MfKeyType.B);
    }

    [Fact]
    public async Task AdaptiveSen_NormalKeyProtocolHandlesAllFortySectors()
    {
        var transport = new CardTransport { Response = r => r.Command == Command.Mf1EncNestedWithKey
            ? (Status.HfTagOk, EncryptedCapture(40)) : null };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new ChameleonClient(connection).GetStaticEncryptedNestedNoncesWithKeyAsync(Key, 255, MfKeyType.B, 40);
        Assert.Equal(40, result.Sectors.Count);
        Assert.Equal(new NestedNonce(0xD14191B3, 0x2E9E49FC, 15), result.Sectors[39].B);
        Assert.Equal(Key.Concat(new byte[] { 40, 0, 255, 0x61 }), Assert.Single(transport.Requests).Data);
    }

    [Fact]
    public async Task AdaptiveSen_StopDrainsNormalKeyCollectionBeforeAnotherCommand()
    {
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport { Response = r => r.Command == Command.Mf1EncNestedWithKey
            ? (Status.HfTagOk, EncryptedCapture(5)) : null };
        transport.OnRequest = r => { if (r.Command == Command.Mf1EncNestedWithKey) stop.Cancel(); };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStaticEncryptedNestedNoncesWithKeyAsync(Key, 3, MfKeyType.A, 5, stop.Token));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.Single(await client.Hf14aScanAsync()).Uid);
    }

    [Theory]
    [InlineData(Status.HfTagNo)]
    [InlineData(Status.HfErrParity)]
    public async Task Adaptive_PartialBatchRetainsKeysWithoutRejectingUnfinishedPairs(Status status)
    {
        var transport = BatchTransport((s, t) => s == 0 && t == MfKeyType.A ? Key : null);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1CheckKeysOfSectors
            ? (status, BatchKeyResponse(r, (s, t) => s == 0 && t == MfKeyType.A ? Key : null)) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var session = new MifareRecoverySession();
        byte[]? saved = null;
        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection)
            .CheckMifareKeysOfSectorsAsync([(0, MfKeyType.A), (1, MfKeyType.B)], [Key],
                onFound: (_, key) => saved = key, session: session));
        Assert.Equal(Key, saved);
        Assert.Empty(session.Rejected);
    }

    [Fact]
    public async Task Adaptive_StopDuringSingleAuthenticationRetainsSuccessfulReply()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        known.RememberPosition(dump.Uid, 0, MfKeyType.A, Key);
        using var stop = new CancellationTokenSource();
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => Key);
        transport.OnRequest = r => { if (r.Command == Command.Mf1AuthOneKeyBlock) stop.Cancel(); };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known).RecoverAutomaticallyAsync(dump, [], ct: stop.Token));
        Assert.Equal(Key, dump.KeyA[0]);
    }

    [Fact]
    public async Task AdaptiveSen_RadioFailureRetriesSameCandidatesAndDoesNotPoisonLedger()
    {
        var key = Convert.FromHexString("112233445566");
        var checks = 0;
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => key, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, AutoEncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock => ++checks == 1 ? (Status.HfErrParity, Array.Empty<byte>())
                : (Status.HfTagOk, new byte[] { 1 }.Concat(key).ToArray()),
            _ => reply(r),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        Assert.True((await new MifareKeyRecovery(client, keyRecovery: new AdaptiveEngine(_ => [0x112233445566UL]))
            .RecoverAutomaticallyAsync(dump, [])).Complete);
        Assert.Equal(2, checks);
        Assert.Empty(MifareRecoverySession.For(dump, client).Rejected);
        var batches = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOnBlock).ToArray();
        Assert.Equal(batches[0].Data, batches[1].Data);
    }

    [Fact]
    public async Task AdaptiveSen_NormalKeyCanUseAnotherLocationOfSameKeyAfterRefusal()
    {
        var dump = KnownCard(); dump.KeyB[4] = null;
        var key = Convert.FromHexString("112233445566");
        var calls = 0;
        var transport = AutoTransport(MifareClassicPrngType.Weak, (s, t) => s == 4 && t == MfKeyType.B ? key : Key);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DC07DF07E7")),
            Command.Mf1EncNestedWithKey => ++calls == 1 ? (Status.MfErrAuth, Array.Empty<byte>()) : (Status.HfTagOk, ValidSenCapture(0xFFFFFFFFFFFFUL)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(key).ToArray()),
            _ => reply(r),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        Assert.True((await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: new AdaptiveEngine(_ => [0x112233445566UL]))
            .RecoverAutomaticallyAsync(dump, [])).Complete);
        var captures = transport.Requests.Where(r => r.Command == Command.Mf1EncNestedWithKey).ToArray();
        Assert.Equal(3, captures.Length);
        Assert.Equal((byte)MfKeyType.A, captures[0].Data[9]);
        Assert.All(captures.Skip(1), r => Assert.Equal((byte)MfKeyType.B, r.Data[9]));
    }

    [Fact]
    public async Task AdaptiveSen_StableNormalKeyCaptureMustExplainCalibrationKey()
    {
        var dump = KnownCard(); dump.KeyB[4] = null;
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => null);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DC07DF07E7")),
            Command.Mf1EncNestedWithKey => (Status.HfTagOk, ValidSenCapture(0x112233445566UL)),
            _ => reply(r),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new AdaptiveEngine(_ => throw new InvalidOperationException("Incorrect calibration reached the solver."));
        Assert.False((await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [])).Complete);
        Assert.Empty(engine.Operations);
    }

    [Fact]
    public async Task AdaptiveSen_PreparesEverySectorBeforeVerificationAndRetainsCandidatesOnStop()
    {
        var data = AutoEncryptedCapture(5);
        for (var i = 0; i < 10; i++) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(7 + i * 7), (uint)i);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => null, backdoor: true);
        using var stop = new CancellationTokenSource();
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, data),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[7]),
            _ => reply(r),
        };
        transport.OnRequest = r => { if (r.Command is Command.Mf1CheckKeysOnBlock or Command.Mf1CheckKeysOfSectors) stop.Cancel(); };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var engine = new AdaptiveEngine(_ => [123]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MifareKeyRecovery(client, keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [], ct: stop.Token));
        Assert.Equal(10, engine.Operations.Count(op => op == KeyRecoveryOperation.StaticEncryptedNested));
        Assert.Equal(10, MifareRecoverySession.For(dump, client).SenCandidates.Count);
    }
}
