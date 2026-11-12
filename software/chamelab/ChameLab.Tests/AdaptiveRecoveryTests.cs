using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Crypto;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public partial class MifareWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adaptive_UidKeysPrecedeBackdoorsAndAreAuthenticatedAtSavedLocations(bool located)
    {
        var key = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        if (located) known.RememberPosition(dump.Uid, 3, MfKeyType.B, key);
        else known.Remember(dump.Uid, [key]);
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => key, backdoor: true);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known)
            .RecoverAutomaticallyAsync(dump, []);
        Assert.True(result.Complete);
        Assert.DoesNotContain(transport.Requests, IsAttack);
        if (located)
        {
            var auth = transport.Requests.First(r => r.Command == Command.Mf1AuthOneKeyBlock);
            Assert.Equal(new byte[] { (byte)MfKeyType.B, 15 }.Concat(key), auth.Data);
        }
        Assert.Equal(10, known.PositionsForUid(dump.Uid).Count);
    }

    [Fact]
    public async Task Adaptive_UncheckedUidSourceIsRespected()
    {
        var key = Convert.FromHexString("112233445566");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        known.RememberPosition(dump.Uid, 0, MfKeyType.A, key);
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => key);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known)
            .RecoverAutomaticallyAsync(dump, [], useKnownUidKeys: false);
        Assert.False(result.Complete);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1CheckKeysOfSectors);
    }

    [Fact]
    public async Task Adaptive_ReadsReadableZeroBWithAInsteadOfAttackingIt()
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var trailer = Convert.FromHexString("000000000000FF078069000000000000");
        var transport = AutoTransport(MifareClassicPrngType.Hard, (_, _) => null, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1ReadOneBlock ? (Status.HfTagOk, trailer) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAutomaticallyAsync(dump, []);
        Assert.True(result.Complete);
        Assert.Equal(new byte[6], dump.KeyB[4]);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock);
        Assert.DoesNotContain(transport.Requests, IsAttack);
    }

    [Fact]
    public async Task Adaptive_SharedLedgerDoesNotRepeatExhaustedDictionaryOnSecondPass()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = BatchTransport((_, _) => null);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var session = MifareRecoverySession.For(dump, client);
        var options = new MifareKeySearchOptions { Session = session };
        await client.FindMifareKeysAsync(dump, [Key], options: options);
        await client.FindMifareKeysAsync(dump, [Key], options: options);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.Equal(10, session.Rejected.Count);
        Assert.NotSame(session, MifareRecoverySession.For(dump, new ChameleonClient(connection)));
    }

    [Fact]
    public async Task Adaptive_BatchLedgerDoesNotRejectCandidatesAfterAHit()
    {
        var transport = BatchTransport((_, _) => Key);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var session = new MifareRecoverySession();
        byte[] before = new byte[6], after = [1, 2, 3, 4, 5, 6];
        await new ChameleonClient(connection).CheckMifareKeysOfSectorsAsync([(0, MfKeyType.A)], [before, Key, after], session: session);
        Assert.Contains((3, MfKeyType.A, 0UL), session.Rejected);
        Assert.DoesNotContain((3, MfKeyType.A, MifareRecoverySession.Value(after)), session.Rejected);
    }

    [Fact]
    public async Task Adaptive_NestedReusesCalibrationAcrossDifferentTargets()
    {
        var one = Convert.FromHexString("112233445566");
        var two = Convert.FromHexString("665544332211");
        var dump = KnownCard();
        dump.KeyB[0] = dump.KeyA[1] = null;
        var transport = AutoTransport(MifareClassicPrngType.Weak,
            (s, t) => s == 0 && t == MfKeyType.B ? one : s == 1 && t == MfKeyType.A ? two : Key);
        var engine = new AdaptiveEngine(r => r.Operation == KeyRecoveryOperation.Nested
            ? new[] { r.Nonces[0].NtEnc == 0 ? 0x112233445566UL : 0x665544332211UL } : []);
        var reply = transport.Response!;
        transport.Response = r =>
        {
            if (r.Command != Command.Mf1NestedAcquire) return reply(r);
            var data = new byte[18];
            data[7] = r.Data[9] == 3 ? (byte)0 : (byte)1;
            return (Status.HfTagOk, data);
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine).RecoverAutomaticallyAsync(dump, []);
        Assert.True(result.Complete);
        Assert.Equal(2, engine.Operations.Count(op => op == KeyRecoveryOperation.Nested));
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1DetectNtDist);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1DetectPrng);
    }

    [Fact]
    public async Task Adaptive_NestedFailureRetriesThenUsesHardnested()
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var counter = 0;
        var hard = new HardnestedEngine();
        var engine = new AdaptiveEngine(_ => []);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (s, t) => s == 4 && t == MfKeyType.B ? new byte[6] : Key);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1HardnestedAcquire ? (Status.HfTagOk, HardnestedBatch(ref counter)) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), hard, keyRecovery: engine).RecoverAutomaticallyAsync(dump, []);
        Assert.True(result.Complete);
        Assert.Equal(2, engine.Operations.Count(op => op == KeyRecoveryOperation.Nested));
        Assert.Equal(1, hard.Calls);
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1DetectNtDist));
    }

    [Fact]
    public async Task Adaptive_RefusedTargetDoesNotTriggerMoreNonceAttacks()
    {
        var dump = KnownCard(); dump.KeyB[4] = null;
        var transport = AutoTransport(MifareClassicPrngType.Weak, (_, _) => null);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1NestedAcquire ? (Status.MfErrAuth, Array.Empty<byte>()) : reply(r);
        var hard = new HardnestedEngine();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), hard).RecoverAutomaticallyAsync(dump, []);
        Assert.False(result.Complete);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1NestedAcquire);
        Assert.Equal(0, hard.Calls);
    }

    private sealed class AdaptiveEngine(Func<KeyRecoveryRequest, IReadOnlyList<ulong>> execute) : IKeyRecoveryEngine
    {
        public List<KeyRecoveryOperation> Operations { get; } = [];
        public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Operations.Add(request.Operation); return Task.FromResult(execute(request)); }
    }

    private static byte[] AutoEncryptedCapture(int sectors)
    {
        var data = EncryptedCapture(sectors);
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x01020304);
        return data;
    }

    private static byte[] ValidSenCapture(ulong key, uint uid = 0x01020304)
    {
        const uint nt = 0xD14191B3;
        var cipher = new Crypto1State(key);
        var enc = nt ^ cipher.Crypto1Word(uid ^ nt, false);
        var stream = (cipher.Crypto1Word(0, false) >> 24) & 1;
        var parity = (byte)(Crypto1.OddParity8((byte)(nt & 255)) ^ Crypto1.OddParity8((byte)enc) ^ stream);
        var data = new byte[4 + 5 * 14];
        BinaryPrimitives.WriteUInt32BigEndian(data, uid);
        for (var i = 0; i < 10; i++)
        {
            var offset = 4 + i * 7;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)(nt >> 16));
            data[offset + 2] = parity;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 3), enc);
        }
        return data;
    }
}
