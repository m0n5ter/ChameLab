using System.Buffers.Binary;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Cli.Tests;

public sealed class AutopwnTests
{
    [Theory]
    [InlineData(MifareClassicPrngType.Weak, false)]
    [InlineData(MifareClassicPrngType.Weak, true)]
    [InlineData(MifareClassicPrngType.Static, false)]
    [InlineData(MifareClassicPrngType.Static, true)]
    [InlineData(MifareClassicPrngType.Hard, false)]
    [InlineData(MifareClassicPrngType.Hard, true)]
    public async Task UsesAutoOrderAndReusesTheSuppliedKey(MifareClassicPrngType prng, bool batch)
    {
        await using var card = new CardFixture(prng, batch) { Key = Convert.FromHexString("123456789ABC") };
        await card.OpenAsync();

        Assert.Equal(0, await card.Cli.ExecuteAsync("hf mf autopwn -k 123456789ABC"));

        var requests = card.Device.Requests;
        var captures = requests.Where(r => r.Command == Command.Mf1EncNestedAcquire).ToArray();
        Assert.Equal(4, captures.Length);
        var firstCheck = requests.FindIndex(r => r.Command is Command.Mf1CheckKeysOfSectors or Command.Mf1AuthOneKeyBlock);
        Assert.All(captures, r => Assert.True(requests.IndexOf(r) < firstCheck));
        var darkside = requests.Where(r => r.Command == Command.Mf1DarksideAcquire).ToArray();
        Assert.Equal(prng == MifareClassicPrngType.Weak ? 1 : 0, darkside.Length);
        Assert.All(darkside, r => Assert.InRange(requests.IndexOf(r), requests.IndexOf(captures[^1]) + 1, firstCheck - 1));
        Assert.DoesNotContain(requests, r => r.Command is Command.Mf1NestedAcquire or Command.Mf1StaticNestedAcquire or Command.Mf1HardnestedAcquire);
        if (batch) Assert.Single(requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
        else Assert.All(requests.Where(r => r.Command == Command.Mf1AuthOneKeyBlock),
            r => Assert.Equal(card.Key, r.Data[2..]));
        Assert.Equal(Enumerable.Repeat(card.Key, 10).SelectMany(k => k!).ToArray(), card.Read(".key"));
        Assert.Equal(new[] { "123456789ABC" }, File.ReadAllLines(card.PathFor(".dic")));
        var dump = card.Read(".bin");
        Assert.Equal(320, dump.Length);
        Assert.Equal(Enumerable.Repeat((byte)1, 16), dump[..16]);
        Assert.Contains("Static encrypted nested", card.Output.ToString());
        Assert.Contains("123456789ABC", card.Output.ToString());
        Assert.Equal("", card.Error.ToString());
        card.AssertModeRestored();
    }

    [Fact]
    public async Task UnrecoverableCardSavesPartialFilesAndDoesNotRepeatDictionaryChecks()
    {
        await using var card = new CardFixture(MifareClassicPrngType.Hard, true) { Key = null };
        await card.OpenAsync();

        Assert.Equal(1, await card.Cli.ExecuteAsync("hf mf autopwn"));

        Assert.Equal(new byte[60], card.Read(".key"));
        Assert.Equal(new byte[320], card.Read(".bin"));
        Assert.Empty(File.ReadAllLines(card.PathFor(".dic")));
        var checkedKeys = card.Device.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors)
            .SelectMany(r => r.Data[10..].Chunk(6)).Select(Convert.ToHexString).ToArray();
        Assert.Equal(MifareClassic.DefaultKeys.Select(Convert.ToHexString), checkedKeys);
        Assert.DoesNotContain(card.Device.Requests, r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1ReadOneBlock);
        Assert.Contains("mfkey32", card.Output.ToString());
        Assert.Contains("incomplete", card.Error.ToString());
        card.AssertModeRestored();
    }

