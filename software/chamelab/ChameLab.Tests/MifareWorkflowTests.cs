using System;
using System.Collections.Generic;
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
