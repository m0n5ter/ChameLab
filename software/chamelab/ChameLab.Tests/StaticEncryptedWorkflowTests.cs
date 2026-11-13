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
    [Fact]
    public async Task StaticEncrypted_RejectsBackdoorsWithoutTryingSavedFactoryOrRetainedKeys()
    {
        var dump = new MifareDump(MifareClassicType.Classic1K, [1, 2, 3, 4]);
        dump.KeyA[0] = Key;
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        known.Remember(dump.Uid, [Convert.FromHexString("112233445566")]);
        var engine = new StaticEncryptedTestEngine();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(Key).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known, keyRecovery: engine)
                .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested));

        Assert.Equal(Strings.Recovery_NoBackdoorAccepted, error.Message);
        Assert.Equal(Key, dump.KeyA[0]);
        Assert.All(dump.KeyA.Skip(1).Concat(dump.KeyB), key => Assert.Null(key));
        Assert.Equal(4, transport.Requests.Count(r => r.Command == Command.Mf1EncNestedAcquire));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1CheckKeysOnBlock
            or Command.Mf1CheckKeysOfSectors or Command.Mf1AuthOneKeyBlock);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task StaticEncrypted_OnlyChecksCalculatedCandidatesAndKeepsThemWhenCanceled()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var saved = Convert.FromHexString("665544332211");
        known.Remember(dump.Uid, [saved]);
        var engine = new StaticEncryptedTestEngine();
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, EncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock when r.Data.AsSpan(3).SequenceEqual(Key) => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var progress = new CallbackProgress<KeyRecoveryProgress>(p => { if (p.Mark == KeyMark.Found) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known, keyRecovery: engine)
                .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested, progress, stop.Token));

        var recovered = Convert.FromHexString("112233445566");
        Assert.Equal(recovered, dump.KeyA[0]);
        Assert.All(dump.KeyB, key => Assert.Null(key));
        var check = Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Equal(recovered, check.Data[3..9]);
        Assert.Equal(20, engine.Calls); // Prepare all ten lists and order both sides of every pair.
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1EncNestedAcquire));
        Assert.Contains(known.ForUid(dump.Uid), key => key.SequenceEqual(recovered));
    }

    [Theory]
    [InlineData(0)] // Plaintext nonce prefix.
    [InlineData(2)] // Encrypted parity.
    [InlineData(3)] // Encrypted nonce.
    public async Task StaticEncrypted_ChangingCaptureStopsBeforeCalculatingCandidates(int changedByte)
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var captures = 0;
        byte[] Capture()
        {
            var data = EncryptedCapture(5);
            if (++captures == 2) data[4 + 4 * 14 + 7 + changedByte] ^= 1;
            return data;
        }
        var engine = new StaticEncryptedTestEngine();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1CheckKeysOnBlock => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, Capture()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
                .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested));

        Assert.Equal(Strings.Recovery_StaticEncNotStatic, error.Message);
        Assert.Equal(2, captures);
        Assert.Equal(0, engine.Calls);
        Assert.Null(dump.KeyB[4]);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaticEncrypted_StableUnknownNonceUsesSolverAndPreservesKnownKeys(bool changeKnownNonce)
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var captures = 0;
        byte[] Capture()
        {
            var data = EncryptedCapture(5);
            if (++captures == 2 && changeKnownNonce) data[4] ^= 1;
            return data;
        }
        var engine = new StaticEncryptedTestEngine();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, Capture()),
            Command.Mf1CheckKeysOnBlock when captures < 2 => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1CheckKeysOnBlock when r.Data.AsSpan(3).SequenceEqual(Key) => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested);

        Assert.Equal(Convert.FromHexString("112233445566"), dump.KeyB[4]);
        Assert.All(dump.KeyA, key => Assert.Equal(Key, key));
        Assert.Equal(1, engine.Calls); // Equal nonces require recovery, without a paired-seed filter.
        var checks = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOnBlock).ToArray();
        Assert.Collection(checks,
            r => Assert.Equal(Key, r.Data[3..]), // Try reusing the known key before calculated candidates.
            r => Assert.Equal(Convert.FromHexString("112233445566"), r.Data[3..]));
        Assert.All(checks, r =>
        {
            Assert.Equal(MifareClassic.SectorFirstBlock(4), r.Data[0]);
            Assert.Equal((byte)MfKeyType.B, r.Data[1]);
            Assert.Equal(1, r.Data[2]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaticEncrypted_ConfirmationRejectsChangedCardBeforeCalculating(bool changeCaptureUid)
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var captures = 0;
        var scans = 0;
        byte[] Capture()
        {
            var data = EncryptedCapture(5);
            if (++captures == 2 && changeCaptureUid) data[0] ^= 1;
            return data;
        }
        var engine = new StaticEncryptedTestEngine();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1CheckKeysOnBlock => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, Capture()),
            Command.Hf14aScan when ++scans == 3 && !changeCaptureUid =>
                (Status.HfTagOk, new byte[] { 4, 9, 8, 7, 6, 4, 0, 9, 0 }),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
                .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested));

        Assert.Equal(Strings.Recovery_CardRemoved, error.Message);
        Assert.Equal(2, captures);
        Assert.Equal(0, engine.Calls);
    }

    private sealed class StaticEncryptedTestEngine : IKeyRecoveryEngine
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            IReadOnlyList<ulong> keys = request.Operation switch
            {
                KeyRecoveryOperation.StaticEncryptedNested => new[] { 0x112233445566UL },
                KeyRecoveryOperation.FilterStaticEncrypted => Array.Empty<ulong>(),
                KeyRecoveryOperation.OrderStaticEncrypted => request.Keys,
                _ => throw new InvalidOperationException("Unexpected recovery operation."),
            };
            return Task.FromResult(keys);
        }
    }
}