    [Fact]
    public async Task CancellationDuringFirmwareBatchSavesKeysFromItsReply()
    {
        using var stop = new CancellationTokenSource();
        await using var card = new CardFixture(MifareClassicPrngType.Hard, true);
        card.OnRequest = r => { if (r.Command == Command.Mf1CheckKeysOfSectors) stop.Cancel(); };
        await card.OpenAsync();

        Assert.Equal(130, await card.Cli.ExecuteAsync("hf mf autopwn", stop.Token));

        Assert.Equal(Enumerable.Repeat((byte)0xFF, 60), card.Read(".key"));
        Assert.Equal(new[] { "FFFFFFFFFFFF" }, File.ReadAllLines(card.PathFor(".dic")));
        Assert.Equal(new byte[320], card.Read(".bin"));
        Assert.Single(card.Device.Requests, r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.DoesNotContain(card.Device.Requests, r => r.Command == Command.Mf1ReadOneBlock);
        Assert.Contains("Cancelled", card.Error.ToString());
        card.AssertModeRestored();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 130)]
    public async Task InterruptedReadKeepsKeysAndPreviouslyReadBlocks(bool cancel, int exitCode)
    {
        using var stop = new CancellationTokenSource();
        await using var card = new CardFixture(MifareClassicPrngType.Hard, true);
        card.Respond = r => r.Command == Command.Mf1ReadOneBlock && r.Data[1] == 1
            ? ScriptedTransport.Reply(r, status: Status.HfTagNo) : null;
        card.OnRequest = r => { if (cancel && r.Command == Command.Mf1ReadOneBlock && r.Data[1] == 1) stop.Cancel(); };
        await card.OpenAsync();

        Assert.Equal(exitCode, await card.Cli.ExecuteAsync("hf mf autopwn", stop.Token));

        Assert.Equal(Enumerable.Repeat((byte)0xFF, 60), card.Read(".key"));
        Assert.Equal(new[] { "FFFFFFFFFFFF" }, File.ReadAllLines(card.PathFor(".dic")));
        var dump = card.Read(".bin");
        Assert.Equal(Enumerable.Repeat((byte)1, 16), dump[..16]);
        Assert.Equal(new byte[16], dump[16..32]);
        if (cancel) Assert.Equal(new byte[288], dump[32..]);
        else Assert.Equal(Enumerable.Repeat((byte)3, 16), dump[32..48]);
        card.AssertModeRestored();
    }

    [Fact]
    public async Task ReadableBlocksDoNotHideAnUnrecoveredKey()
    {
        await using var card = new CardFixture(MifareClassicPrngType.Hard, true);
        card.Respond = r =>
        {
            if (r.Command != Command.Mf1CheckKeysOfSectors) return null;
            var response = card.BatchReply(r);
            response[1] &= 0xBF; // Sector 4 B is not the dictionary key.
            Array.Clear(response, 10 + 9 * 6, 6);
            return ScriptedTransport.Reply(r, response, Status.HfTagOk);
        };
        await card.OpenAsync();

        Assert.Equal(1, await card.Cli.ExecuteAsync("hf mf autopwn"));

        Assert.Equal(Enumerable.Repeat((byte)0xFF, 54).Concat(new byte[6]), card.Read(".key"));
        var dump = card.Read(".bin");
        Assert.Equal(320, dump.Length);
        Assert.Equal(Enumerable.Repeat((byte)19, 16), dump[(18 * 16)..(19 * 16)]);
        Assert.Contains("incomplete", card.Error.ToString());
        Assert.DoesNotContain(card.Device.Requests, r => r.Command == Command.Mf1AuthOneKeyBlock);
        card.AssertModeRestored();
    }

