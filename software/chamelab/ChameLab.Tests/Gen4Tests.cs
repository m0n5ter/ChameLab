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
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public sealed class Gen4Tests
{
    private const string FactoryConfigHex = "00000000000002000978009102DABC191010111213141516040008006B02";

    [Fact]
    public void PackPage_FollowsCcRevisionAndRequiresChoiceForMalformedVersions()
    {
        Assert.Equal(0x13, Gen4Client.PackPage(Gen4Client.ParseHex("00000006A0")));
        Assert.Equal(0x13, Gen4Client.PackPage(Gen4Client.ParseHex("00000066661234")));
        Assert.Equal(0xE6, Gen4Client.PackPage(Gen4Client.ParseHex("00000003ABE731")));
        Assert.Null(Gen4Client.PackPage(Gen4Client.ParseHex("3F")));
        Assert.Null(Gen4Client.PackPage(Gen4Client.ParseHex("6300")));
        Assert.Null(Gen4Client.PackPage(Gen4Client.ParseHex("90000003AB")));
    }

    [Fact]
    public void FromResponse_Accepts30_32_And_34ByteFrames()
    {
        // Raw response from RfidResearchGroup/proxmark3#1822, including RF CRC.
        var response = Gen4Client.ParseHex("00000000000002090978009102BDAC191310111213141516040008026B004F6BF441");
        var config = Gen4Configuration.FromResponse(response);
        Assert.Equal("00000000000002090978009102BDAC191310111213141516040008026B00", Gen4Client.ToHex(config.RawBytes));
        Assert.Equal(9, config.Ats.Length);
        Assert.Equal("0978009102BDAC1913", Gen4Client.ToHex(config.Ats));
    }

    [Fact]
    public void DecodesEveryConfigurationFieldAndRetainsUnusedAtsBytes()
    {
        var bytes = Gen4Client.ParseHex(FactoryConfigHex);
        Assert.Equal(30, bytes.Length);
        var config = Gen4Configuration.FromResponse([.. bytes, 0x4F, 0x6B]);
        Assert.Equal(0, config.Protocol);
        Assert.Equal(4, config.UidLength);
        Assert.Equal([0, 0, 0, 0], config.Password);
        Assert.Equal(2, config.ShadowMode);
        Assert.Empty(config.Ats);
        Assert.Equal([0, 4], config.Atqa);
        Assert.Equal(8, config.Sak);
        Assert.Equal(0, config.UltralightMode);
        Assert.Equal(0x6B, config.MaxBlock);
        Assert.Equal(2, config.Block0Mode);
        Assert.Equal(FactoryConfigHex, Gen4Client.ToHex(config.RawBytes));

        var edited = config.WithEdits(
            protocol: 0,
            uidSizeCode: 1,
            password: [0x11, 0x22, 0x33, 0x44],
            shadowMode: 3,
            ats: [0x05, 0x78],
            atqa: [0x00, 0x44],
            sak: 0x20,
            ultralightMode: 1,
            maxBlock: 0x3F,
            block0Mode: 0);
        // Unused ATS bytes [10..24) remain untouched for lossless round trip.
        Assert.Equal(bytes[10..24], edited.RawBytes.Slice(10, 14).ToArray());
        Assert.Equal([0x00, 0x44], edited.Atqa);
        Assert.Equal(0x44, edited.RawBytes[24]);
        Assert.Equal(0x00, edited.RawBytes[25]);

        Assert.Throws<FormatException>(() => Gen4Configuration.FromResponse(new byte[2]));
        bytes[7] = 17;
        Assert.Throws<FormatException>(() => new Gen4Configuration(bytes));
    }

    [Fact]
    public async Task WriteConfiguration_UpdatesCredentialOnlyAfterSuccessAndSupportsPermanentLock()
    {
        var commands = new List<byte[]>();
        var reject = true;
        var client = new Gen4Client((cmd, _) =>
        {
            commands.Add(cmd);
            return Task.FromResult(Gen4Client.ParseHex(reject ? "6300" : "9000FD07"));
        }, new byte[4]);

        var baseBytes = Gen4Client.ParseHex(FactoryConfigHex);
        var config = new Gen4Configuration(baseBytes).WithEdits(
            0, 0, [0xAA, 0xBB, 0xCC, 0xDD], 2, [], [0x00, 0x04], 0x08, 0, 0x6B, 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.WriteConfigurationAsync(config));
        reject = false;
        await client.WriteConfigurationAsync(config);
        await client.WriteConfigurationAsync(config, permanent: true);

        Assert.Equal("CF00000000F0", Gen4Client.ToHex(commands[0].AsSpan(0, 6)));
        Assert.Equal("CF00000000F0", Gen4Client.ToHex(commands[1].AsSpan(0, 6)));
        Assert.Equal($"CFAABBCCDDF1{Gen4Client.ToHex(config.RawBytes)}", Gen4Client.ToHex(commands[2]));
    }

    [Fact]
    public async Task WriteUid_PreservesManufacturerBytesAndComputesBccForClassic4Byte()
    {
        var original = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[]? written = null;
        var client = new Gen4Client((cmd, _) =>
        {
            if (cmd[5] == 0xCE) return Task.FromResult((byte[])original.Clone());
            written = cmd;
            return Task.FromResult(Gen4Client.ParseHex("9000"));
        }, new byte[4]);

        var config = new Gen4Configuration(Gen4Client.ParseHex(FactoryConfigHex));
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteUidAsync(config, new byte[7]));
        Assert.Null(written);

        await client.WriteUidAsync(config, Gen4Client.ParseHex("11223344"));
        Assert.NotNull(written);
        Assert.Equal([0xCF, 0, 0, 0, 0, 0xCD, 0], written![..7]);
        Assert.Equal([0x11, 0x22, 0x33, 0x44, 0x44], written[7..12]);
        Assert.Equal(original[5..], written[12..]);
    }

    [Fact]
    public async Task WriteUid_Classic7BytePreservesSakAndManufacturerData()
    {
        var original = Gen4Client.ParseHex("04112233445566884400010203040506");
        byte[]? written = null;
        var client = new Gen4Client((cmd, _) =>
        {
            if (cmd[5] == 0xCE) return Task.FromResult((byte[])original.Clone());
            written = cmd[7..];
            return Task.FromResult(Gen4Client.ParseHex("9000"));
        }, new byte[4]);

        var raw = Gen4Client.ParseHex(FactoryConfigHex);
        raw[1] = 1;
        var config = new Gen4Configuration(raw);
        var uid = Gen4Client.ParseHex("04123456789ABC");
        await client.WriteUidAsync(config, uid);
        Assert.NotNull(written);
        Assert.Equal(uid, written![..7]);
        Assert.Equal(original[7..], written[7..]);
    }

    [Fact]
    public async Task WriteUid_Ultralight7ByteUpdatesBccWithoutOverwritingLockOrOtp()
    {
        var memory = Enumerable.Range(0, 3).Select(i => Enumerable.Repeat((byte)(0xA0 + i), 16).ToArray()).ToArray();
        var client = new Gen4Client((cmd, _) =>
        {
            var block = cmd[6];
            if (cmd[5] == 0xCE) return Task.FromResult((byte[])memory[block].Clone());
            memory[block] = cmd[7..];
            return Task.FromResult(Gen4Client.ParseHex("9000"));
        }, new byte[4]);

        var raw = Gen4Client.ParseHex(FactoryConfigHex);
        raw[0] = 1;
        raw[1] = 1;
        var config = new Gen4Configuration(raw);
        await client.WriteUidAsync(config, Gen4Client.ParseHex("04112233445566"));

        Assert.Equal([4, 0x11, 0x22, 0xBF], memory[0][..4]);
        Assert.Equal([0x33, 0x44, 0x55, 0x66], memory[1][..4]);
        Assert.Equal(0x44, memory[2][0]);
        Assert.Equal(Enumerable.Repeat((byte)0xA2, 15).ToArray(), memory[2][1..]);
    }

    [Fact]
    public async Task MemoryEditor_PreservesBytesOutsideTargetRange()
    {
        var memory = new Dictionary<int, byte[]>
        {
            [0x13] = Enumerable.Repeat((byte)0xAA, 16).ToArray(),
            [0xFA] = Enumerable.Repeat((byte)0xBB, 16).ToArray(),
            [0xFB] = Enumerable.Repeat((byte)0xCC, 16).ToArray(),
        };
        var client = new Gen4Client((cmd, _) =>
        {
            var address = cmd[6];
            if (cmd[5] == 0xCE) return Task.FromResult((byte[])memory[address].Clone());
            memory[address] = cmd[7..];
            return Task.FromResult(Gen4Client.ParseHex("9000"));
        }, new byte[4]);

        await client.WriteMemoryAsync(0x13, Gen4Client.ParseHex("8080"), 4);
        Assert.Equal([0x80, 0x80], memory[0x13][..2]);
        Assert.Equal(Enumerable.Repeat((byte)0xAA, 14).ToArray(), memory[0x13][2..]);

        var version = Gen4Client.ParseHex("0004040201000F03");
        await client.WriteMemoryAsync(0xFA, version, 4);
        Assert.Equal(version, await client.ReadMemoryAsync(0xFA, 8, 4));
        Assert.Equal(Enumerable.Repeat((byte)0xBB, 12).ToArray(), memory[0xFA][4..]);
        Assert.Equal(Enumerable.Repeat((byte)0xCC, 12).ToArray(), memory[0xFB][4..]);
    }

    [Fact]
    public async Task ParseHexAndBounds_RejectInvalidInputBeforeTransport()
    {
        var calls = 0;
        var client = new Gen4Client((_, _) =>
        {
            calls++;
            return Task.FromResult(Array.Empty<byte>());
        }, new byte[4]);

        foreach (var bad in new[] { "0", "zz", "000" })
            Assert.Throws<FormatException>(() => Gen4Client.ParseHex(bad));
        Assert.Equal([0xAB, 0xCD], Gen4Client.ParseHex("ab cd"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReadBlockAsync(256));
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteBlockAsync(0, new byte[1]));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Gen4Client_OverChameleonClient_SendsExpectedHf14aRawFrames()
    {
        var transport = new Gen4Transport();
        await using var conn = new ChameleonConnection(transport);
        await conn.OpenAsync();
        var gen4 = new Gen4Client(new ChameleonClient(conn), new byte[4]);

        var cfg = await gen4.ReadConfigurationAsync();
        Assert.Equal(FactoryConfigHex, Gen4Client.ToHex(cfg.RawBytes));
        var ver = await gen4.ReadVersionAsync();
        Assert.Equal("00000006A0", Gen4Client.ToHex(ver));

        // Verify raw 14A options: ActivateRfField(0x80) | WaitResponse(0x40) | AppendCrc(0x20) | AutoSelect(0x10) = 0xF0; CheckResponseCrc(0x04) is off.
        Assert.All(transport.RawOptions, opt => Assert.Equal(0xF0, opt));
    }

    [Fact]
    public void ToolsViewModel_TracksValidationAndConfirmationState()
    {
        var vm = new ToolsViewModel(new DeviceService([]));
        vm.OpenGen4Command.Execute(null);
        Assert.True(vm.IsGen4);
        Assert.False(vm.Gen4HasConfig);

        vm.Gen4Password = "12 34";
        Assert.NotNull(vm.Gen4PasswordError);
        vm.Gen4Password = "00 00 00 00";
        Assert.Null(vm.Gen4PasswordError);

        Assert.False(vm.IsGen4Confirming);
        Assert.False(vm.Gen4ShowShadowWarning);
        vm.Gen4ShadowOption = vm.Gen4ShadowOptions.Single(o => o.Value == 1);
        Assert.True(vm.Gen4ShowShadowWarning);
    }

    private sealed class Gen4Transport : ITransport
    {
        private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        private readonly byte[] _config = Gen4Client.ParseHex(FactoryConfigHex);

        public List<byte> RawOptions { get; } = [];
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Gen4 fake";
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var chunk = await _inbox.Reader.ReadAsync(ct);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var frame in _parser.Push(data))
            {
                if (frame.Command == Command.Hf14aRaw)
                {
                    RawOptions.Add(frame.Data[0]);
                    var raw = frame.Data[5..];
                    var reply = raw[5] switch
                    {
                        0xC6 => _config,
                        0xCC => Gen4Client.ParseHex("00000006A0"),
                        _ => Gen4Client.ParseHex("9000"),
                    };
                    _inbox.Writer.TryWrite(FrameCodec.Encode(Command.Hf14aRaw, reply, (ushort)Status.HfTagOk));
                }
                else
                {
                    _inbox.Writer.TryWrite(FrameCodec.Encode(frame.Command, [], (ushort)Status.Success));
                }
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _inbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
