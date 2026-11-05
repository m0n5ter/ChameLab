using ChameLab.Protocol;
using ChameLab.Models;
using System.Buffers.Binary;
using Xunit;

namespace ChameLab.Cli.Tests;

public class CommandTests
{
    private static CliArguments Parse(string line)
    {
        var (command, args) = CommandCatalog.Find(CommandLine.Split(line));
        return CliArguments.Parse(command!, args);
    }

    [Fact]
    public async Task EveryLegacyCommandHasHandlerAndOfflineHelp()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        await using var cli = new CliSession(output, error);
        Assert.Equal(115, CommandCatalog.Commands.Count);
        Assert.Equal(CommandCatalog.Commands.Select(c => c.Name).Order(), cli.ImplementedCommands.Order());
        foreach (var command in CommandCatalog.Commands)
        {
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await cli.ExecuteAsync(command.Name + " --help"));
            Assert.Contains(command.Name, output.ToString());
        }
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("hw slot change")]
    [InlineData("hw slot change --slot 9")]
    [InlineData("hw slot delete -s 2")]
    [InlineData("hw slot delete -s 2 --hf --lf")]
    [InlineData("hw mode --reader --emulator")]
    [InlineData("hf 14a raw --unknown")]
    [InlineData("hf mf rdbl --blk 2 -k FFFFFFFFFFFF -a -b")]
    public void InvalidArgumentsFailBeforeExecution(string line) => Assert.Throws<ArgumentException>(() => Parse(line));

    [Fact]
    public void ParserKeepsConstDefaultsAndCombinedFlags()
    {
        var raw = Parse("hf 14a raw -sc -d 26 --timeout=100");
        Assert.True(raw.Flag("select_tag")); Assert.True(raw.Flag("crc"));
        Assert.Equal(new byte[] { 0x26 }, raw.Hex("data")); Assert.Equal(100, raw.Int("timeout"));
        Assert.Equal(0, Parse("hw slot prng --type 0").Int("type"));
    }

    [Fact]
    public void TokenizerPreservesWindowsPathsAndEmptyArguments()
    {
        Assert.Equal(new[] { "hf", "mf", "eload", "-f", @"C:\My Cards\card.bin", "" },
            CommandLine.Split("hf mf eload -f \"C:\\My Cards\\card.bin\" ''"));
        Assert.Throws<ArgumentException>(() => CommandLine.Split("rem 'unfinished"));
        Assert.Contains("hf mf hardnested", LineEditor.Complete("hf mf hard"));
        Assert.Contains("hf 14a raw --timeout", LineEditor.Complete("hf 14a raw --time"));
    }

    [Fact]
    public async Task RawCommandEncodesBitsFlagsAndRestoresMode()
    {
        using var error = new StringWriter(); using var output = new StringWriter();
        await using var cli = new CliSession(output, error);
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r,
            r.Command == Command.GetDeviceMode ? [0] : r.RawCommand == 2010 ? [4, 0] : []));
        await cli.AttachAsync(device);
        Assert.Equal(0, await cli.ExecuteAsync("hf 14a raw -sc -d 26 -b 7 --timeout 100"));
        var raw = Assert.Single(device.Requests, r => r.RawCommand == 2010);
        Assert.Equal(Convert.FromHexString("700064000726"), raw.Data);
        Assert.Equal(new byte[] { 0 }, device.Requests.Last().Data);
        Assert.Equal((ushort)1001, device.Requests.Last().RawCommand);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 130)]
    public async Task ReaderModeRestoredAfterFailureOrCancellation(bool cancel, int exitCode)
    {
        using var cts = new CancellationTokenSource();
        await using var cli = new CliSession(TextWriter.Null, TextWriter.Null);
        var device = new ScriptedTransport(r =>
        {
            if (r.RawCommand == 2010 && cancel) cts.Cancel();
            return ScriptedTransport.Reply(r, r.Command == Command.GetDeviceMode ? [0] : [],
                r.RawCommand == 2010 ? Status.HfTagNo : Status.Success);
        });
        await cli.AttachAsync(device);
        Assert.Equal(exitCode, await cli.ExecuteAsync("hf 14a raw -d 26", cts.Token));
        Assert.Equal((ushort)1001, device.Requests.Last().RawCommand);
        Assert.Equal(new byte[] { 0 }, device.Requests.Last().Data);
    }

    [Fact]
    public async Task ChangingSlotPersistsWhereasScopedEmulatorOperationRestoresIt()
    {
        await using var cli = new CliSession(TextWriter.Null, TextWriter.Null);
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r,
            r.Command == Command.GetActiveSlot ? [2] : [], r.RawCommand == 4026 ? Status.InvalidSlotType : Status.Success));
        await cli.AttachAsync(device);
        Assert.Equal(0, await cli.ExecuteAsync("hw slot change --slot 8"));
        Assert.Single(device.Requests);
        Assert.Equal(new byte[] { 7 }, device.Requests.Single().Data);
        Assert.False(CommandCatalog.Commands.Single(c => c.Name == "hw slot change").ScopedSlot);
        device.Requests.Clear();
        // A read operation on another emulator must restore the previous active slot.
        var scoped = new ScriptedTransport(r => ScriptedTransport.Reply(r,
            r.Command == Command.GetActiveSlot ? [2] : r.RawCommand == 4034 ? [0, 0, 0, 0] : []));
        await cli.AttachAsync(scoped);
        Assert.Equal(0, await cli.ExecuteAsync("hf mfu edetect -s 7 --count 0"));
        var changes = scoped.Requests.Where(r => r.Command == Command.SetActiveSlot).ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal(new byte[] { 6 }, changes[0].Data);
        Assert.Equal(new byte[] { 2 }, changes[1].Data);
    }

    [Fact]
    public async Task UltralightWriteExpectsShortAckWithoutReceiveCrcCheck()
    {
        await using var cli = new CliSession(TextWriter.Null, TextWriter.Null);
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r,
            r.Command == Command.GetDeviceMode ? [1] : r.RawCommand == 2010 ? [0x0A] : []));
        await cli.AttachAsync(device);
        Assert.Equal(0, await cli.ExecuteAsync("hf mfu wrpg -p 4 -d 01020304"));
        var raw = Assert.Single(device.Requests, r => r.RawCommand == 2010);
        Assert.Equal(Convert.FromHexString("7003E80030A20401020304"), raw.Data);
    }

    [Fact]
    public async Task DisconnectedCommandsAndUnknownCommandsReturnFailures()
    {
        await using var cli = new CliSession(TextWriter.Null, TextWriter.Null);
        Assert.Equal(1, await cli.ExecuteAsync("hw version"));
        Assert.Equal(2, await cli.ExecuteAsync("does not exist"));
        Assert.Equal(0, await cli.ExecuteAsync("rem test"));
        Assert.Equal(0, await cli.ExecuteAsync("quit"));
        Assert.True(cli.ExitRequested);
    }

    [Fact]
    public async Task FailedAuthTraceStillDecryptsReaderResponseAndKeepsNineByteSelect()
    {
        // NT/NR/AR from the original C fixture crypto_vectors.json, key 000000000000.
        var trace = new List<byte>();
        void Frame(bool card, byte[] data)
        {
            var header = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)(data.Length * 8 | (card ? 0x8000 : 0)));
            trace.AddRange(header); trace.AddRange(data);
        }
        byte[] Crc(byte[] bytes) { var crc = HfSniff.CrcA(bytes); return [.. bytes, (byte)crc, (byte)(crc >> 8)]; }
        byte[] Word(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
        Frame(false, Crc([0x93, 0x70, 0x12, 0x34, 0x56, 0x78, 0x08]));
        Frame(false, Crc([0x60, 0]));
        Frame(true, Word(2271560481));
        Frame(false, [.. Word(3870924339), .. Word(927515771)]);
        using var output = new StringWriter();
        await using var cli = new CliSession(output, TextWriter.Null);
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r,
            r.Command == Command.GetDeviceMode ? [1] : r.RawCommand == 2017 ? trace.ToArray() : [],
            r.RawCommand == 2017 ? Status.MfErrAuth : Status.Success));
        await cli.AttachAsync(device);
        Assert.Equal(1, await cli.ExecuteAsync("hf 14a auth-trace --blk 0 -k 000000000000"));
        Assert.Contains("UID=12345678", output.ToString());
        Assert.Contains("; MATCH", output.ToString());
        Assert.DoesNotContain("MISMATCH", output.ToString());
        Assert.Contains("AT not received", output.ToString());
    }

    [Theory]
    [InlineData("H10301", 256, 1)]
    [InlineData("H10301", 1, 65536)]
    [InlineData("H10302", 1, 1)]
    [InlineData("H10302", 0, -1)]
    public void HidRejectsValuesThatWouldBeSilentlyTruncated(string format, long fc, long cn)
        => Assert.Throws<ArgumentException>(() => HidLimits.Validate(format, fc, cn, 0, 0));

    [Fact]
    public async Task ElectraUsesItsOwnWriteCommandAndThirteenByteId()
    {
        await using var cli = new CliSession(TextWriter.Null, TextWriter.Null);
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r, r.Command == Command.GetDeviceMode ? [1] : []));
        await cli.AttachAsync(device);
        Assert.Equal(0, await cli.ExecuteAsync("lf em 410x write --id DEADBEEF880102030405060708"));
        Assert.Equal(Convert.FromHexString("DEADBEEF880102030405060708"), Assert.Single(device.Requests, r => r.RawCommand == 3006).Data[..13]);
    }

    [Fact]
    public async Task Em4305CommandsPreserveNewMainFeaturesAndVerifyWrites()
    {
        await using var cli = new CliSession(TextWriter.Null, TextWriter.Null);
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r,
            r.Command == Command.GetDeviceMode ? [1] : r.RawCommand == 3036 ? Convert.FromHexString("12345678") : [],
            r.RawCommand is 3034 or 3035 or 3036 ? Status.LfTagOk : Status.Success));
        await cli.AttachAsync(device);
        Assert.Equal(0, await cli.ExecuteAsync("lf em 410x write --id 0102030405 --em4305 --pwd AABBCCDD"));
        Assert.Equal(Convert.FromHexString("0102030405AABBCCDD"), Assert.Single(device.Requests, r => r.RawCommand == 3034).Data);
        Assert.Equal(0, await cli.ExecuteAsync("lf em 4x05 wrbl -b 5 -d 12345678 --pwd AABBCCDD"));
        Assert.Equal(Convert.FromHexString("0512345678AABBCCDD"), Assert.Single(device.Requests, r => r.RawCommand == 3035).Data);
        Assert.Equal(Convert.FromHexString("05AABBCCDD"), Assert.Single(device.Requests, r => r.RawCommand == 3036).Data);
        var count = device.Requests.Count;
        Assert.Equal(2, await cli.ExecuteAsync("lf em 4x05 wrbl -b 1 -d 12345678"));
        Assert.DoesNotContain(device.Requests.Skip(count), r => r.RawCommand == 3035);
    }
}