    private sealed class CardFixture : IAsyncDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("chamelab-autopwn-").FullName;
        private readonly FolderHost _host;
        public ScriptedTransport Device { get; }
        public CliSession Cli { get; }
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public byte[]? Key { get; init; } = Convert.FromHexString("FFFFFFFFFFFF");
        public Action<ChameleonFrame>? OnRequest { get; set; }
        public Func<ChameleonFrame, ChameleonFrame?>? Respond { get; set; }

        public CardFixture(MifareClassicPrngType prng, bool batch)
        {
            Command[] capabilities = [Command.Mf1CheckKeysOnBlock, Command.Mf1EncNestedAcquire, Command.Mf1DarksideAcquire,
                .. batch ? new[] { Command.Mf1CheckKeysOfSectors } : Array.Empty<Command>()];
            Device = new ScriptedTransport(r =>
            {
                var response = Respond?.Invoke(r) ?? r.Command switch
                {
                    Command.GetDeviceMode => ScriptedTransport.Reply(r, [0]),
                    Command.Hf14aScan => ScriptedTransport.Reply(r, [4, 1, 2, 3, 4, 4, 0, 9, 0], Status.HfTagOk),
                    Command.GetDeviceCapabilities => ScriptedTransport.Reply(r, capabilities.SelectMany(c =>
                    {
                        var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)c); return bytes;
                    }).ToArray()),
                    Command.Mf1DetectPrng => ScriptedTransport.Reply(r, [(byte)prng], Status.HfTagOk),
                    Command.Mf1EncNestedAcquire => ScriptedTransport.Reply(r, status: Status.MfErrAuth),
                    Command.Mf1DarksideAcquire => ScriptedTransport.Reply(r, [(byte)DarksideResult.Fixed], Status.HfTagOk),
                    Command.Mf1CheckKeysOfSectors => ScriptedTransport.Reply(r, BatchReply(r), Status.HfTagOk),
                    Command.Mf1AuthOneKeyBlock => ScriptedTransport.Reply(r,
                        status: Key is not null && r.Data.AsSpan(2).SequenceEqual(Key) ? Status.HfTagOk : Status.MfErrAuth),
                    Command.Mf1ReadOneBlock => ScriptedTransport.Reply(r,
                        MifareClassic.IsTrailer(r.Data[1]) ? Convert.FromHexString("0000000000007F078869000000000000")
                            : Enumerable.Repeat((byte)(r.Data[1] + 1), 16).ToArray(), Status.HfTagOk),
                    _ => ScriptedTransport.Reply(r),
                };
                OnRequest?.Invoke(r);
                return response;
            });
            _host = new FolderHost(_folder);
            Cli = new CliSession(Output, Error, _host);
        }

        public byte[] BatchReply(ChameleonFrame request)
        {
            var response = new byte[490];
            for (var bit = 0; bit < 10; bit++)
            {
                if ((request.Data[bit / 8] & (0x80 >> (bit % 8))) != 0 || Key is null
                    || !request.Data[10..].Chunk(6).Any(k => k.AsSpan().SequenceEqual(Key))) continue;
                response[bit / 8] |= (byte)(0x80 >> (bit % 8));
                Key.CopyTo(response, 10 + bit * 6);
            }
            return response;
        }

        public Task OpenAsync() => _host.AttachAsync(Device);
        public string PathFor(string extension) => Path.Combine(_folder, "01020304" + extension);
        public byte[] Read(string extension) => File.ReadAllBytes(PathFor(extension));
        public void AssertModeRestored()
        {
            Assert.Equal(Command.ChangeDeviceMode, Device.Requests.Last().Command);
            Assert.Equal(new byte[] { 0 }, Device.Requests.Last().Data);
        }

        public async ValueTask DisposeAsync()
        {
            await Cli.DisposeAsync();
            await _host.DisposeAsync();
            Output.Dispose(); Error.Dispose();
            Directory.Delete(_folder, recursive: true);
        }

        private sealed class FolderHost(string folder) : StandaloneCliHost
        {
            public override string ResolvePath(string path) => Path.Combine(folder, path);
        }
    }
}
