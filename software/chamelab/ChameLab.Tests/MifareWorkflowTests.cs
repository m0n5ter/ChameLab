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
        var transport = new CardTransport { Prng = prng };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection)).RecoverAsync(dump, method);

        var requests = transport.Requests.FindAll(r => r.Command == acquire);
        Assert.Equal(method == MifareRecoveryMethod.StaticNested ? 3 : 1, requests.Count);
        Assert.All(requests, request => Assert.Equal(new byte[] { (byte)MfKeyType.B, 19 }, request.Data[8..]));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock
            || r.Command == Command.Mf1DarksideAcquire);
        Assert.Equal(Key, dump.KeyA[4]);
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
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
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
            Response = request => request.Command == Command.Mf1StaticNestedAcquire
                ? (Status.HfTagOk, Convert.FromHexString("7C048D13009080A24B5B5AB9009080A248D9A6C1"))
                : null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();

        await new MifareKeyRecovery(new ChameleonClient(connection))
            .RecoverAsync(dump, MifareRecoveryMethod.StaticNested);

        Assert.Equal(Convert.FromHexString("112233445566"), dump.KeyA[0]);
        var acquire = Assert.Single(transport.Requests, r => r.Command == Command.Mf1StaticNestedAcquire);
        Assert.Equal((byte)MfKeyType.B, acquire.Data[0]);
        Assert.Equal((byte)MfKeyType.A, acquire.Data[8]);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
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
        var auth = Assert.Single(transport.Requests, request => request.Command == Command.Mf1AuthOneKeyBlock);
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
            _ => null,
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var engine = new HardnestedEngine { OnRecover = () => changed = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MifareKeyRecovery(new ChameleonClient(connection), engine)
                .RecoverAsync(dump, MifareRecoveryMethod.Hardnested));

        Assert.Null(dump.KeyB[4]);
        Assert.DoesNotContain(transport.Requests, request => request.Command == Command.Mf1AuthOneKeyBlock);
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
        public Action? OnRecover { get; init; }
        public Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct)
        {
            Capture = capture;
            OnRecover?.Invoke();
            return Task.FromResult<ulong?>(0);
        }
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
