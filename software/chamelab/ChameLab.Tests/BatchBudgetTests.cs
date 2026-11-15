using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public partial class MifareWorkflowTests
{
    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Serial, 8, false)]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Ble, 32, false)]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Tcp, 8, true)]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Ble, 32, true)]
    [InlineData(MifareKeySearchMethod.OneSector, TransportKind.Serial, 8, false)]
    [InlineData(MifareKeySearchMethod.OneSector, TransportKind.Ble, 32, true)]
    [InlineData(MifareKeySearchMethod.SingleKey, TransportKind.Serial, 8, false)]
    [InlineData(MifareKeySearchMethod.SingleKey, TransportKind.Ble, 32, true)]
    public async Task BatchBudget_AllModesBoundChecksAndVisitEveryCandidateInSectorOrder(
        MifareKeySearchMethod method, TransportKind kind, int limit, bool random)
    {
        var dump = new MifareDump(MifareClassicType.Classic4K, [1, 2, 3, 4]);
        var keys = DistinctKeys(35);
        var transport = BatchTransport((_, _) => null, kind);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1AuthOneKeyBlock ? (Status.MfErrAuth, []) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var options = new MifareKeySearchOptions(method, MifareKeySearchStart.Selected,
            random ? MifareKeySearchOrder.Random : MifareKeySearchOrder.Sequential, 37);

        await new ChameleonClient(connection).FindMifareKeysAsync(dump, keys, options: options);

        var checks = transport.Requests.Where(r => r.Command is Command.Mf1CheckKeysOfSectors or Command.Mf1AuthOneKeyBlock).ToArray();
        Assert.All(checks, r => Assert.InRange(RequestedPairs(r).Length * RequestedKeys(r).Length, 1, limit));
        var attempts = checks.SelectMany(r => RequestedPairs(r)
            .SelectMany(pair => RequestedKeys(r).Select(key => (Pair: pair, Key: Convert.ToHexString(key))))).ToArray();
        Assert.Equal(80 * keys.Length, attempts.Length);
        Assert.Equal(attempts.Length, attempts.Distinct().Count());
        var firstOrder = attempts.Where(a => a.Key == Convert.ToHexString(keys[0])).Select(a => a.Pair).ToArray();
        Assert.Equal(74, firstOrder[0]);
        Assert.Equal(Enumerable.Range(0, 80), firstOrder.Order());
        Assert.All(keys, key => Assert.Equal(firstOrder,
            attempts.Where(a => a.Key == Convert.ToHexString(key)).Select(a => a.Pair)));
        if (!random) Assert.Equal(options.GetSectorOrder(40).SelectMany(s => new[] { s * 2, s * 2 + 1 }), firstOrder);
    }

    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Serial, 8)]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Ble, 32)]
    [InlineData(MifareKeySearchMethod.OneSector, TransportKind.Serial, 8)]
    [InlineData(MifareKeySearchMethod.OneSector, TransportKind.Ble, 32)]
    public async Task BatchBudget_CancelDrainsOnlyTheCurrentBoundedRequestAndReportsOnlyItsPairs(
        MifareKeySearchMethod method, TransportKind kind, int limit)
    {
        var dump = new MifareDump(MifareClassicType.Classic4K, [1, 2, 3, 4]);
        var transport = BatchTransport((_, _) => Key, kind);
        using var stop = new CancellationTokenSource();
        var ticks = new List<MifareKeyProgress>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnRequest = r => { if (r.Command == Command.Mf1CheckKeysOfSectors) stop.Cancel(); };
        transport.BeforeReadReply = async reply =>
        {
            if (new FrameParser().Push(reply).Single().Command != Command.Mf1CheckKeysOfSectors) return;
            waiting.TrySetResult();
            await release.Task;
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var search = client.FindMifareKeysAsync(dump, new[] { Key }.Concat(DistinctKeys(40)).ToArray(),
            new CallbackProgress<MifareKeyProgress>(ticks.Add), stop.Token, new(method));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
        try
        {
            Assert.False(search.IsCompleted);
            Assert.InRange(RequestedPairs(request).Length * RequestedKeys(request).Length, 1, limit);
            Assert.Equal(RequestedPairs(request), ticks.Where(p => p.Phase == MifareKeyPhase.Searching)
                .Select(p => p.Sector * 2 + (p.KeyType == MfKeyType.A ? 0 : 1)));
            Assert.All(dump.KeyA.Concat(dump.KeyB), Assert.Null);
        }
        finally { release.TrySetResult(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        for (var bit = 0; bit < 80; bit++)
            Assert.Equal(RequestedPairs(request).Contains(bit) ? Key : null,
                bit % 2 == 0 ? dump.KeyA[bit / 2] : dump.KeyB[bit / 2]);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.Single(await client.Hf14aScanAsync());
    }

    [Fact]
    public async Task BatchBudget_RetainedRejectionsDoNotReorderInterveningSectors()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var session = new MifareRecoverySession();
        var keys = DistinctKeys(2);
        session.Rejected.Add((MifareClassic.SectorTrailerBlock(1), MfKeyType.A, MifareRecoverySession.Value(keys[0])));
        var transport = BatchTransport((_, _) => null);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new ChameleonClient(connection).FindMifareKeysAsync(dump, keys, options: new() { Session = session });

        var checks = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors).ToArray();
        var secondKeyPairs = checks.Where(r => RequestedKeys(r).Any(k => k.SequenceEqual(keys[1]))).SelectMany(RequestedPairs);
        Assert.Equal(Enumerable.Range(0, 10), secondKeyPairs);
        Assert.DoesNotContain(checks, r => RequestedPairs(r).Contains(2) && RequestedKeys(r).Any(k => k.SequenceEqual(keys[0])));
    }

    [Theory]
    [InlineData(TransportKind.Serial, 8)]
    [InlineData(TransportKind.Ble, 32)]
    public async Task BatchBudget_SingleBlockCandidatesUseTheSameLimitAndKeepTheirOrder(TransportKind kind, int limit)
    {
        var keys = DistinctKeys(70);
        var transport = BatchTransport((_, _) => null, kind);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1CheckKeysOnBlock ? (Status.MfErrAuth, []) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        Assert.Null(await new ChameleonClient(connection).CheckMifareKeysOnBlockAsync(3, MfKeyType.A, keys));

        var checks = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOnBlock).ToArray();
        Assert.All(checks, r => Assert.InRange((int)r.Data[2], 1, limit));
        Assert.Equal(keys.Select(Convert.ToHexString), checks.SelectMany(r => r.Data[3..].Chunk(6)).Select(Convert.ToHexString));
    }

    private static byte[][] RequestedKeys(ChameleonFrame request) => request.Command == Command.Mf1AuthOneKeyBlock
        ? [request.Data[2..]] : request.Data[10..].Chunk(6).ToArray();
}
