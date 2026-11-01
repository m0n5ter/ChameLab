using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class MifareWorkflowTests
{
    private static readonly byte[] Key = Convert.FromHexString("FFFFFFFFFFFF");

    private static MifareDump KnownCard()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        for (var s = 0; s < dump.SectorCount; s++)
        {
            dump.KeyA[s] = (byte[])Key.Clone();
            dump.KeyB[s] = (byte[])Key.Clone();
        }
        return dump;
    }

    [Fact]
    public async Task Dump_OnlySearchesMissingKeysAndReadsMissingBlocks()
    {
        var dump = KnownCard();
        for (var b = 0; b < dump.BlockCount; b++) dump.SetBlock(b, new byte[16]);
        var keptBlock = dump.GetBlock(0);
        dump.KeyB[4] = null;
        dump.SetBlock(17, null);
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        var result = await client.ReadMifareClassicAsync([Key], type: dump.Type, existing: dump);

        Assert.Same(dump, result);
        Assert.Same(keptBlock, result.GetBlock(0));
        Assert.Equal(20, result.RecoveredBlocks);
        var auth = Assert.Single(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
        Assert.Equal(new byte[] { (byte)MfKeyType.B, 16 }, auth.Data[..2]);
        var read = Assert.Single(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock);
        Assert.Equal(17, read.Data[1]);
        Assert.Equal(Key, result.KeyB[4]);
    }

    [Fact]
    public async Task Dump_SavedSectorsWithoutAuthenticatedKeysAreSkippedEntirely()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        for (var block = 0; block < dump.BlockCount; block++) dump.SetBlock(block, new byte[16]);
        dump.SetBlock(17, null);
        var kept = dump.GetBlock(0);
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new ChameleonClient(connection).ReadMifareClassicAsync([Key],
            type: dump.Type, existing: dump);

        Assert.Equal(20, result.RecoveredBlocks);
        Assert.Same(kept, result.GetBlock(0));
        var auth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock).ToList();
        Assert.NotEmpty(auth);
        Assert.All(auth, request => Assert.Equal(16, request.Data[1]));
        var read = Assert.Single(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock);
        Assert.Equal(17, read.Data[1]);
        Assert.Null(result.KeyA[0]);
        Assert.Null(result.KeyB[0]);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task Dump_ProgressCountsUniqueKnownKeysAndReportsReadingBeforeIo(bool knownWorks, int attempts)
    {
        var dump = KnownCard();
        for (var block = 0; block < dump.BlockCount; block++) dump.SetBlock(block, new byte[16]);
        dump.KeyB[4] = null;
        dump.SetBlock(17, null);
        var other = Convert.FromHexString("112233445566");
        var accepted = knownWorks ? Key : other;
        var keys = new List<MifareKeyProgress>();
        var blocks = new List<MifareBlockProgress>();
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (request.Data[2..].SequenceEqual(accepted) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>())
                : null,
        };
        transport.OnRequest = request =>
        {
            if (request.Command == Command.Mf1ReadOneBlock)
                Assert.Contains(blocks, p => p.First == 17 && p.Count == 1 && p.State == MifareBlockState.Reading);
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new ChameleonClient(connection).ReadMifareClassicAsync([Key, Key, new byte[6], new byte[6], other],
            type: dump.Type, existing: dump,
            keyProgress: new CallbackProgress<MifareKeyProgress>(keys.Add),
            blocks: new CallbackProgress<MifareBlockProgress>(blocks.Add));

        Assert.All(keys, p => Assert.Equal(3, p.Total));
        Assert.Equal(Enumerable.Range(0, attempts + 1), keys.Select(p => p.Tried));
        var found = Assert.Single(keys, p => p.Phase == MifareKeyPhase.Found);
        Assert.Equal(attempts, found.Tried);
        Assert.Equal(attempts, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_ResumesOnlyUnconfirmedBlocksAndRejectsAnotherUid(bool changedCard)
    {
        var source = KnownCard();
        for (var b = 0; b < source.BlockCount; b++) source.SetBlock(b, MifareClassic.BlankBlock(b));
        var session = new MifareWriteSession(source, MagicGeneration.Gen2, targetKeys: KnownCard());
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport
        {
            OnRequest = r => { if (r.Command == Command.Mf1WriteOneBlock && r.Data[1] == 5) stop.Cancel(); },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WriteMifareDumpToCardAsync(source, [], ct: stop.Token, session: session));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, session.WrittenBlocks.Order());
        var confirmed = session.WrittenBlocks.ToArray();
        transport.OnRequest = null;
        transport.Requests.Clear();
        if (changedCard)
        {
            transport.Response = r => r.Command == Command.Hf14aScan
                ? (Status.HfTagOk, new byte[] { 4, 9, 8, 7, 6, 4, 0, 9, 0 }) : null;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.WriteMifareDumpToCardAsync(source, [], session: session));
            Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1WriteOneBlock);
            Assert.Equal(confirmed, session.WrittenBlocks);
        }
        else
        {
            var failed = await client.WriteMifareDumpToCardAsync(source, [], session: session);
            Assert.Empty(failed);
            Assert.True(session.IsComplete);
            var writes = transport.Requests.Where(r => r.Command == Command.Mf1WriteOneBlock).ToArray();
            Assert.Equal(14, writes.Length);
            Assert.DoesNotContain(writes, r => confirmed.Contains(r.Data[1]));
        }
    }

    [Theory]
    [InlineData(MagicGeneration.Gen2)]
    [InlineData(MagicGeneration.Gen3)]
    public async Task Write_ResumeAcceptsUidChangedByConfirmedBlock0(MagicGeneration generation)
    {
        var source = KnownCard();
        for (var b = 0; b < source.BlockCount; b++) source.SetBlock(b, MifareClassic.BlankBlock(b));
        var uid = new byte[] { 1, 2, 3, 4 };
        var replacement = new byte[] { 9, 8, 7, 6 };
        replacement.CopyTo(source.GetBlock(0)!, 0);
        var session = new MifareWriteSession(source, generation, true, KnownCard());
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport
        {
            Response = r => r.Command == Command.Hf14aScan
                ? (Status.HfTagOk, new byte[] { 4 }.Concat(uid).Concat(new byte[] { 4, 0, 9, 0 }).ToArray()) : null,
            OnRequest = r =>
            {
                if (r.Command == Command.Hf14aRaw)
                {
                    var start = r.Data.AsSpan().IndexOf(new byte[] { 0x90, 0xFB, 0xCC, 0xCC });
                    if (start >= 0) uid = r.Data[(start + 5)..(start + 9)];
                }
                if (r.Command != Command.Mf1WriteOneBlock) return;
                if (r.Data[1] == 0) uid = r.Data[8..12];
                if (r.Data[1] == 1) stop.Cancel();
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WriteMifareDumpToCardAsync(source, [], generation,
            ct: stop.Token, writeGen2Block0: true, session: session));
        Assert.Equal(replacement, session.TargetKeys!.Uid);
        Assert.Equal(new[] { 0, 1 }, session.WrittenBlocks.Order());
        transport.OnRequest = null;
        transport.Requests.Clear();

        Assert.Empty(await client.WriteMifareDumpToCardAsync(source, [], generation, writeGen2Block0: true, session: session));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock && r.Data[1] == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_ResumeUsesKeysChangedByTrailer(bool acknowledged)
    {
        var source = KnownCard();
        var replacement = Convert.FromHexString("112233445566");
        for (var b = 0; b < source.BlockCount; b++)
        {
            var data = MifareClassic.BlankBlock(b);
            if (MifareClassic.IsTrailer(b)) { replacement.CopyTo(data, 0); replacement.CopyTo(data, 10); }
            source.SetBlock(b, data);
        }
        Array.Clear(source.KeyA);
        Array.Clear(source.KeyB);
        // Match an imported dump: key bytes exist in trailers, but were not authenticated on the source.
        var session = new MifareWriteSession(source, MagicGeneration.Gen2, targetKeys: KnownCard());
        var liveKeys = Enumerable.Range(0, 5).Select(_ => Key).ToArray();
        var retry = false;
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport
        {
            Response = r =>
            {
                if (r.Command == Command.Mf1AuthOneKeyBlock)
                    return (r.Data[2..].SequenceEqual(liveKeys[r.Data[1] / 4]) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>());
                if (r.Command != Command.Mf1WriteOneBlock) return null;
                if (r.Data[1] == 1 && !retry) return (Status.MfErrAuth, Array.Empty<byte>());
                if (MifareClassic.IsTrailer(r.Data[1])) liveKeys[r.Data[1] / 4] = r.Data[8..14];
                if (r.Data[1] == 3 && !acknowledged && !retry) return (Status.HfTagNo, Array.Empty<byte>());
                return (Status.HfTagOk, Array.Empty<byte>());
            },
            OnRequest = r => { if (r.Command == Command.Mf1WriteOneBlock && r.Data[1] == (acknowledged ? 4 : 3)) stop.Cancel(); },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WriteMifareDumpToCardAsync(source, [], ct: stop.Token, session: session));
        Assert.Equal(acknowledged, session.WrittenBlocks.Contains(3));
        Assert.Equal(acknowledged ? replacement : Key, session.TargetKeys!.KeyA[0]);
        retry = true;
        transport.OnRequest = null;
        transport.Requests.Clear();

        Assert.Empty(await client.WriteMifareDumpToCardAsync(source, [], session: session));
        var retriedBlock = Assert.Single(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock && r.Data[1] == 1);
        Assert.Equal(replacement, retriedBlock.Data[2..8]);
        Assert.Equal(!acknowledged, transport.Requests.Any(r => r.Command == Command.Mf1WriteOneBlock && r.Data[1] == 3));
    }

    [Fact]
    public async Task Gen1a_ResumeSkipsAcknowledgedBlocks()
    {
        var source = KnownCard();
        for (var b = 0; b < source.BlockCount; b++) source.SetBlock(b, MifareClassic.BlankBlock(b));
        source.Uid.CopyTo(source.GetBlock(0)!, 0);
        var session = new MifareWriteSession(source, MagicGeneration.Gen1a);
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport
        {
            Response = r => r.Command == Command.Hf14aRaw ? (Status.HfTagOk, new byte[] { 0x0A }) : null,
            OnRequest = r => { if (r.Command == Command.Hf14aRaw && r.Data.AsSpan().EndsWith(new byte[] { 0xA0, 2 })) stop.Cancel(); },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WriteMifareDumpToCardAsync(source, [], MagicGeneration.Gen1a, ct: stop.Token, session: session));
        Assert.Equal(new[] { 0, 1, 2 }, session.WrittenBlocks.Order());
        transport.OnRequest = null;
        transport.Requests.Clear();

        Assert.Empty(await client.WriteMifareDumpToCardAsync(source, [], MagicGeneration.Gen1a, session: session));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Hf14aRaw
            && (r.Data.AsSpan().EndsWith(new byte[] { 0xA0, 0 }) || r.Data.AsSpan().EndsWith(new byte[] { 0xA0, 1 })));
    }

    [Theory]
    [InlineData(false, 19)]
    [InlineData(true, 20)]
    public async Task Gen2_Block0OptionControlsWritesAndProgress(bool enabled, int count)
    {
        var dump = KnownCard();
        for (var b = 0; b < dump.BlockCount; b++) dump.SetBlock(b, new byte[16]);
        var progress = new List<MifareProgress>();
        var blocks = new List<MifareBlockProgress>();
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var failed = await new ChameleonClient(connection).WriteMifareDumpToCardAsync(dump, [],
            progress: new CallbackProgress<MifareProgress>(progress.Add),
            blocks: new CallbackProgress<MifareBlockProgress>(blocks.Add), writeGen2Block0: enabled);

        Assert.Empty(failed);
        var writes = transport.Requests.Where(r => r.Command == Command.Mf1WriteOneBlock).ToArray();
        Assert.Equal(count, writes.Length);
        Assert.Equal(enabled, writes.Any(r => r.Data[1] == 0));
        Assert.Equal(enabled, blocks.Any(p => p.First == 0 && p.State == MifareBlockState.Reading));
        Assert.Equal(count, progress[^1].Done);
        Assert.Equal(count, progress[^1].Total);
    }

    [Fact]
    public async Task Gen2_DefaultSkipsBlock0WithoutTryingKeysForAnOtherwiseEmptySector()
    {
        var dump = KnownCard();
        dump.SetBlock(0, new byte[16]);
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var failed = await new ChameleonClient(connection).WriteMifareDumpToCardAsync(dump, [Key]);

        Assert.Empty(failed);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1WriteOneBlock);
    }

    [Theory]
    [InlineData(MagicGeneration.Gen1a)]
    [InlineData(MagicGeneration.Gen3)]
    public async Task OtherGenerationsStillWriteBlock0WithGen2OptionOff(MagicGeneration generation)
    {
        var dump = KnownCard();
        var data = new byte[16];
        dump.Uid.CopyTo(data, 0);
        dump.SetBlock(0, data);
        var transport = new CardTransport
        {
            Response = r => r.Command == Command.Hf14aRaw ? (Status.HfTagOk, new byte[] { 0x0A }) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var failed = await new ChameleonClient(connection).WriteMifareDumpToCardAsync(dump, [], generation);

        Assert.Empty(failed);
        Assert.Contains(transport.Requests, r => r.Command == Command.Hf14aRaw
            && (generation == MagicGeneration.Gen1a
                ? r.Data.AsSpan().EndsWith(new byte[] { 0xA0, 0x00 })
                : r.Data.AsSpan().IndexOf(new byte[] { 0x90, 0xFB, 0xCC, 0xCC }) >= 0));
    }

    [Fact]
    public async Task KeySearch_DoesNotReadOrWriteMemoryAndReusesRecoveredKeys()
    {
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var ticks = new List<MifareKeyProgress>();
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (request.Data[2..].SequenceEqual(Key) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>())
                : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        await client.FindMifareKeysAsync(target, [new byte[6], Key],
            new CallbackProgress<MifareKeyProgress>(ticks.Add));

        Assert.All(target.KeyA.Concat(target.KeyB), key => Assert.Equal(Key, key));
        Assert.Equal(10, ticks.Count(p => p.Phase == MifareKeyPhase.Found));
        Assert.Equal(0, target.RecoveredBlocks);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1ReadOneBlock or Command.Mf1WriteOneBlock);
        transport.Requests.Clear();
        await client.FindMifareKeysAsync(target, []);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
    }

    [Fact]
    public async Task Write_UsesRecoveredTargetKeysWithoutChangingSourceDump()
    {
        var source = KnownCard();
        source.SetBlock(1, new byte[16]);
        var targetKey = Convert.FromHexString("112233445566");
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        target.KeyB[0] = targetKey;
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (request.Data[0] == (byte)MfKeyType.B && request.Data[2..].SequenceEqual(targetKey)
                    ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>()) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var failed = await new ChameleonClient(connection).WriteMifareDumpToCardAsync(source, [], targetKeys: target);

        Assert.Empty(failed);
        var write = Assert.Single(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock);
        Assert.Equal((byte)MfKeyType.B, write.Data[0]);
        Assert.Equal(targetKey, write.Data[2..8]);
        Assert.Equal(Key, source.KeyA[0]);
        Assert.Equal(Key, source.KeyB[0]);
        Assert.Equal(targetKey, target.KeyB[0]);
    }

    [Fact]
    public async Task Recovery_RejectsChangedTargetBeforeSearchingOrWriting()
    {
        var source = KnownCard();
        source.SetBlock(0, new byte[16]);
        var target = new MifareDump(MifareClassicType.Mini, [9, 8, 7, 6]);
        target.KeyA[0] = Key;
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FindMifareKeysAsync(target, [Key]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.WriteMifareDumpToCardAsync(source, [Key], targetKeys: target));

        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1WriteOneBlock);
    }

    [Fact]
    public async Task KeySearch_CancellationKeepsAlreadyRecoveredKeys()
    {
        var target = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var progress = new CallbackProgress<MifareKeyProgress>(p =>
        {
            if (p.Phase == MifareKeyPhase.Found && p.KeyType == MfKeyType.A) stop.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChameleonClient(connection).FindMifareKeysAsync(target, [Key], progress, stop.Token));

        Assert.Equal(Key, target.KeyA[0]);
        Assert.Null(target.KeyB[0]);
        Assert.Equal(0, target.RecoveredBlocks);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1ReadOneBlock or Command.Mf1WriteOneBlock);
    }

    [Fact]
    public async Task Write_SearchesTargetKeysOncePerSectorAndKeepsSourceKeys()
    {
        var dump = KnownCard();
        for (var block = 0; block < 4; block++) dump.SetBlock(block, MifareClassic.BlankBlock(block));
        var targetKey = Convert.FromHexString("112233445566");
        var ticks = new List<MifareKeyProgress>();
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (request.Data[2..].SequenceEqual(targetKey) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>())
                : null,
        };
        transport.OnRequest = request =>
        {
            if (request.Command != Command.Mf1WriteOneBlock) return;
            Assert.Equal(targetKey, request.Data[2..8]);
            Assert.Equal(2, ticks.Count(p => p.Phase == MifareKeyPhase.Found));
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var failed = await new ChameleonClient(connection).WriteMifareDumpToCardAsync(dump,
            [Key, Key, targetKey, targetKey], keyProgress: new CallbackProgress<MifareKeyProgress>(ticks.Add));

        Assert.Empty(failed);
        Assert.All(ticks, tick => Assert.Equal(0, tick.Sector));
        Assert.All(ticks, tick => Assert.Equal(2, tick.Total));
        Assert.Equal(4, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
        Assert.Equal(3, transport.Requests.Count(r => r.Command == Command.Mf1WriteOneBlock));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock && r.Data[1] == 0);
        Assert.Equal(Key, dump.KeyA[0]);
        Assert.Equal(Key, dump.KeyB[0]);
    }

    [Fact]
    public async Task Write_MissingTargetKeysReportsNotFoundAndFailedBlocks()
    {
        var dump = KnownCard();
        dump.SetBlock(0, new byte[16]);
        dump.SetBlock(1, new byte[16]);
        var ticks = new List<MifareKeyProgress>();
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (Status.MfErrAuth, Array.Empty<byte>()) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var failed = await new ChameleonClient(connection).WriteMifareDumpToCardAsync(dump, [],
            keyProgress: new CallbackProgress<MifareKeyProgress>(ticks.Add));

        Assert.Equal(new[] { 1 }, failed);
        Assert.Equal(2, ticks.Count(p => p.Phase == MifareKeyPhase.NotFound));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock);
    }

    [Fact]
    public async Task Write_CancellationDuringKeySearchDoesNotWriteBlocks()
    {
        var dump = KnownCard();
        dump.SetBlock(1, new byte[16]);
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport
        {
            OnRequest = request =>
            {
                if (request.Command == Command.Mf1AuthOneKeyBlock) stop.Cancel();
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChameleonClient(connection).WriteMifareDumpToCardAsync(dump, [Key], ct: stop.Token));

        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock);
    }

    [Fact]
    public async Task Dump_CancelKeepsKeysAndBlocksForNextRun()
    {
        var dump = KnownCard();
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport();
        transport.OnRequest = request =>
        {
            if (request.Command == Command.Mf1ReadOneBlock && request.Data[1] == 1) stop.Cancel();
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        await client.ReadMifareClassicAsync([Key], ct: stop.Token, type: dump.Type, existing: dump);
        Assert.NotNull(dump.GetBlock(0));
        Assert.InRange(dump.RecoveredBlocks, 1, 2);
        var keptBlock = dump.GetBlock(0);
        transport.OnRequest = null;
        transport.Requests.Clear();
        await client.ReadMifareClassicAsync([Key], type: dump.Type, existing: dump);

        Assert.Equal(20, dump.RecoveredBlocks);
        Assert.Same(keptBlock, dump.GetBlock(0));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock && r.Data[1] == 0);
    }

    [Theory]
    [InlineData(MifareRecoveryMethod.Nested, MifareClassicPrngType.Weak, Command.Mf1NestedAcquire)]
    [InlineData(MifareRecoveryMethod.StaticNested, MifareClassicPrngType.Static, Command.Mf1StaticNestedAcquire)]
    public async Task Recovery_OnlyAttacksMissingKey(MifareRecoveryMethod method, MifareClassicPrngType prng, Command acquire)
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var transport = new CardTransport
        {
            Prng = prng,
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (Status.MfErrAuth, Array.Empty<byte>()) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump, method);

        var requests = transport.Requests.FindAll(r => r.Command == acquire);
        Assert.Equal(method == MifareRecoveryMethod.StaticNested ? 3 : 1, requests.Count);
        Assert.All(requests, request => Assert.Equal(new byte[] { (byte)MfKeyType.B, 19 }, request.Data[8..]));
        var auth = Assert.Single(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
        Assert.Equal(new byte[] { (byte)MfKeyType.B, 19 }.Concat(Key), auth.Data);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1DarksideAcquire);
        Assert.Equal(Key, dump.KeyA[4]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nested_SkipsGrowingKnownKeyListAndFallsBackIfRecoveryFails(bool nestedSucceeds)
    {
        var other = Convert.FromHexString("111111111111");
        var recovered = Convert.FromHexString("A0A1A2A3A4A5");
        var next = Convert.FromHexString("112233445566");
        var dump = KnownCard();
        dump.KeyB[4] = other;
        dump.KeyB[0] = dump.KeyA[1] = null;
        var clock = new RecoveryClock();
        var transport = TimedNestedTransport(clock, request => request.Data[2..].SequenceEqual(
            request.Data[1] == 3 ? recovered : nestedSucceeds ? next : Key));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new TimedNestedEngine(clock, call => call == 1 ? [0xA0A1A2A3A4A5UL] :
            nestedSucceeds ? [0x112233445566UL] : [0xA0A1A2A3A4A5UL]);
        var updates = new List<KeyRecoveryProgress>();

        await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine, timeProvider: clock)
            .RecoverAsync(dump, MifareRecoveryMethod.Nested, new CallbackProgress<KeyRecoveryProgress>(updates.Add));

        Assert.Equal(recovered, dump.KeyB[0]);
        Assert.Equal(nestedSucceeds ? next : Key, dump.KeyA[1]);
        Assert.Equal(2, engine.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(nestedSucceeds ? 280 : 320), clock.GetElapsedTime(0));
        // First missing key calibrates both paths: 2 * 40 ms for reuse, then
        // 10 + 10 + 40 + 40 = 100 ms for acquisition, solving and verification.
        var firstAuth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock && r.Data[1] == 3);
        Assert.Equal(new[] { Key, other, recovered }.Select(Convert.ToHexString),
            firstAuth.Select(r => Convert.ToHexString(r.Data[2..])));
        // The new unique key raises the next reuse estimate to 120 ms, so the
        // 100 ms attack must start before authenticating any known key on block 7.
        var collect = transport.Requests.FindIndex(r => r.Command == Command.Mf1NestedAcquire && r.Data[9] == 7);
        var authenticate = transport.Requests.FindIndex(r => r.Command == Command.Mf1AuthOneKeyBlock && r.Data[1] == 7);
        Assert.True(collect >= 0 && collect < authenticate);
        var secondAuth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock && r.Data[1] == 7);
        Assert.Equal((nestedSucceeds ? new[] { next } : new[] { recovered, Key }).Select(Convert.ToHexString),
            secondAuth.Select(r => Convert.ToHexString(r.Data[2..])));
        var choice = Assert.Single(updates, p => p.Status == "Using nested instead of checking known keys…");
        Assert.Contains("120 ms", choice.Detail);
        Assert.Contains("100 ms", choice.Detail);
    }

    [Fact]
    public async Task Nested_FailedAttackDoesNotCalibrateAShortRecoveryTime()
    {
        var dump = KnownCard();
        dump.KeyB[4] = Convert.FromHexString("111111111111");
        dump.KeyB[0] = dump.KeyA[1] = null;
        var clock = new RecoveryClock();
        var transport = TimedNestedTransport(clock, r => r.Data[1] == 7 && r.Data[2..].SequenceEqual(Key));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new TimedNestedEngine(clock, _ => []);

        await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine, timeProvider: clock)
            .RecoverAsync(dump, MifareRecoveryMethod.Nested);

        Assert.Null(dump.KeyB[0]);
        Assert.Equal(Key, dump.KeyA[1]);
        Assert.Equal(1, engine.Calls);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1NestedAcquire && r.Data[9] == 7);
    }

    [Fact]
    public async Task Nested_TimingStartsFreshForTheNextRun()
    {
        var dump = KnownCard();
        dump.KeyB[4] = Convert.FromHexString("111111111111");
        dump.KeyB[0] = null;
        var clock = new RecoveryClock();
        var transport = TimedNestedTransport(clock, r => r.Data[2..].SequenceEqual(
            r.Data[1] == 3 ? Convert.FromHexString("A0A1A2A3A4A5") : Key));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new TimedNestedEngine(clock, _ => [0xA0A1A2A3A4A5UL]);
        var recovery = new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine, timeProvider: clock);
        await recovery.RecoverAsync(dump, MifareRecoveryMethod.Nested);
        Assert.Equal(1, engine.Calls);

        dump.KeyA[2] = null;
        transport.Requests.Clear();
        await recovery.RecoverAsync(dump, MifareRecoveryMethod.Nested);

        Assert.Equal(Key, dump.KeyA[2]);
        Assert.Equal(1, engine.Calls);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1NestedAcquire);
        Assert.Equal(11, Assert.Single(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock).Data[1]);
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(int milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);
    }

    private sealed class TimedNestedEngine(RecoveryClock clock, Func<int, IReadOnlyList<ulong>> result) : IKeyRecoveryEngine
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(KeyRecoveryOperation.Nested, request.Operation);
            clock.Advance(40);
            return Task.FromResult(result(++Calls));
        }
    }

    private static CardTransport TimedNestedTransport(RecoveryClock clock, Func<ChameleonFrame, bool> authenticate) => new()
    {
        Response = request =>
        {
            switch (request.Command)
            {
                case Command.Mf1AuthOneKeyBlock:
                    clock.Advance(40);
                    return (authenticate(request) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>());
                case Command.Mf1DetectNtDist:
                    clock.Advance(10);
                    return (Status.HfTagOk, new byte[8]);
                case Command.Mf1NestedAcquire:
                    clock.Advance(10);
                    return (Status.HfTagOk, new byte[18]);
                default:
                    return null;
            }
        }
    };

    [Fact]
    public async Task Hardnested_ReusesNewKeyAndRevisitsEarlierFailuresWithoutRepeatingRejectedKeys()
    {
        var dump = KnownCard();
        dump.KeyB[0] = dump.KeyA[1] = dump.KeyA[2] = dump.KeyB[3] = dump.KeyB[4] = null;
        var store = new MemoryKeyValueStore();
        var known = new KnownCardKeys(store);
        var savedBeforeNextSector = false;
        var counter = 0;
        var transport = new CardTransport
        {
            Prng = MifareClassicPrngType.Hard,
            Response = request => request.Command switch
            {
                Command.GetDeviceCapabilities => (Status.Success, new byte[] { 0x07, 0xdd }),
                Command.Mf1AuthOneKeyBlock => (request.Data[1] is 3 or 11 or 15
                    && request.Data[2..].SequenceEqual(new byte[6]) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>()),
                Command.Mf1HardnestedAcquire when request.Data[10] != 11 => (Status.HfErrStat, Array.Empty<byte>()),
                Command.Mf1HardnestedAcquire => (Status.HfTagOk, HardnestedBatch(ref counter)),
                _ => null,
            },
            OnRequest = request =>
            {
                if (request.Command != Command.Mf1AuthOneKeyBlock || request.Data[1] != 15) return;
                Assert.Equal(new byte[6], Assert.Single(new KnownCardKeys(store).ForUid(dump.Uid)));
                savedBeforeNextSector = true;
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new HardnestedEngine();
        var updates = new List<KeyRecoveryProgress>();

        await new MifareKeyRecovery(new ChameleonClient(connection), engine, known)
            .RecoverAsync(dump, MifareRecoveryMethod.Hardnested,
                new CallbackProgress<KeyRecoveryProgress>(updates.Add));

        Assert.Equal(1, engine.Calls);
        Assert.True(savedBeforeNextSector);
        Assert.Equal(new byte[6], dump.KeyB[0]);
        Assert.Equal(new byte[6], dump.KeyA[2]);
        Assert.Equal(new byte[6], dump.KeyB[3]);
        Assert.Null(dump.KeyA[1]);
        Assert.Null(dump.KeyB[4]);
        Assert.Equal(3, updates.Count(p => p.Mark == KeyMark.Found));
        var auth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock).ToArray();
        Assert.Equal(9, auth.Length);
        Assert.All(auth.GroupBy(r => Convert.ToHexString(r.Data)), group => Assert.Single(group));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1HardnestedAcquire && r.Data[10] == 15);
        var lastAuth = auth[^1];
        Assert.Equal(new byte[] { (byte)MfKeyType.A, 7 }.Concat(new byte[6]), lastAuth.Data);
    }

    [Theory]
    [InlineData(MifareRecoveryMethod.Nested, MifareClassicPrngType.Weak)]
    [InlineData(MifareRecoveryMethod.StaticNested, MifareClassicPrngType.Static)]
    [InlineData(MifareRecoveryMethod.Hardnested, MifareClassicPrngType.Hard)]
    public async Task Recovery_PrioritizesKeysBySuccessfulUsesAcrossSectorsAndTypes(
        MifareRecoveryMethod method, MifareClassicPrngType prng)
    {
        var other = Convert.FromHexString("112233445566");
        var dump = KnownCard();
        dump.KeyB[4] = other;
        dump.KeyA[0] = dump.KeyB[0] = dump.KeyA[1] = dump.KeyB[1] = null;
        var transport = new CardTransport
        {
            Prng = prng,
            Response = request => request.Command switch
            {
                Command.GetDeviceCapabilities => (Status.Success, new byte[] { 0x07, 0xdd }),
                Command.Mf1AuthOneKeyBlock => (request.Data[2..].SequenceEqual(
                    request.Data[1] == 7 && request.Data[0] == (byte)MfKeyType.A ? Key : other)
                    ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>()),
                _ => null,
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection), new HardnestedEngine()).RecoverAsync(dump, method);

        // The second key moves ahead immediately; after two successes it keeps its
        // lead even when the first key subsequently opens another sector.
        var auth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock).ToArray();
        var expected = new[]
        {
            new byte[] { (byte)MfKeyType.A, 3 }.Concat(Key).ToArray(),
            new byte[] { (byte)MfKeyType.A, 3 }.Concat(other).ToArray(),
            new byte[] { (byte)MfKeyType.B, 3 }.Concat(other).ToArray(),
            new byte[] { (byte)MfKeyType.A, 7 }.Concat(other).ToArray(),
            new byte[] { (byte)MfKeyType.A, 7 }.Concat(Key).ToArray(),
            new byte[] { (byte)MfKeyType.B, 7 }.Concat(other).ToArray(),
        };
        Assert.Equal(expected.Select(Convert.ToHexString), auth.Select(r => Convert.ToHexString(r.Data)));
        Assert.Equal(other, dump.KeyA[0]);
        Assert.Equal(other, dump.KeyB[0]);
        Assert.Equal(Key, dump.KeyA[1]);
        Assert.Equal(other, dump.KeyB[1]);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1NestedAcquire
            or Command.Mf1StaticNestedAcquire or Command.Mf1HardnestedAcquire);
    }

    [Fact]
    public async Task Recovery_PreservesOriginalKeyOrderWhenSuccessCountsAreEqual()
    {
        var other = Convert.FromHexString("112233445566");
        var dump = KnownCard();
        dump.KeyB[4] = other;
        dump.KeyA[0] = dump.KeyB[0] = dump.KeyA[1] = null;
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (request.Data[2..].SequenceEqual(request.Data[1] == 3 && request.Data[0] == (byte)MfKeyType.A
                    ? other : Key) ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>()) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump, MifareRecoveryMethod.Nested);

        var auth = transport.Requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock).ToArray();
        Assert.Equal(5, auth.Length);
        Assert.Equal(new byte[] { (byte)MfKeyType.A, 7 }.Concat(Key), auth[^1].Data);
        Assert.Equal(Key, dump.KeyA[1]);
    }

    [Theory]
    [InlineData(MifareRecoveryMethod.Nested, MifareClassicPrngType.Weak)]
    [InlineData(MifareRecoveryMethod.StaticNested, MifareClassicPrngType.Static)]
    [InlineData(MifareRecoveryMethod.Hardnested, MifareClassicPrngType.Hard)]
    public async Task Recovery_TriesUniqueExistingKeysForBothTypesBeforeStartingAnyAttack(
        MifareRecoveryMethod method, MifareClassicPrngType prng)
    {
        var dump = KnownCard();
        dump.KeyA[0] = dump.KeyB[0] = dump.KeyA[2] = dump.KeyB[4] = null;
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var changes = 0;
        known.Changed += () => changes++;
        var transport = new CardTransport
        {
            Prng = prng,
            Response = request => request.Command == Command.GetDeviceCapabilities
                ? (Status.Success, new byte[] { 0x07, 0xdd }) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new HardnestedEngine();

        await new MifareKeyRecovery(new ChameleonClient(connection), engine, known).RecoverAsync(dump, method);

        Assert.All(dump.KeyA.Concat(dump.KeyB), key => Assert.Equal(Key, key));
        Assert.Equal(0, engine.Calls);
        Assert.Equal(4, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1NestedAcquire
            or Command.Mf1StaticNestedAcquire or Command.Mf1HardnestedAcquire or Command.Mf1DetectNtDist);
        Assert.Equal(Key, Assert.Single(known.ForUid(dump.Uid)));
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(Status.HfTagNo)]
    [InlineData(Status.HfErrCrc)]
    [InlineData(Status.HfErrParity)]
    public async Task Recovery_DoesNotCacheRfFailuresAsWrongKeys(Status failure)
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var attempts = 0;
        var transport = new CardTransport
        {
            Response = request => request.Command == Command.Mf1AuthOneKeyBlock
                ? (++attempts <= 4 ? failure : Status.HfTagOk, Array.Empty<byte>()) : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump, MifareRecoveryMethod.Nested);

        Assert.Equal(Key, dump.KeyB[4]);
        Assert.Equal(5, attempts);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1NestedAcquire);
    }

    [Fact]
    public async Task Recovery_PersistsConfirmedKeyBeforeCancellationInNextSector()
    {
        var dump = KnownCard();
        dump.KeyB[0] = dump.KeyA[1] = null;
        var store = new MemoryKeyValueStore();
        var known = new KnownCardKeys(store);
        using var stop = new CancellationTokenSource();
        var counter = 0;
        var transport = new CardTransport
        {
            Prng = MifareClassicPrngType.Hard,
            Response = request => request.Command switch
            {
                Command.GetDeviceCapabilities => (Status.Success, new byte[] { 0x07, 0xdd }),
                Command.Mf1HardnestedAcquire => (Status.HfTagOk, HardnestedBatch(ref counter)),
                Command.Mf1AuthOneKeyBlock => (request.Data[2..].SequenceEqual(new byte[6])
                    ? Status.HfTagOk : Status.MfErrAuth, Array.Empty<byte>()),
                _ => null,
            },
            OnRequest = request =>
            {
                if (request.Command != Command.Mf1AuthOneKeyBlock || request.Data[1] != 7) return;
                Assert.Equal(new byte[6], Assert.Single(new KnownCardKeys(store).ForUid(dump.Uid)));
                stop.Cancel();
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), new HardnestedEngine(), known)
                .RecoverAsync(dump, MifareRecoveryMethod.Hardnested, ct: stop.Token));

        Assert.Equal(new byte[6], dump.KeyB[0]);
        Assert.Null(dump.KeyA[1]);
        Assert.Equal(new byte[6], Assert.Single(new KnownCardKeys(store).ForUid(dump.Uid)));
    }

    [Theory]
    [InlineData(0UL, 2)]
    [InlineData(0xFFFFFFFFFFFFUL, 1)]
    public async Task Recovery_DoesNotRememberUnconfirmedCandidatesOrRepeatRejectedKey(ulong candidate, int authCount)
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var counter = 0;
        var transport = new CardTransport
        {
            Prng = MifareClassicPrngType.Hard,
            Response = request => request.Command switch
            {
                Command.GetDeviceCapabilities => (Status.Success, new byte[] { 0x07, 0xdd }),
                Command.Mf1HardnestedAcquire => (Status.HfTagOk, HardnestedBatch(ref counter)),
                Command.Mf1AuthOneKeyBlock => (Status.MfErrAuth, Array.Empty<byte>()),
                _ => null,
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection), new HardnestedEngine { Result = candidate }, known)
            .RecoverAsync(dump, MifareRecoveryMethod.Hardnested);

        Assert.Null(dump.KeyB[4]);
        Assert.Empty(known.ForUid(dump.Uid));
        Assert.Equal(authCount, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
    }

    [Fact]
    public async Task Darkside_ProbesOnlyUnknownKeys()
    {
        var dump = KnownCard();
        dump.KeyA[4] = null;
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump, MifareRecoveryMethod.Darkside);

        var request = Assert.Single(transport.Requests, r => r.Command == Command.Mf1DarksideAcquire);
        Assert.Equal(new byte[] { (byte)MfKeyType.A, 19, 1, 2 }, request.Data);
    }

    [Fact]
    public async Task Dump_RejectsDifferentCardBeforeTryingKeys()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [9, 8, 7, 6]);
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChameleonClient(connection).ReadMifareClassicAsync([Key], type: dump.Type, existing: dump));

        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
        Assert.Equal(0, dump.RecoveredBlocks);
    }

    [Fact]
    public async Task StaticNested_UsesAuthenticationUidFromCaptureForLongUidCard()
    {
        var uid = Convert.FromHexString("0401024D4F4E53");
        var dump = new MifareDump(MifareClassicType.Mini, uid);
        for (var s = 0; s < dump.SectorCount; s++)
        {
            dump.KeyA[s] = Key;
            dump.KeyB[s] = Key;
        }
        dump.KeyB[4] = null;
        var transport = new CardTransport { Prng = MifareClassicPrngType.Static };
        transport.Response = request => request.Command switch
        {
            Command.Hf14aScan => (Status.HfTagOk, Convert.FromHexString("070401024D4F4E5304000900")),
            Command.Mf1StaticNestedAcquire => (Status.HfTagOk,
                Convert.FromHexString("4D4F4E53009080A22B7C6E42009080A21DE65248")),
            Command.Mf1AuthOneKeyBlock when request.Data[2..].SequenceEqual(Key) => (Status.MfErrAuth, Array.Empty<byte>()),
            _ => null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        var capture = await client.GetStaticNestedNoncesAsync(3, MfKeyType.A, Key, 19, MfKeyType.B);
        Assert.Equal(0x4D4F4E53u, capture.Uid);
        Assert.Equal(2, capture.Nonces.Count);
        transport.Requests.Clear();
        await new MifareKeyRecovery(client).RecoverAsync(dump, MifareRecoveryMethod.StaticNested);

        Assert.Equal(Convert.FromHexString("112233445566"), dump.KeyB[4]);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1StaticNestedAcquire);
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 9)]
    [InlineData(false, 19)]
    [InlineData(true, 0)]
    [InlineData(true, 19)]
    [InlineData(true, 21)]
    public async Task NestedAcquisition_RejectsIncompleteOrMisalignedPayload(bool isStatic, int length)
    {
        var transport = new CardTransport
        {
            Response = _ => (Status.HfTagOk, new byte[length]),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection)
            .GetNestedNoncesAsync(3, MfKeyType.A, Key, 19, MfKeyType.B, isStatic));
    }

    [Fact]
    public async Task DarksideProbe_EmptySuccessResponseDoesNotClaimVulnerability()
    {
        var transport = new CardTransport { Response = _ => (Status.HfTagOk, Array.Empty<byte>()) };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        Assert.Equal(DarksideResult.Fixed, await new ChameleonClient(connection).CheckDarksideAsync());
    }

    [Fact]
    public async Task DarksideAcquisition_RejectsErrorStatusEvenWhenPayloadLooksValid()
    {
        var transport = new CardTransport { Response = _ => (Status.MfErrAuth, new byte[33]) };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection)
            .GetDarksideAsync(3, MfKeyType.B, true, 15));
    }

    [Fact]
    public async Task StaticNested_KnownBToTargetA_PassesSeedTypeToSolver()
    {
        var dump = KnownCard();
        dump.KeyA[0] = null;
        var transport = new CardTransport
        {
            Prng = MifareClassicPrngType.Static,
            Response = request => request.Command switch
            {
                Command.Mf1StaticNestedAcquire => (Status.HfTagOk,
                    Convert.FromHexString("7C048D13009080A24B5B5AB9009080A248D9A6C1")),
                Command.Mf1AuthOneKeyBlock when request.Data[2..].SequenceEqual(Key) => (Status.MfErrAuth, Array.Empty<byte>()),
                _ => null,
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection))
            .RecoverAsync(dump, MifareRecoveryMethod.StaticNested);

        Assert.Equal(Convert.FromHexString("112233445566"), dump.KeyA[0]);
        var acquire = Assert.Single(transport.Requests, r => r.Command == Command.Mf1StaticNestedAcquire);
        Assert.Equal((byte)MfKeyType.B, acquire.Data[0]);
        Assert.Equal((byte)MfKeyType.A, acquire.Data[8]);
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1AuthOneKeyBlock));
    }

    [Fact]
    public async Task HardnestedAcquisition_DecodesBothNoncesAndParityNibbles()
    {
        var transport = new CardTransport
        {
            Response = _ => (Status.HfTagOk, Convert.FromHexString("123456789ABCDEF0A5")),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        var samples = await new ChameleonClient(connection)
            .GetHardnestedNoncesAsync(true, 3, MfKeyType.A, Key, 19, MfKeyType.B);

        Assert.Equal(new[] { new HardnestedSample(0x12345678, 10), new HardnestedSample(0x9ABCDEF0, 5) }, samples);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(Command.Mf1HardnestedAcquire, request.Command);
        Assert.Equal(new byte[] { 1, 0x60, 3 }.Concat(Key).Concat(new byte[] { 0x61, 19 }), request.Data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(10)]
    public async Task HardnestedAcquisition_RejectsMisalignedPayload(int length)
    {
        var transport = new CardTransport { Response = _ => (Status.HfTagOk, new byte[length]) };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection)
            .GetHardnestedNoncesAsync(false, 3, MfKeyType.A, Key, 19, MfKeyType.B));
    }

    [Fact]
    public async Task Hardnested_RecoversOnlyMissingKeyAndAuthenticatesZeroKey()
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var counter = 0;
        var transport = new CardTransport { Prng = MifareClassicPrngType.Hard };
        transport.Response = request => request.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, new byte[] { 0x07, 0xdd }),
            Command.Mf1HardnestedAcquire => (Status.HfTagOk, HardnestedBatch(ref counter)),
            Command.Mf1AuthOneKeyBlock when request.Data[2..].SequenceEqual(Key) => (Status.MfErrAuth, Array.Empty<byte>()),
            _ => null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new HardnestedEngine();

        await new MifareKeyRecovery(new ChameleonClient(connection), engine)
            .RecoverAsync(dump, MifareRecoveryMethod.Hardnested);

        Assert.NotNull(engine.Capture);
        Assert.Equal(0x01020304u, engine.Capture.AuthenticationUid);
        Assert.True(engine.Capture.Samples.Count >= 4096);
        Assert.Equal(new byte[6], dump.KeyB[4]);
        Assert.All(transport.Requests.Where(request => request.Command == Command.Mf1HardnestedAcquire), request =>
            Assert.Equal(new byte[] { (byte)MfKeyType.B, 19 }, request.Data[9..]));
        Assert.Equal(2, transport.Requests.Count(request => request.Command == Command.Mf1AuthOneKeyBlock));
        var auth = Assert.Single(transport.Requests, request => request.Command == Command.Mf1AuthOneKeyBlock
            && request.Data[2..].SequenceEqual(new byte[6]));
        Assert.Equal(new byte[6], auth.Data[2..]);
    }

    [Fact]
    public async Task Hardnested_UnsupportedPlatformDoesNotTouchDevice()
    {
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection))
                .RecoverAsync(KnownCard(), MifareRecoveryMethod.Hardnested));

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Hardnested_UnsupportedFirmwareDoesNotCollectNonces()
    {
        var transport = new CardTransport { Prng = MifareClassicPrngType.Hard };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), new HardnestedEngine())
                .RecoverAsync(KnownCard(), MifareRecoveryMethod.Hardnested));

        Assert.DoesNotContain(transport.Requests, request => request.Command == Command.Mf1HardnestedAcquire);
    }

    private static byte[] HardnestedBatch(ref int counter)
    {
        var data = new byte[495];
        for (var offset = 0; offset < data.Length; offset += 9)
        {
            data[offset] = (byte)counter++;
            data[offset + 4] = (byte)counter++;
        }
        return data;
    }

    [Fact]
    public async Task Hardnested_RejectsChangedCardBeforeAuthenticatingCandidate()
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var counter = 0;
        var changed = false;
        var transport = new CardTransport { Prng = MifareClassicPrngType.Hard };
        transport.Response = request => request.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, new byte[] { 0x07, 0xdd }),
            Command.Mf1HardnestedAcquire => (Status.HfTagOk, HardnestedBatch(ref counter)),
            Command.Hf14aScan when changed => (Status.HfTagOk, new byte[] { 4, 9, 8, 7, 6, 4, 0, 9, 0 }),
            Command.Mf1AuthOneKeyBlock => (Status.MfErrAuth, Array.Empty<byte>()),
            _ => null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new HardnestedEngine { OnRecover = () => changed = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), engine)
                .RecoverAsync(dump, MifareRecoveryMethod.Hardnested));

        Assert.Null(dump.KeyB[4]);
        var auth = Assert.Single(transport.Requests, request => request.Command == Command.Mf1AuthOneKeyBlock);
        Assert.Equal(Key, auth.Data[2..]);
    }

    [Fact]
    public async Task HardnestedAcquisition_CancellationDrainsTheCurrentResponse()
    {
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport
        {
            Response = _ => (Status.HfTagOk, Convert.FromHexString("123456789ABCDEF0A5")),
            OnRequest = _ => stop.Cancel(),
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetHardnestedNoncesAsync(false, 3, MfKeyType.A, Key, 19, MfKeyType.B, stop.Token));
        transport.OnRequest = null;
        transport.Response = _ => (Status.HfTagOk, Convert.FromHexString("DEADBEEF010203045A"));

        var samples = await client.GetHardnestedNoncesAsync(false, 3, MfKeyType.A, Key, 19, MfKeyType.B);
        Assert.Equal(new HardnestedSample(0xDEADBEEF, 5), samples[0]);
    }

    private sealed class HardnestedEngine : IHardnestedEngine
    {
        public string? UnavailableReason => null;
        public HardnestedCapture? Capture { get; private set; }
        public int Calls { get; private set; }
        public ulong? Result { get; init; } = 0;
        public Action? OnRecover { get; init; }
        public Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct)
        {
            Capture = capture;
            Calls++;
            OnRecover?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private static byte[] EncryptedCapture(int sectors)
    {
        var data = new byte[4 + sectors * 14];
        Convert.FromHexString("A13E4902").CopyTo(data, 0);
        for (var i = 0; i < sectors * 2; i++)
            Convert.FromHexString("D1410F2E9E49FC").CopyTo(data, 4 + i * 7);
        return data;
    }

    [Fact]
    public async Task StaticEncrypted_ParsesWireResponseAndCollectsFromSectorZero()
    {
        var transport = new CardTransport { Response = r => r.Command == Command.Mf1EncNestedAcquire
            ? (Status.HfTagOk, EncryptedCapture(2)) : null };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new ChameleonClient(connection).GetStaticEncryptedNestedNoncesAsync(Key, 2);
        Assert.Equal(0xA13E4902u, result.Uid);
        Assert.Equal(2, result.Sectors.Count);
        Assert.Equal(new NestedNonce(0xD14191B3, 0x2E9E49FC, 15), result.Sectors[1].B);
        Assert.Equal(Convert.FromHexString("FFFFFFFFFFFF0200"), Assert.Single(transport.Requests).Data);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(19)]
    [InlineData(33)]
    public async Task StaticEncrypted_RejectsIncompleteOrExtraRecords(int length)
    {
        var transport = new CardTransport { Response = _ => (Status.HfTagOk, new byte[length]) };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ChameleonException>(() =>
            new ChameleonClient(connection).GetStaticEncryptedNestedNoncesAsync(Key, 2));
    }

    [Fact]
    public async Task StaticEncrypted_TriesKnownBackdoorsWithoutPrngOrSeedKey()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var transport = new CardTransport
        {
            Response = r => r.Command switch
            {
                Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
                Command.Mf1EncNestedAcquire => (++attempts < 3 ? Status.MfErrAuth : Status.HfTagOk, EncryptedCapture(5)),
                _ => null,
            },
            OnRequest = r => { if (r.Command == Command.Mf1EncNestedAcquire && attempts == 3) stop.Cancel(); },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump,
                MifareRecoveryMethod.StaticEncryptedNested, ct: stop.Token));
        var requests = transport.Requests.Where(r => r.Command == Command.Mf1EncNestedAcquire).ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("A396EFA4E24F", Convert.ToHexString(requests[0].Data[..6]));
        Assert.Equal("A31667A8CEC1", Convert.ToHexString(requests[1].Data[..6]));
        Assert.Equal("518B3354E760", Convert.ToHexString(requests[2].Data[..6]));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1DetectPrng);
    }

    [Fact]
    public async Task StaticEncrypted_RecoversOnlyMissingKeyAndUsesBatchAuthentication()
    {
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, EncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested);
        Assert.NotNull(dump.KeyB[4]);
        Assert.All(dump.KeyA, key => Assert.Equal(Key, key));
        var request = Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Equal(19, request.Data[0]);
        Assert.Equal((byte)MfKeyType.B, request.Data[1]);
        Assert.InRange(request.Data[2], (byte)1, (byte)64);
        Assert.Equal(3 + 6 * request.Data[2], request.Data.Length);
    }

    [Fact]
    public async Task StaticEncrypted_UnsupportedFirmwareFailsBeforeCollection()
    {
        var transport = new CardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => new MifareKeyRecovery(new ChameleonClient(connection))
            .RecoverAsync(KnownCard(), MifareRecoveryMethod.StaticEncryptedNested));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1EncNestedAcquire);
    }

    [Fact]
    public async Task StaticEncrypted_NoSeedRequiredAndCancellationKeepsFoundKey()
    {
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        using var stop = new CancellationTokenSource();
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, EncryptedCapture(5)),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(r.Data[3..9]).ToArray()),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var progress = new CallbackProgress<KeyRecoveryProgress>(p => { if (p.Mark == KeyMark.Found) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MifareKeyRecovery(new ChameleonClient(connection))
            .RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested, progress, stop.Token));
        Assert.NotNull(dump.KeyB[0]);
        Assert.All(dump.KeyA, key => Assert.Null(key));
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
    }

    [Fact]
    public async Task StaticEncrypted_RejectsChangedCardBeforeCheckingCandidates()
    {
        var scans = 0;
        var transport = new CardTransport { Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, EncryptedCapture(5)),
            Command.Hf14aScan when ++scans == 2 => (Status.HfTagOk, new byte[] { 4, 9, 8, 7, 6, 4, 0, 9, 0 }),
            _ => null,
        }};
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MifareKeyRecovery(new ChameleonClient(connection))
            .RecoverAsync(KnownCard(), MifareRecoveryMethod.StaticEncryptedNested));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
    }

    [Theory]
    [InlineData(Status.MfErrAuth, "")]
    [InlineData(Status.HfTagNo, "")]
    [InlineData(Status.HfTagOk, "01000000000000")]
    [InlineData(Status.HfTagOk, "01")]
    public async Task StaticEncrypted_BatchCheckHandlesFailures(Status status, string payload)
    {
        var transport = new CardTransport { Response = _ => (status, Convert.FromHexString(payload)) };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        if (status == Status.MfErrAuth)
            Assert.Null(await client.CheckMifareKeysOnBlockAsync(255, MfKeyType.B, [Key]));
        else await Assert.ThrowsAsync<ChameleonException>(() => client.CheckMifareKeysOnBlockAsync(255, MfKeyType.B, [Key]));
    }

    private sealed class CardTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = new();
        public Action<ChameleonFrame>? OnRequest { get; set; }
        public Func<ChameleonFrame, (Status Status, byte[] Payload)?>? Response { get; set; }
        public MifareClassicPrngType Prng { get; init; } = MifareClassicPrngType.Weak;
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Test card";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                var (status, payload) = request.Command switch
                {
                    Command.ChangeDeviceMode => (Status.Success, Array.Empty<byte>()),
                    Command.Hf14aScan => (Status.HfTagOk, new byte[] { 4, 1, 2, 3, 4, 4, 0, 9, 0 }),
                    Command.Mf1DetectPrng => (Status.HfTagOk, new byte[] { (byte)Prng }),
                    Command.Mf1DetectNtDist => (Status.HfTagOk, new byte[8]),
                    Command.Mf1StaticNestedAcquire => (Status.HfTagOk, new byte[20]),
                    Command.Mf1DarksideAcquire => (Status.HfTagOk, new byte[] { (byte)DarksideResult.Fixed }),
                    Command.Mf1ReadOneBlock => (Status.HfTagOk, new byte[16]),
                    _ => (Status.HfTagOk, Array.Empty<byte>()),
                };
                (status, payload) = Response?.Invoke(request) ?? (status, payload);
                OnRequest?.Invoke(request);
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
