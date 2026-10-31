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
    private static LfCycleCard Em(string hex) => new(TagSpecificType.Em410x, Convert.FromHexString(hex));
    private static LfCycleCard Electra(string hex) => new(TagSpecificType.Em410xElectra, Convert.FromHexString(hex));

    [Fact]
    public async Task WritesAndReadsMixedCycleUsingDedicatedCommands()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = new LfCardCycle(2, [Em("0001020304"), Electra("FFEEDDCCBB0102030405060708"),
            new LfCycleCard(TagSpecificType.HidProx, new byte[13]), new LfCycleCard(TagSpecificType.Idteck, new byte[8])], 17);
        await client.SetLfCycleAsync(cycle);
        var loaded = await client.GetLfCycleAsync();
        Assert.True(cycle.SameAs(loaded));
        Assert.Equal(new[] { Command.LfSetCycle, Command.LfGetCycle }, transport.Requests.Select(r => r.Command));
        Assert.Empty(transport.Requests[1].Data);
    }

    [Fact]
    public async Task SlotCopyRestoresCycleAfterPrimaryUploadAndBeforeFlashSave()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = new LfCardCycle(5, [Electra("AABBCCDDEE0000000000000001")], 30);
        var card = new CardSave { Tag = TagSpecificType.Em410x, Uid = Convert.FromHexString("0001020304") };
        await client.WriteSlotContentAsync(3, new SlotContent(1, null, new SlotSide(card, "Door", null, true), SlotEmulatorSettings.None, cycle));
        var commands = transport.Requests.Select(r => r.Command).ToList();
        Assert.True(commands.IndexOf(Command.Em410xSetEmuId) < commands.IndexOf(Command.LfSetCycle));
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
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(new LfCardCycle(1, [Em("000102030405")])));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(new LfCardCycle(1, [], 31)));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SlotReadPreservesCycleAndActiveSlotAndSupportsOldFirmware(bool supported)
    {
        var all = new byte[8 * LfCardCycle.AllCyclesEntrySize];
        var cycle = new LfCardCycle(4, [Em("AABBCCDDEE")], 17);
        var e = 2 * LfCardCycle.AllCyclesEntrySize;
        all[e + 1] = (byte)TagSpecificType.Em410x;
        cycle.Encode().CopyTo(all, e + 18);
        var transport = new CycleTransport { AllCycles = supported ? all : null };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var content = await client.ReadSlotContentAsync(new SlotInfo { Index = 3, LfType = TagSpecificType.Em410x, LfEnabled = true });
        Assert.Equal(1, await client.GetActiveSlotAsync());
        Assert.Equal(Convert.FromHexString("0001020304"), content.Lf!.Card.Uid);
        if (supported) Assert.True(cycle.SameAs(content.LfCycle!));
        else Assert.Null(content.LfCycle);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.LfGetCycle);
    }

    [Fact]
    public async Task ReadsAllCyclesWithoutSwitchingSlots()
    {
        var all = new byte[8 * LfCardCycle.AllCyclesEntrySize];
        // Slot 2: Electra, 3 bursts, silence 12, one extra EM410x card.
        var e = 1 * LfCardCycle.AllCyclesEntrySize;
        all[e + 1] = (byte)TagSpecificType.Em410xElectra;
        Convert.FromHexString("01020304050607080910111213").CopyTo(all, e + 2);
        Convert.FromHexString("030C01" + "0064" + "AABBCCDDEE").CopyTo(all, e + 18);
        // Slot 4: Viking without a cycle.
        e = 3 * LfCardCycle.AllCyclesEntrySize;
        all[e + 1] = (byte)TagSpecificType.Viking; all[e + 18] = 1;
        var transport = new CycleTransport { AllCycles = all };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var slots = Enumerable.Range(1, 8).Select(i => new SlotInfo
        {
            Index = i,
            LfType = i switch { 2 => TagSpecificType.Em410xElectra, 4 => TagSpecificType.Viking, _ => TagSpecificType.Undefined },
            LfEnabled = i is 2 or 4,
        }).ToList();
        await client.ReadLfCyclesAsync(slots);
        Assert.Equal(3, slots[1].LfCycle!.Bursts);
        Assert.Equal(12, slots[1].LfCycle!.GapSteps);
        Assert.True(Em("AABBCCDDEE").SameAs(slots[1].LfCycleCards.Single()));
        Assert.Empty(slots[3].LfCycleCards);
        Assert.Null(slots[0].LfCycle);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.SetActiveSlot or Command.LfGetCycle);
    }

    [Fact]
    public void DecodeAllRejectsBadLength() =>
        Assert.Throws<ArgumentException>(() => LfCardCycle.DecodeAll(new byte[LfCardCycle.AllCyclesEntrySize + 1]));

    private sealed class CycleTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public byte[] Cycle { get; private set; } = [1, 0, 0];
        public byte[]? AllCycles { get; init; }
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
                if (request.Command == Command.Em410xSetEmuId) Cycle = [1, 0, 0];
                if (request.Command == Command.LfSetCycle) Cycle = request.Data.ToArray();
                if (request.Command == Command.SetActiveSlot) _active = request.Data[0];
                byte[] payload = request.Command switch
                {
                    Command.LfGetCycle => Cycle,
                    Command.GetActiveSlot => [_active],
                    Command.LfGetAllCycles when AllCycles is not null => AllCycles,
                    Command.GetDeviceCapabilities when AllCycles is not null => [0x17, 0x13, 0x17, 0x14, 0x17, 0x15],
                    Command.GetDeviceCapabilities => [0x17, 0x13, 0x17, 0x14],
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
