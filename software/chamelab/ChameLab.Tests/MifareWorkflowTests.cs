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

        var request = Assert.Single(transport.Requests, r => r.Command == acquire);
        Assert.Equal(new byte[] { (byte)MfKeyType.B, 19 }, request.Data[8..]);
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

    private sealed class CardTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = new();
        public Action<ChameleonFrame>? OnRequest { get; set; }
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
                    Command.Mf1DarksideAcquire => (Status.HfTagOk, new byte[] { (byte)DarksideResult.Fixed }),
                    Command.Mf1ReadOneBlock => (Status.HfTagOk, new byte[16]),
                    _ => (Status.HfTagOk, Array.Empty<byte>()),
                };
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
