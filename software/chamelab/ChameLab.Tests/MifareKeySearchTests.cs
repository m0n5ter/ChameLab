using System;
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
    private static int[] RequestedPairs(ChameleonFrame request) => request.Command == Command.Mf1AuthOneKeyBlock
        ? [MifareClassic.SectorOfBlock(request.Data[1]) * 2 + (request.Data[0] == (byte)MfKeyType.A ? 0 : 1)]
        : Enumerable.Range(0, 80).Where(bit => (request.Data[bit / 8] & (0x80 >> (bit % 8))) == 0).ToArray();

    [Theory]
    [InlineData(MifareKeySearchMethod.SingleKey)]
    [InlineData(MifareKeySearchMethod.AllSectors)]
    [InlineData(MifareKeySearchMethod.OneSector)]
    public async Task ConfiguredSearch_StartsAtSelectedSectorAndWrapsWithoutOmittingAnyPair(MifareKeySearchMethod method)
    {
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = BatchTransport((_, _) => null);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1AuthOneKeyBlock ? (Status.MfErrAuth, []) : reply(r);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(target, [Key], options:
            new(method, MifareKeySearchStart.Selected, SelectedSector: 3));

        var checks = transport.Requests.Where(r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1CheckKeysOfSectors).ToArray();
        Assert.Equal(new[] { 6, 7, 8, 9, 0, 1, 2, 3, 4, 5 }, checks.SelectMany(RequestedPairs));
        Assert.All(checks, r => Assert.Equal(method == MifareKeySearchMethod.SingleKey,
            r.Command == Command.Mf1AuthOneKeyBlock));
    }

    [Fact]
    public async Task ConfiguredSearch_RandomOrderKeepsSelectedStartAndCoversEveryPair()
    {
        var transport = BatchTransport((_, _) => null);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(
            new(MifareClassicType.Mini, [1, 2, 3, 4]), [Key], options:
            new(MifareKeySearchMethod.AllSectors, MifareKeySearchStart.Selected, MifareKeySearchOrder.Random, 3));

        var visited = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors).SelectMany(RequestedPairs).ToArray();
        Assert.Equal(6, visited[0]);
        Assert.Equal(Enumerable.Range(0, 10), visited.Order());
    }

    [Theory]
    [InlineData(TransportKind.Serial, 8)]
    [InlineData(TransportKind.Tcp, 8)]
    [InlineData(TransportKind.Ble, 32)]
    public async Task ConfiguredSearch_OneSectorFindsSeedThenReusesItBeforeContinuingDictionary(TransportKind kind, int size)
    {
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = BatchTransport((_, _) => Key, kind);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var dictionary = DistinctKeys(100);
        dictionary[6] = Key;
        var ticks = new System.Collections.Generic.List<MifareKeyProgress>();
        await new ChameleonClient(connection).FindMifareKeysAsync(target, dictionary,
            keyProgress: new CallbackProgress<MifareKeyProgress>(ticks.Add), options:
            new(MifareKeySearchMethod.OneSector, MifareKeySearchStart.Selected, SelectedSector: 0));

        var batches = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(new[] { 0 }, RequestedPairs(batches[0]));
        Assert.Equal(size, (batches[0].Data.Length - 10) / 6);
        Assert.Equal(Key, batches[1].Data[10..]);
        Assert.All(target.KeyA.Concat(target.KeyB), k => Assert.Equal(Key, k));
        Assert.All(ticks.GroupBy(p => (p.Sector, p.KeyType)), group => Assert.Equal(MifareKeyPhase.Found, group.Last().Phase));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
    }

    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors)]
    [InlineData(MifareKeySearchMethod.OneSector)]
    public async Task ConfiguredSearch_OldFirmwareFallsBackAndHonorsSelectedStart(MifareKeySearchMethod method)
    {
        var transport = new CardTransport { Response = r => r.Command == Command.Mf1AuthOneKeyBlock ? (Status.MfErrAuth, []) : null };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(
            new(MifareClassicType.Mini, [1, 2, 3, 4]), DistinctKeys(2), options:
            new(method, MifareKeySearchStart.Selected, SelectedSector: 2));
        var auth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock).ToArray();
        Assert.Equal(20, auth.Length);
        Assert.Equal(8, auth[0].Data[1]);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
    }

    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors)]
    [InlineData(MifareKeySearchMethod.OneSector)]
    public async Task ConfiguredSearch_CancellationKeepsFoundKeyAndDrainsCurrentReply(MifareKeySearchMethod method)
    {
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = BatchTransport((_, _) => Key);
        using var stop = new CancellationTokenSource();
        transport.OnRequest = r => { if (r.Command == Command.Mf1CheckKeysOfSectors) stop.Cancel(); };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindMifareKeysAsync(target, [Key],
            ct: stop.Token, options: new(method)));
        Assert.Equal(Key, target.KeyA[0]);
        await client.SetDeviceModeAsync(DeviceMode.Tag);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
    }

    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Serial, 8, 5)]
    [InlineData(MifareKeySearchMethod.AllSectors, TransportKind.Ble, 32, 2)]
    [InlineData(MifareKeySearchMethod.OneSector, TransportKind.Serial, 8, 400)]
    [InlineData(MifareKeySearchMethod.OneSector, TransportKind.Ble, 32, 160)]
    public async Task ConfiguredSearch_UsesTransportBatchSizeEvenWithAll80PairsUnknown(
        MifareKeySearchMethod method, TransportKind kind, int size, int commands)
    {
        var target = new MifareDump(MifareClassicType.Classic4K, [1, 2, 3, 4]);
        var transport = BatchTransport((_, _) => null, kind);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(target, DistinctKeys(40), options: new(method));
        var batches = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors).ToArray();
        Assert.Equal(commands, batches.Length);
        Assert.Equal(size, (batches[0].Data.Length - 10) / 6);
        Assert.All(batches, batch => Assert.True((batch.Data.Length - 10) / 6 is 8 || (batch.Data.Length - 10) / 6 == size));
        Assert.All(target.KeyA.Concat(target.KeyB), Assert.Null);
    }

    [Fact]
    public async Task ConfiguredRead_SearchesOnlyMissingDataAndDoesNotRepeatDictionary()
    {
        var target = KnownCard();
        for (var b = 0; b < target.BlockCount; b++) target.SetBlock(b, new byte[16]);
        target.SetBlock(17, null);
        target.KeyB[4] = null;
        var transport = BatchTransport((_, _) => Key);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).ReadMifareClassicAsync([Key], type: target.Type, existing: target,
            keySearch: new(MifareKeySearchMethod.OneSector));
        Assert.Equal(20, target.RecoveredBlocks);
        Assert.Equal(new[] { 9 }, RequestedPairs(Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors)));
        Assert.Equal(17, Assert.Single(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock).Data[1]);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
    }

    [Fact]
    public async Task ConfiguredSearch_ReusesTrailerKeyThatWasNotInDictionary()
    {
        var extra = Convert.FromHexString("112233445566");
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var first = true;
        var transport = BatchTransport((s, _) => s == 0 ? Key : extra);
        var reply = transport.Response!;
        transport.Response = r =>
        {
            if (r.Command != Command.Mf1CheckKeysOfSectors || !first) return reply(r);
            first = false;
            var data = BatchKeyResponse(r, (s, _) => s == 0 ? Key : extra);
            data[0] |= 0x40; // key A opened sector 0 and the trailer disclosed a different B
            extra.CopyTo(data, 16);
            return (Status.HfTagOk, data);
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(target, [Key], options: new());
        Assert.Equal(extra, target.KeyB[0]);
        Assert.All(target.KeyA.Skip(1).Concat(target.KeyB.Skip(1)), k => Assert.Equal(extra, k));
        var batches = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(extra, batches[1].Data[10..]);
    }

    [Fact]
    public async Task ConfiguredSearch_DoesNotRetryDictionaryKeyAlreadyCheckedAcrossAllSectors()
    {
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = BatchTransport((s, type) => s == 0 && type == MfKeyType.A ? Key : null);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).FindMifareKeysAsync(target, [Key], options: new());
        Assert.Equal(Key, target.KeyA[0]);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
    }

    [Theory]
    [InlineData(MifareKeySearchMethod.AllSectors, true)]
    [InlineData(MifareKeySearchMethod.AllSectors, false)]
    [InlineData(MifareKeySearchMethod.OneSector, true)]
    [InlineData(MifareKeySearchMethod.OneSector, false)]
    public async Task ConfiguredSearch_LinkFailureNeverStartsSingleKeyFallback(MifareKeySearchMethod method, bool timedOut)
    {
        var transport = BatchTransport((_, _) => null);
        transport.OnRequest = r =>
        {
            if (r.Command != Command.Mf1CheckKeysOfSectors) return;
            if (timedOut) throw new TimeoutException();
            throw new ChameleonDisconnectedException();
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        if (timedOut) await Assert.ThrowsAsync<TimeoutException>(() => client.FindMifareKeysAsync(target, [Key], options: new(method)));
        else await Assert.ThrowsAsync<ChameleonDisconnectedException>(() => client.FindMifareKeysAsync(target, [Key], options: new(method)));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
    }
}
