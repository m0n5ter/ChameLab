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
        var cycle = new LfCardCycle([Em("0001020304"), Electra("FFEEDDCCBB0102030405060708"),
            new LfCycleCard(TagSpecificType.HidProx, new byte[13]), new LfCycleCard(TagSpecificType.Idteck, new byte[8])]);
        await client.SetLfCycleAsync(cycle);
        var loaded = await client.GetLfCycleAsync();
        Assert.True(cycle.SameAs(loaded));
        var commands = transport.Requests.Where(r => r.Command != Command.GetDeviceCapabilities).ToList();
        Assert.Equal(new[] { Command.LfSetCycle, Command.LfGetCycle }, commands.Select(r => r.Command));
        Assert.Empty(commands[1].Data);
    }

    [Fact]
    public async Task WritesAndReadsCycleModeWhereSupported()
    {
        var transport = new CycleTransport { SupportsModes = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = new LfCardCycle([Em("0001020304")]) { Mode = LfCycleMode.WhilePresent, Bursts = 3, GapSteps = 12 };
        await client.SetLfCycleAsync(cycle);
        Assert.Equal(new byte[] { 1, 3, 12 }, transport.Mode);
        var loaded = await client.GetLfCycleAsync();
        Assert.True(cycle.SameAs(loaded));
        Assert.False(cycle.SameAs(loaded with { Bursts = 4 }));
        var commands = transport.Requests.Select(r => r.Command).Where(c => c != Command.GetDeviceCapabilities);
        Assert.Equal(new[] { Command.LfSetCycle, Command.LfSetCycleMode, Command.LfGetCycle, Command.LfGetCycleMode }, commands);
    }

    [Fact]
    public async Task SingleCardSendsNoModeAndInvalidModeSendsNothing()
    {
        var transport = new CycleTransport { SupportsModes = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await client.SetLfCycleAsync(new LfCardCycle([]) { Mode = LfCycleMode.WhilePresent });
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.LfSetCycleMode);
        transport.Requests.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(new LfCardCycle([Em("0001020304")]) { Bursts = 11 }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(new LfCardCycle([Em("0001020304")]) { GapSteps = 31 }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(new LfCardCycle([Em("0001020304")]) { Mode = (LfCycleMode)2 }));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task OlderFirmwareGetsNoModeCommands()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await client.SetLfCycleAsync(new LfCardCycle([Em("0001020304")]) { Mode = LfCycleMode.WhilePresent, Bursts = 2 });
        var loaded = await client.GetLfCycleAsync();
        Assert.Equal(LfCycleMode.PerField, loaded.Mode);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.LfSetCycleMode or Command.LfGetCycleMode);
    }

    [Fact]
    public async Task SlotCopyRestoresCycleAfterPrimaryUploadAndBeforeFlashSave()
    {
        var transport = new CycleTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var cycle = new LfCardCycle([Electra("AABBCCDDEE0000000000000001")]);
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
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(new LfCardCycle([Em("000102030405")])));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetLfCycleAsync(
            new LfCardCycle(Enumerable.Range(0, 8).Select(i => Em(i.ToString("X10"))).ToArray())));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SlotReadPreservesCycleAndActiveSlotAndSupportsOldFirmware(bool supported)
    {
        var all = new byte[8 * LfCardCycle.AllCyclesEntrySize];
        var cycle = new LfCardCycle([Em("AABBCCDDEE")]);
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
        // Slot 2: Electra with one extra EM410x card.
        var e = 1 * LfCardCycle.AllCyclesEntrySize;
        all[e + 1] = (byte)TagSpecificType.Em410xElectra;
        Convert.FromHexString("01020304050607080910111213").CopyTo(all, e + 2);
        Convert.FromHexString("01" + "0064" + "AABBCCDDEE").CopyTo(all, e + 18);
        // Slot 4: Viking without a cycle.
        e = 3 * LfCardCycle.AllCyclesEntrySize;
        all[e + 1] = (byte)TagSpecificType.Viking;
        var modes = new byte[8 * LfCardCycle.ModeSize];
        for (var i = 0; i < 8; i++) modes[i * 3 + 1] = 1;
        modes[3] = 1; modes[4] = 5; modes[5] = 7;
        var transport = new CycleTransport { AllCycles = all, SupportsModes = true, AllModes = modes };
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
        Assert.True(Em("AABBCCDDEE").SameAs(slots[1].LfCycleCards.Single()));
        Assert.Equal(LfCycleMode.WhilePresent, slots[1].LfCycle!.Mode);
        Assert.Equal(5, slots[1].LfCycle!.Bursts);
        Assert.Equal(7, slots[1].LfCycle!.GapSteps);
        Assert.Equal(LfCycleMode.PerField, slots[3].LfCycle!.Mode);
        Assert.Empty(slots[3].LfCycleCards);
        Assert.Null(slots[0].LfCycle);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.SetActiveSlot or Command.LfGetCycle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0001")]
    [InlineData("020100")]
    [InlineData("010000")]
    [InlineData("010B00")]
    [InlineData("01011F")]
    [InlineData("01010000")]
    public void RejectsMalformedModeResponses(string hex) =>
        Assert.Throws<ArgumentException>(() => new LfCardCycle([]).WithMode(Convert.FromHexString(hex)));

    [Fact]
    public void DecodeAllRejectsBadLength() =>
        Assert.Throws<ArgumentException>(() => LfCardCycle.DecodeAll(new byte[LfCardCycle.AllCyclesEntrySize + 1]));

    private sealed class CycleTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public byte[] Cycle { get; private set; } = [0];
        public byte[]? AllCycles { get; init; }
        public bool SupportsModes { get; init; }
        public byte[]? AllModes { get; init; }
        public byte[] Mode { get; private set; } = [0, 1, 0];
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
                if (request.Command == Command.Em410xSetEmuId) { Cycle = [0]; Mode = [0, 1, 0]; }
                if (request.Command == Command.LfSetCycleMode) Mode = request.Data.ToArray();
                if (request.Command == Command.LfSetCycle) Cycle = request.Data.ToArray();
                if (request.Command == Command.SetActiveSlot) _active = request.Data[0];
                byte[] payload = request.Command switch
                {
                    Command.LfGetCycle => Cycle,
                    Command.GetActiveSlot => [_active],
                    Command.LfGetCycleMode => Mode,
                    Command.LfGetAllCycles when AllCycles is not null => AllCycles,
                    Command.LfGetAllCycleModes when AllModes is not null => AllModes,
                    Command.GetDeviceCapabilities => Capabilities(),
                    Command.Em410xGetEmuId => [(byte)((ushort)TagSpecificType.Em410x >> 8), (byte)TagSpecificType.Em410x, 0, 1, 2, 3, 4],
                    _ => [],
                };
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)Status.Success));
            }
            return Task.CompletedTask;
        }
        private byte[] Capabilities()
        {
            List<byte> list = [0x17, 0x13, 0x17, 0x14];
            if (AllCycles is not null) list.AddRange([0x17, 0x15]);
            if (SupportsModes) list.AddRange([0x17, 0x16, 0x17, 0x17, 0x17, 0x18]);
            return list.ToArray();
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
