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

public class LfCardCycleProtocolTests
{
    [Fact]
    public async Task WritesAndReadsCycleUsingDedicatedCommands()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = LfCardCycle.Parse("0001020304\nFFEEDDCCBB", 2);
        await client.SetEm410xCycleAsync(cycle);
        var loaded = await client.GetEm410xCycleAsync();
        Assert.Equal(cycle.Encode(), loaded.Encode());
        Assert.Equal(new[] { Command.Em410xSetCycle, Command.Em410xGetCycle }, transport.Requests.Select(r => r.Command));
        Assert.Empty(transport.Requests[1].Data);
    }

    [Fact]
    public async Task SlotCopyRestoresCycleAfterPrimaryUploadAndBeforeFlashSave()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = LfCardCycle.Parse("AABBCCDDEE", 5);
        var card = new CardSave { Tag = TagSpecificType.Em410x, Uid = Convert.FromHexString("0001020304") };
        await client.WriteSlotContentAsync(3, new SlotContent(1, null, new SlotSide(card, "Door", null, true), SlotEmulatorSettings.None, cycle));
        var commands = transport.Requests.Select(r => r.Command).ToList();
        Assert.True(commands.IndexOf(Command.Em410xSetEmuId) < commands.IndexOf(Command.Em410xSetCycle));
        Assert.Equal(Command.SlotDataConfigSave, commands[^1]);
        Assert.Equal(cycle.Encode(), transport.Cycle);
    }

    [Fact]
    public async Task RejectsInvalidCycleWithoutSendingCommand()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SetEm410xCycleAsync(new LfCardCycle(1, [new byte[6]])));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SlotReadPreservesCycleAndActiveSlotAndSupportsOldFirmware(bool supported)
    {
        var transport = new CycleTransport { SupportsCycle = supported };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = LfCardCycle.Parse("AABBCCDDEE", 4);
        await client.SetEm410xCycleAsync(cycle);
        var content = await client.ReadSlotContentAsync(new SlotInfo { Index = 3, LfType = TagSpecificType.Em410x, LfEnabled = true });
        Assert.Equal(1, await client.GetActiveSlotAsync());
        Assert.Equal(Convert.FromHexString("0001020304"), content.Lf!.Card.Uid);
        if (supported) Assert.Equal(cycle.Encode(), content.LfCycle!.Encode());
        else
        {
            Assert.Null(content.LfCycle);
            Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Em410xGetCycle);
        }
    }

    private sealed class CycleTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public byte[] Cycle { get; private set; } = [1, 0];
        public bool SupportsCycle { get; init; } = true;
        private byte _active;
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Cycle test device";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                if (request.Command == Command.Em410xSetEmuId) Cycle = [1, 0];
                if (request.Command == Command.Em410xSetCycle) Cycle = request.Data.ToArray();
                if (request.Command == Command.SetActiveSlot) _active = request.Data[0];
                byte[] payload = request.Command switch
                {
                    Command.Em410xGetCycle => Cycle,
                    Command.GetActiveSlot => [_active],
                    Command.GetDeviceCapabilities when SupportsCycle => [0x17, 0x0E, 0x17, 0x0F],
                    Command.Em410xGetEmuId => [(byte)((ushort)TagSpecificType.Em410x >> 8), (byte)TagSpecificType.Em410x, 0, 1, 2, 3, 4],
                    _ => [],
                };
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)Status.Success));
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
