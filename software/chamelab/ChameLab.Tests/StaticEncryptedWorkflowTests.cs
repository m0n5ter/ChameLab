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
    public async Task StaticEncrypted_FactoryKeysFinishWithoutCollectingOrCalculatingNonces()
    {
        var dump = new MifareDump(MifareClassicType.Classic1K, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var engine = new StaticEncryptedTestEngine();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(Key).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known, keyRecovery: engine)
            .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested);

        Assert.All(dump.KeyA.Concat(dump.KeyB), key => Assert.Equal(Key, key));
        Assert.Equal(Key, Assert.Single(known.ForUid(dump.Uid)));
        Assert.Equal(32, transport.Requests.Count(r => r.Command == Command.Mf1CheckKeysOnBlock));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1EncNestedAcquire);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task StaticEncrypted_TriesSavedKeysBeforeDefaultsAndKeepsThemWhenCanceled()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var saved = Convert.FromHexString("112233445566");
        known.Remember(dump.Uid, [saved]);
        var engine = new StaticEncryptedTestEngine();
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var progress = new CallbackProgress<KeyRecoveryProgress>(p => { if (p.Mark == KeyMark.Found) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), knownKeys: known, keyRecovery: engine)
                .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested, progress, stop.Token));

        Assert.Equal(saved, dump.KeyB[0]);
        Assert.All(dump.KeyA, key => Assert.Null(key));
        var check = Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Equal(saved, check.Data[3..9]);
        Assert.Equal(0, engine.Calls);
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
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaticEncrypted_StableUnknownNonceUsesSolverAndUnfilteredFallback(bool changeKnownNonce)
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
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested);

        Assert.Equal(Convert.FromHexString("112233445566"), dump.KeyB[4]);
        Assert.All(dump.KeyA, key => Assert.Equal(Key, key));
        Assert.Equal(2, engine.Calls); // One recovery and one filter, which rejects all candidates.
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1CheckKeysOnBlock));
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
            Calls++;
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ulong> keys = request.Operation switch
            {
                KeyRecoveryOperation.StaticEncryptedNested => new[] { 0x112233445566UL },
                KeyRecoveryOperation.FilterStaticEncrypted => Array.Empty<ulong>(),
                _ => throw new InvalidOperationException("Unexpected recovery operation."),
            };
            return Task.FromResult(keys);
        }
    }
}
