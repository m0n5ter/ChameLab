using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class Mfkey32WizardTests
{
    private static CardSave Card(string uid = "12345678") => new()
    {
        Name = "Test card", Tag = TagSpecificType.Mifare1024,
        Uid = Convert.FromHexString(uid), Sak = 8, Atqa = [4, 0],
    };

    [Fact]
    public void MatchingSlotPrefersActiveThenLowestIndex()
    {
        var card = Card();
        SlotInfo Slot(int index, bool active = false) => new()
        {
            Index = index, IsActive = active, HfType = card.Tag, HfEnabled = true, HfNick = card.Name,
        };
        Assert.Equal(5, Mfkey32Wizard.FindSlot([Slot(3), Slot(5, true)], card)!.Index);
        Assert.Equal(3, Mfkey32Wizard.FindSlot([Slot(5), Slot(3)], card)!.Index);
        var disabled = Slot(1); disabled.HfEnabled = false;
        Assert.Null(Mfkey32Wizard.FindSlot([disabled], card));
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("04010212345678")]
    [InlineData("04010203040512345678")]
    public async Task RecoverySavesFullUidAndRestoresDeviceAfterReading(string uid)
    {
        var card = Card(uid);
        var transport = new WizardTransport(card);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        var read = false;
        await new Mfkey32Wizard(new ChameleonClient(connection), known).RunAsync(card, new QuietProgress(), keys =>
        {
            Assert.Equal(new byte[6], Assert.Single(keys));
            Assert.Equal(new byte[6], Assert.Single(known.ForUid(card.Uid)));
            Assert.Equal(DeviceMode.Reader, transport.Mode);
            Assert.False(transport.Detection);
            read = true;
            return Task.CompletedTask;
        }, default);

        Assert.True(read);
        AssertRestored(transport);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.SetSlotDataDefault
            or Command.SetSlotTagType or Command.SlotDataConfigSave);
        if (card.Uid.Length > 4) Assert.Empty(known.ForUid(card.Uid[^4..]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRestoresStateDuringCaptureOrWaitingForCard(bool afterRecovery)
    {
        var card = Card();
        var transport = new WizardTransport(card);
        using var stop = new CancellationTokenSource();
        transport.OnRequest = r =>
        {
            if (r.Command == (afterRecovery ? Command.Hf14aScan : Command.Mf1GetDetectionCount)) stop.Cancel();
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Mfkey32Wizard(new ChameleonClient(connection), known).RunAsync(card, new QuietProgress(), _ =>
            {
                stop.Cancel();
                return Task.Delay(Timeout.Infinite, stop.Token);
            }, stop.Token));
        AssertRestored(transport);
        Assert.Equal(afterRecovery ? 1 : 0, known.KeyCount);
    }

    [Fact]
    public async Task SetupFailureRestoresStateAndDoesNotStartCapture()
    {
        var card = Card();
        var transport = new WizardTransport(card) { FailWriteSetup = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ChameleonException>(() =>
            new Mfkey32Wizard(new ChameleonClient(connection), new KnownCardKeys(new MemoryKeyValueStore()))
                .RunAsync(card, new QuietProgress(), _ => throw new Exception("Must not read"), default));
        AssertRestored(transport);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1GetDetectionCount);
    }

    [Fact]
    public async Task SlotWithSameNameButDifferentUidIsRejected()
    {
        var card = Card();
        var transport = new WizardTransport(Card("DEADBEEF"));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Mfkey32Wizard(new ChameleonClient(connection), new KnownCardKeys(new MemoryKeyValueStore()))
                .RunAsync(card, new QuietProgress(), _ => throw new Exception("Must not read"), default));
        AssertRestored(transport);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1SetDetectionEnable);
    }

    [Fact]
    public async Task ReadFailureKeepsRecoveredKeyAndRestoresDevice()
    {
        var card = Card();
        var transport = new WizardTransport(card);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var known = new KnownCardKeys(new MemoryKeyValueStore());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Mfkey32Wizard(new ChameleonClient(connection), known).RunAsync(card, new QuietProgress(),
                _ => throw new InvalidOperationException("Wrong physical card"), default));
        Assert.Single(known.ForUid(card.Uid));
        AssertRestored(transport);
    }

    private static void AssertRestored(WizardTransport transport)
    {
        Assert.Equal(DeviceMode.Reader, transport.Mode);
        Assert.Equal(1, transport.ActiveSlot);
        Assert.True(transport.Detection);
        Assert.Equal(EmulatorWriteMode.Normal, transport.WriteMode);
    }

    [Fact]
    public async Task OriginalTagModeAndDisabledDetectionAreRestored()
    {
        var card = Card();
        var transport = new WizardTransport(card) { Mode = DeviceMode.Tag, Detection = false };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await new Mfkey32Wizard(new ChameleonClient(connection), new KnownCardKeys(new MemoryKeyValueStore()))
            .RunAsync(card, new QuietProgress(), _ => Task.CompletedTask, default);
        Assert.Equal(DeviceMode.Tag, transport.Mode);
        Assert.False(transport.Detection);
        Assert.Equal(1, transport.ActiveSlot);
        Assert.Equal(EmulatorWriteMode.Normal, transport.WriteMode);
    }

    private sealed class QuietProgress : IProgress<Mfkey32Progress> { public void Report(Mfkey32Progress value) { } }

    [Fact]
    public async Task WaitsThroughMissingAndWrongCardThenReadsAutomatically()
    {
        var card = Card("04010212345678");
        var scans = 0;
        var transport = new WizardTransport(card)
        {
            ScanResponse = () => ++scans switch
            {
                1 => (Status.HfTagNo, Array.Empty<byte>()),
                // Same last four UID bytes, different full UID: must keep waiting.
                2 => (Status.HfTagOk, new byte[] { 7, 5, 6, 7, 0x12, 0x34, 0x56, 0x78, 4, 0, 8, 0 }),
                _ => (Status.HfTagOk, [7, ..card.Uid, 4, 0, 8, 0]),
            },
        };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var read = false;
        await new Mfkey32Wizard(new ChameleonClient(connection), new KnownCardKeys(new MemoryKeyValueStore()))
            .RunAsync(card, new QuietProgress(), _ => { read = true; Assert.Equal(3, scans); return Task.CompletedTask; }, default);
        Assert.True(read);
        AssertRestored(transport);
    }

    private sealed class WizardTransport(CardSave card) : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = new();
        public Action<ChameleonFrame>? OnRequest { get; set; }
        public bool FailWriteSetup { get; init; }
        public Func<(Status Status, byte[] Payload)>? ScanResponse { get; init; }
        public DeviceMode Mode { get; set; } = DeviceMode.Reader;
        public int ActiveSlot { get; private set; } = 1;
        public bool Detection { get; set; } = true;
        public EmulatorWriteMode WriteMode { get; private set; } = EmulatorWriteMode.Normal;
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Wizard test";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var r in _parser.Push(data))
            {
                Requests.Add(r);
                var payload = Array.Empty<byte>();
                var status = Status.Success;
                switch (r.Command)
                {
                    case Command.Hf14aScan:
                        (status, payload) = ScanResponse?.Invoke() ??
                            (Status.HfTagOk, [(byte)card.Uid.Length, ..card.Uid, 4, 0, 8, 0]);
                        break;
                    case Command.GetSlotInfo:
                        payload = new byte[32];
                        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)card.Tag);
                        break;
                    case Command.GetEnabledSlots:
                        payload = new byte[16]; payload[2] = 1; break;
                    case Command.GetAllSlotNicks:
                        var nick = Encoding.UTF8.GetBytes(card.Name);
                        payload = new byte[16 + nick.Length]; payload[2] = (byte)nick.Length;
                        nick.CopyTo(payload, 3); break;
                    case Command.GetActiveSlot: payload = [(byte)(ActiveSlot - 1)]; break;
                    case Command.SetActiveSlot: ActiveSlot = r.Data[0] + 1; break;
                    case Command.GetDeviceMode: payload = [(byte)Mode]; break;
                    case Command.ChangeDeviceMode: Mode = (DeviceMode)r.Data[0]; break;
                    case Command.Hf14aGetAntiCollData:
                        payload = [(byte)card.Uid.Length, ..card.Uid, 4, 0, 8, 0]; break;
                    case Command.Mf1GetEmulatorConfig:
                        payload = [(byte)(Detection ? 1 : 0), 0, 0, 0, (byte)WriteMode]; break;
                    case Command.Mf1SetWriteMode:
                        if (FailWriteSetup && r.Data[0] == (byte)EmulatorWriteMode.Denied) status = Status.ParErr;
                        else WriteMode = (EmulatorWriteMode)r.Data[0];
                        break;
                    case Command.Mf1SetDetectionEnable: Detection = r.Data[0] != 0; break;
                    case Command.Mf1GetDetectionCount: payload = [0, 0, 0, 2]; break;
                    case Command.Mf1GetDetectionLog:
                        // Independent C reference exchanges for the all-zero key.
                        payload = Convert.FromHexString("03001234567887654321E6B99E333748C47B030012345678876543215C62D73055F2311D");
                        break;
                }
                OnRequest?.Invoke(r);
                _replies.Writer.TryWrite(FrameCodec.Encode(r.Command, payload, (ushort)status));
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
