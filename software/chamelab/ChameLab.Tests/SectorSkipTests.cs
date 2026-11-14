using System;
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
    [Theory]
    [InlineData(MifareKeySearchMethod.SingleKey, true)]
    [InlineData(MifareKeySearchMethod.AllSectors, true)]
    [InlineData(MifareKeySearchMethod.OneSector, true)]
    [InlineData(MifareKeySearchMethod.AllSectors, false)]
    public async Task SkipSector_DictionaryContinuesInConfiguredOrderAndKeepsConfirmedKeys(
        MifareKeySearchMethod method, bool batch)
    {
        var control = new MifareSectorControl();
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        dump.KeyA[3] = Key;
        var transport = BatchTransport((_, _) => null);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities when !batch => (Status.Success, []),
            Command.Mf1AuthOneKeyBlock => (Status.MfErrAuth, []),
            _ => reply(r),
        };
        var skippedAt = -1;
        transport.OnRequest = r =>
        {
            if (skippedAt >= 0 || r.Command is not (Command.Mf1CheckKeysOfSectors or Command.Mf1AuthOneKeyBlock)) return;
            Assert.Equal(3, control.CurrentSector);
            skippedAt = transport.Requests.Count;
            control.SkipCurrent();
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        using var stop = new CancellationTokenSource();
        var client = new ChameleonClient(connection);
        await client.FindMifareKeysAsync(dump, DistinctKeys(24), ct: stop.Token, options:
            new(method, MifareKeySearchStart.Selected, SelectedSector: 3) { SectorControl = control });
        await client.SetDeviceModeAsync(DeviceMode.Tag);

        Assert.True(control.IsSkipped(3));
        Assert.Equal(-1, control.CurrentSector);
        Assert.False(stop.IsCancellationRequested);
        Assert.Equal(Key, dump.KeyA[3]);
        var later = transport.Requests.Skip(skippedAt)
            .Where(r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1CheckKeysOfSectors).ToArray();
        Assert.NotEmpty(later);
        Assert.DoesNotContain(later.SelectMany(RequestedPairs), bit => bit / 2 == 3);
        // A firmware batch has already covered sector 4 in its in-flight ascending run.
        Assert.Equal(method == MifareKeySearchMethod.SingleKey || !batch ? 4 : 0, RequestedPairs(later[0])[0] / 2);
    }

    [Fact]
    public async Task SkipSector_BatchRetainsKeysFromTheInFlightReply()
    {
        var control = new MifareSectorControl();
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = BatchTransport((s, type) => s == 0 && type == MfKeyType.A ? Key : null);
        transport.OnRequest = r =>
        {
            if (r.Command == Command.Mf1CheckKeysOfSectors && !control.IsSkipped(0)) control.SkipCurrent();
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(dump, new[] { Key }.Concat(DistinctKeys(24)).ToArray(),
            options: new() { SectorControl = control });
        Assert.Equal(Key, dump.KeyA[0]);
        Assert.Null(dump.KeyB[0]);
        Assert.DoesNotContain(transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors).Skip(1)
            .SelectMany(RequestedPairs), bit => bit / 2 == 0);
    }

    [Theory]
    [InlineData(MifareRecoveryMethod.Nested, MifareClassicPrngType.Weak)]
    [InlineData(MifareRecoveryMethod.StaticNested, MifareClassicPrngType.Static)]
    [InlineData(MifareRecoveryMethod.Hardnested, MifareClassicPrngType.Hard)]
    public async Task SkipSector_CancelsSolverAndSkipsBothKeysBeforeRecoveringNextSector(
        MifareRecoveryMethod method, MifareClassicPrngType prng)
    {
        var dump = KnownCard();
        dump.KeyA[0] = dump.KeyB[0] = dump.KeyA[1] = null;
        var control = new MifareSectorControl();
        var engine = new SkipTestEngine(control);
        var hardnested = new SkipHardnestedEngine(engine);
        var transport = AutoTransport(prng, (s, type) => s == 0 || s == 1 && type == MfKeyType.A ? new byte[6] : Key);
        var reply = transport.Response!;
        var nonce = 0;
        transport.Response = r => r.Command == Command.Mf1HardnestedAcquire
            ? (Status.HfTagOk, HardnestedBatch(ref nonce)) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        using var stop = new CancellationTokenSource();
        await new MifareKeyRecovery(new ChameleonClient(connection), hardnested, keyRecovery: engine, sectorControl: control)
            .RecoverAsync(dump, method, ct: stop.Token);

        Assert.True(engine.Canceled);
        Assert.False(stop.IsCancellationRequested);
        Assert.Equal(new[] { 0, 1 }, engine.Sectors);
        Assert.Null(dump.KeyA[0]);
        Assert.Null(dump.KeyB[0]);
        Assert.Equal(new byte[6], dump.KeyA[1]);
        Assert.Equal(Key, dump.KeyB[1]);
    }

    [Fact]
    public async Task SkipSector_AutoNeverRevisitsSkippedSectorDuringReuseAttackOrDictionaryFallback()
    {
        var dump = KnownCard();
        dump.KeyA[0] = dump.KeyB[0] = dump.KeyA[1] = null;
        var control = new MifareSectorControl();
        var transport = AutoTransport(MifareClassicPrngType.Weak,
            (s, type) => s == 0 || s == 1 && type == MfKeyType.A ? new byte[6] : Key);
        var skippedAt = -1;
        transport.OnRequest = r =>
        {
            if (skippedAt >= 0 || r.Command != Command.Mf1CheckKeysOfSectors) return;
            skippedAt = transport.Requests.Count;
            control.SkipCurrent();
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: new AutoTestEngine([0]),
            sectorControl: control).RecoverAutomaticallyAsync(dump, DistinctKeys(24));
        Assert.Equal(2, result.MissingKeys);
        Assert.Equal(new byte[6], dump.KeyA[1]);
        foreach (var r in transport.Requests.Skip(skippedAt))
        {
            if (r.Command is Command.Mf1CheckKeysOfSectors or Command.Mf1AuthOneKeyBlock)
                Assert.DoesNotContain(RequestedPairs(r), bit => bit / 2 == 0);
            if (r.Command == Command.Mf1NestedAcquire) Assert.NotEqual(3, r.Data[9]);
            if (r.Command == Command.Mf1ReadOneBlock) Assert.NotEqual(3, r.Data[1]);
        }
    }

    [Fact]
    public async Task SkipSector_SenContinuesPreparationAndCanRetrySkippedSectorOnTheNextRun()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var control = new MifareSectorControl();
        var engine = new SkipTestEngine(control);
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, EncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await new MifareKeyRecovery(client, keyRecovery: engine, sectorControl: control)
            .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested);
        Assert.True(engine.Canceled);
        Assert.Null(dump.KeyA[0]);
        Assert.Null(dump.KeyB[0]);
        Assert.All(dump.KeyA.Skip(1).Concat(dump.KeyB.Skip(1)), k => Assert.Equal(new byte[6], k));
        var session = MifareRecoverySession.For(dump, client);
        Assert.DoesNotContain((0, MfKeyType.A), session.SenExhausted);
        Assert.DoesNotContain((0, MfKeyType.B), session.SenExhausted);
        Assert.False(session.SenCandidates.ContainsKey((0, MfKeyType.A)));

        await new MifareKeyRecovery(client, keyRecovery: new StaticEncryptedTestEngine(), sectorControl: new())
            .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested);
        Assert.All(dump.KeyA.Concat(dump.KeyB), Assert.NotNull);
    }

    [Fact]
    public async Task SkipSector_GlobalStopStillCancelsTheWholeOperation()
    {
        var control = new MifareSectorControl();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.RunAsync(0, stop.Token, token =>
        {
            control.SkipCurrent();
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));
        Assert.Equal(-1, control.CurrentSector);
    }

    private sealed class SkipTestEngine(MifareSectorControl control) : IKeyRecoveryEngine
    {
        public List<int> Sectors { get; } = [];
        public bool Canceled { get; private set; }
        public async Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        {
            if (request.Operation is KeyRecoveryOperation.OrderStaticEncrypted) return request.Keys;
            if (request.Operation is KeyRecoveryOperation.FilterStaticEncrypted) return [];
            Sectors.Add(control.CurrentSector);
            if (Sectors.Count == 1)
            {
                control.SkipCurrent();
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { Canceled = true; throw; }
            }
            return [0];
        }
    }

    private sealed class SkipHardnestedEngine(SkipTestEngine engine) : IHardnestedEngine
    {
        public string? UnavailableReason => null;
        public async Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct) =>
            (await engine.RecoverAsync(new(KeyRecoveryOperation.Nested), ct))[0];
    }
}
