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
        Assert.Equal(new[] { Command.Em410xSetCycle, Command.Em410xGetCycle, Command.GetDeviceCapabilities }, transport.Requests.Select(r => r.Command));
        Assert.Empty(transport.Requests[1].Data);
    }

    [Fact]
    public async Task SlotCopyRestoresCycleAfterPrimaryUploadAndBeforeFlashSave()
    {
        var transport = new CycleTransport { SupportsGap = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = LfCardCycle.Parse("AABBCCDDEE", 5) with { GapSteps = 30 };
        var card = new CardSave { Tag = TagSpecificType.Em410x, Uid = Convert.FromHexString("0001020304") };
        await client.WriteSlotContentAsync(3, new SlotContent(1, null, new SlotSide(card, "Door", null, true), SlotEmulatorSettings.None, cycle));
        var commands = transport.Requests.Select(r => r.Command).ToList();
        Assert.True(commands.IndexOf(Command.Em410xSetEmuId) < commands.IndexOf(Command.Em410xSetCycle));
        Assert.Equal(Command.SlotDataConfigSave, commands[^1]);
        Assert.Equal(cycle.Encode(), transport.Cycle);
        Assert.Equal(30, transport.Gap);
        Assert.True(commands.IndexOf(Command.Em410xSetCycle) < commands.IndexOf(Command.Em410xSetCycleGap));
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    public async Task SilenceRoundTripsAndZeroClearsPreviousGap(int steps)
    {
        var transport = new CycleTransport { SupportsGap = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = LfCardCycle.Parse("AABBCCDDEE", 2);
        await client.SetEm410xCycleAsync(cycle with { GapSteps = 30 });
        await client.SetEm410xCycleAsync(cycle with { GapSteps = steps });
        var loaded = await client.GetEm410xCycleAsync();
        Assert.Equal(steps, loaded.GapSteps);
        Assert.Equal(cycle.Encode(), loaded.Encode());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task RejectsInvalidSilenceBeforeSendingCommands(int steps)
    {
        var transport = new CycleTransport { SupportsGap = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => new ChameleonClient(connection)
            .SetEm410xCycleAsync(new LfCardCycle(1, [], steps)));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task OldFirmwareRejectsNonzeroSilenceBeforeChangingTheSlot()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => new ChameleonClient(connection)
            .SetEm410xCycleAsync(new LfCardCycle(1, [], 1)));
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Em410xSetCycle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SlotReadPreservesCycleAndActiveSlotAndSupportsOldFirmware(bool supported)
    {
        var transport = new CycleTransport { SupportsCycle = supported, SupportsGap = supported };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = LfCardCycle.Parse("AABBCCDDEE", 4) with { GapSteps = supported ? 17 : 0 };
        await client.SetEm410xCycleAsync(cycle);
        var content = await client.ReadSlotContentAsync(new SlotInfo { Index = 3, LfType = TagSpecificType.Em410x, LfEnabled = true });
        Assert.Equal(1, await client.GetActiveSlotAsync());
        Assert.Equal(Convert.FromHexString("0001020304"), content.Lf!.Card.Uid);
        if (supported)
        {
            Assert.Equal(cycle.Encode(), content.LfCycle!.Encode());
            Assert.Equal(17, content.LfCycle.GapSteps);
        }
        else
        {
            Assert.Null(content.LfCycle);
            Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Em410xGetCycle);
        }
    }

    [Fact]
    public async Task ReadsAllCyclesWithoutSwitchingSlots()
    {
        var all = new byte[8 * LfCardCycle.AllCyclesEntrySize];
        // Slot 2: EM410x, 3 bursts, one extra card, silence 12.
        var e = 1 * LfCardCycle.AllCyclesEntrySize;
        all[e] = 1; all[e + 1] = 3; all[e + 2] = 1; all[e + 3] = 12;
        new byte[] { 1, 2, 3, 4, 5 }.CopyTo(all, e + 4);
        new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }.CopyTo(all, e + 9);
        // Slot 4: EM410x without a cycle.
        e = 3 * LfCardCycle.AllCyclesEntrySize;
        all[e] = 1; all[e + 1] = 1;
        var transport = new CycleTransport { SupportsGap = true, AllCycles = all };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var slots = Enumerable.Range(1, 8).Select(i => new SlotInfo
        {
            Index = i,
            LfType = i is 2 or 4 ? TagSpecificType.Em410x : TagSpecificType.Undefined,
            LfEnabled = i is 2 or 4,
        }).ToList();
        await client.ReadLfCyclesAsync(slots);
        Assert.Equal(3, slots[1].LfCycle!.Bursts);
        Assert.Equal(12, slots[1].LfCycle!.GapSteps);
        Assert.Equal("AABBCCDDEE", slots[1].LfCycle!.Text);
        Assert.Empty(slots[3].LfCycle!.ExtraIds);
        Assert.Null(slots[0].LfCycle);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.SetActiveSlot or Command.Em410xGetCycle);
    }

    [Fact]
    public void DecodeAllRejectsBadLength() =>
        Assert.Throws<ArgumentException>(() => LfCardCycle.DecodeAll(new byte[LfCardCycle.AllCyclesEntrySize + 1]));

    private sealed class CycleTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public byte[] Cycle { get; private set; } = [1, 0];
        public bool SupportsCycle { get; init; } = true;
        public bool SupportsGap { get; init; }
        public byte[]? AllCycles { get; init; }
        public byte Gap { get; private set; }
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
                if (request.Command == Command.Em410xSetCycle) { Cycle = request.Data.ToArray(); Gap = 0; }
                if (request.Command == Command.Em410xSetCycleGap) Gap = request.Data[0];
                if (request.Command == Command.SetActiveSlot) _active = request.Data[0];
                byte[] payload = request.Command switch
                {
                    Command.Em410xGetCycle => Cycle,
                    Command.Em410xGetCycleGap => [Gap],
                    Command.GetActiveSlot => [_active],
                    Command.Em410xGetAllCycles when AllCycles is not null => AllCycles,
                    Command.GetDeviceCapabilities when AllCycles is not null => [0x17, 0x0E, 0x17, 0x0F, 0x17, 0x10, 0x17, 0x11, 0x17, 0x12],
                    Command.GetDeviceCapabilities when SupportsCycle && SupportsGap => [0x17, 0x0E, 0x17, 0x0F, 0x17, 0x10, 0x17, 0x11],
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
