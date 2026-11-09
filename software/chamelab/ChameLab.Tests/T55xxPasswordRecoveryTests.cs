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

[Collection(AppSettingsCollection.Name)]
public class T55xxPasswordRecoveryTests
{
    [Theory]
    [InlineData(null, "20206666")]
    [InlineData("AABBCCDD", "AABBCCDD")]
    public async Task RecoveryVerifiesProbeRestoresOriginalIdAndReportsActualKey(string? target, string expected)
    {
        var transport = new PasswordTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var progress = new List<(int, int)>();
        string? found = null;
        var result = await T55xxPasswordRecovery.RecoverAsync(new(connection),
            [Hex.Parse("11111111"), Hex.Parse("12345678"), Hex.Parse("12345678"), Hex.Parse("87654321")],
            target is null ? null : Hex.Parse(target), (done, total) => progress.Add((done, total)),
            keyFound: key => found = Convert.ToHexString(key!));
        Assert.Equal("12345678", found);
        Assert.Equal(Hex.Parse(found!), result!.Key);
        Assert.Equal(expected, Convert.ToHexString(transport.Password));
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.Equal(DeviceMode.Tag, transport.Mode);
        Assert.Equal(new[] { (0, 3), (1, 3), (2, 3) }, progress);
        Assert.DoesNotContain(transport.Attempts, r => r.Data[6..10].SequenceEqual(Hex.Parse("87654321")));
        Assert.All(transport.Attempts, r =>
        {
            Assert.Equal(11, r.Data.Length);
            Assert.Contains(r.Data[0], new byte[] { 0, 1, 2 });
            Assert.Equal(0, r.Data[10]);
        });
        Assert.Contains(transport.ScannedIds, id => !id.SequenceEqual(PasswordTransport.OriginalId));
        Assert.Equal(PasswordTransport.OriginalId, transport.ScannedIds.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalWriteUsesDictionaryAndLeavesRequestedId(bool sameId)
    {
        var transport = new PasswordTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var desired = sameId ? PasswordTransport.OriginalId : Hex.Parse("AABBCCDDEE");
        var result = await T55xxPasswordRecovery.WriteAsync(new(connection), TagSpecificType.Em410x, desired,
            [Hex.Parse("FFFFFFFF"), Hex.Parse("12345678")], Hex.Parse("DEADBEEF"));
        Assert.Equal(Hex.Parse("12345678"), result!.Key);
        Assert.Equal(desired, transport.ReadId());
        Assert.Equal("DEADBEEF", Convert.ToHexString(transport.Password));
        Assert.Contains(transport.ScannedIds, id => !id.SequenceEqual(desired) && !id.SequenceEqual(PasswordTransport.OriginalId));
        Assert.Equal(desired, transport.ScannedIds.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringProbeDrainsReplyVerifiesAndRestoresBeforeReturning(bool normalWrite)
    {
        var transport = new PasswordTransport { PauseFirstProtectedReply = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        using var cts = new CancellationTokenSource();
        var run = normalWrite
            ? T55xxPasswordRecovery.WriteAsync(new(connection), TagSpecificType.Em410x, Hex.Parse("AABBCCDDEE"),
                [Hex.Parse("12345678"), Hex.Parse("87654321")], ct: cts.Token)
            : T55xxPasswordRecovery.RecoverAsync(new(connection),
                [Hex.Parse("12345678"), Hex.Parse("87654321")], ct: cts.Token);
        await transport.FirstProtectedAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.False(run.IsCompleted);
        Assert.Equal(DeviceMode.Reader, transport.Mode);
        transport.ReleaseReply();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.Equal("12345678", Convert.ToHexString(transport.Password));
        Assert.DoesNotContain(transport.Attempts, r => r.Data[6..10].SequenceEqual(Hex.Parse("87654321")));
        Assert.Equal(DeviceMode.Tag, transport.Mode);
        var result = await T55xxPasswordRecovery.RecoverAsync(new(connection), [Hex.Parse("12345678")]);
        Assert.NotNull(result);
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
    }

    [Fact]
    public async Task PartialProbeFailureRestoresOriginalAndStops()
    {
        var transport = new PasswordTransport { FailSecondProtectedBlock = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ChameleonException>(() => T55xxPasswordRecovery.RecoverAsync(new(connection),
            [Hex.Parse("12345678"), Hex.Parse("87654321")]));
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.Equal(DeviceMode.Tag, transport.Mode);
        Assert.DoesNotContain(transport.Attempts, r => r.Data[6..10].SequenceEqual(Hex.Parse("87654321")));
    }

    [Fact]
    public async Task FailedReadBackRestoresOriginalAndNeverReportsAMatch()
    {
        var transport = new PasswordTransport { MissProbeReads = 3 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var found = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => T55xxPasswordRecovery.RecoverAsync(new(connection),
            [Hex.Parse("12345678")], keyFound: _ => found = true));
        Assert.False(found);
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.Equal(DeviceMode.Tag, transport.Mode);
    }

    [Fact]
    public async Task FailedRestorationReportsOriginalIdAndKeysInsteadOfSuccess()
    {
        var transport = new PasswordTransport { RejectFinalWrites = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        string? found = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => T55xxPasswordRecovery.RecoverAsync(new(connection),
            [Hex.Parse("12345678")], keyFound: key => found = Convert.ToHexString(key!)));
        Assert.Equal("12345678", found);
        Assert.Contains(Convert.ToHexString(PasswordTransport.OriginalId), error.Message);
        Assert.Contains("12345678", error.Message);
        Assert.Contains("20206666", error.Message);
        Assert.Equal(DeviceMode.Tag, transport.Mode);
    }

    [Fact]
    public async Task KnownPasswordForTheReadIdIsTriedFirst()
    {
        var transport = new PasswordTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        byte[]? askedFor = null;
        var progress = new List<(int, int)>();
        var result = await T55xxPasswordRecovery.RecoverAsync(new(connection),
            [Hex.Parse("00000000"), Hex.Parse("FFFFFFFF")], progress: (done, total) => progress.Add((done, total)),
            knownPassword: card => { askedFor = card.Id; return Hex.Parse("12345678"); });
        Assert.Equal(PasswordTransport.OriginalId, askedFor);
        Assert.Equal(Hex.Parse("12345678"), result!.Key);
        Assert.Equal(PasswordTransport.OriginalId, result.Card.Id);
        Assert.Equal((1, 3), progress.Last());
        Assert.DoesNotContain(transport.Attempts, r => r.Data[6..10].SequenceEqual(Hex.Parse("FFFFFFFF")));
    }

    [Fact]
    public async Task NoMatchDoesNotChangeIdOrPassword()
    {
        var transport = new PasswordTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await T55xxPasswordRecovery.RecoverAsync(new(connection), [Hex.Parse("00000000"), Hex.Parse("FFFFFFFF")]);
        Assert.Null(result);
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.Equal("12345678", Convert.ToHexString(transport.Password));
        Assert.Equal(DeviceMode.Tag, transport.Mode);
    }

    [Fact]
    public async Task SameTargetIdAndNewPasswordCannotFalselyConfirmWrongDictionaryKey()
    {
        var transport = new PasswordTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await T55xxPasswordRecovery.WriteAsync(new(connection), TagSpecificType.Em410x,
            PasswordTransport.OriginalId, [Hex.Parse("FFFFFFFF")], Hex.Parse("12345678"));
        Assert.Null(result);
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Em410xWriteToT55xx);
    }

    [Fact]
    public async Task BlankTagCanBeWrittenWithoutAReadableOriginalOrDictionaryKeys()
    {
        var transport = new PasswordTransport { Blank = true, Protected = false };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var desired = Hex.Parse("AABBCCDDEE");
        var result = await T55xxPasswordRecovery.WriteAsync(new(connection), TagSpecificType.Em410x, desired, []);
        Assert.NotNull(result);
        Assert.Null(result.Key);
        Assert.Equal(desired, transport.ReadId());
        Assert.Equal("20206666", Convert.ToHexString(transport.Password));
    }

    [Fact]
    public async Task NonEmOriginalIsRestoredAndVerifiedUsingItsOriginalProtocol()
    {
        var transport = new PasswordTransport { InitialType = TagSpecificType.Viking };
        var original = transport.ReadId();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        LfScanResult? captured = null;
        var result = await T55xxPasswordRecovery.RecoverAsync(new(connection), [Hex.Parse("12345678")],
            originalRead: card => captured = card);
        Assert.NotNull(result);
        Assert.Equal(TagSpecificType.Viking, captured!.Type);
        Assert.Equal(original, captured.Id);
        Assert.Equal(TagSpecificType.Viking, transport.CurrentType);
        Assert.Equal(original, transport.ReadId());
        Assert.Contains(transport.Requests, r => r.Command == Command.VikingWriteToT55xx);
        Assert.Equal(Command.VikingScan, transport.Requests[^2].Command);
    }

    [Fact]
    public async Task UnprotectedTagIsRecognizedWithoutInventingAMatchingKey()
    {
        var transport = new PasswordTransport { Protected = false };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await T55xxPasswordRecovery.RecoverAsync(new(connection), [Hex.Parse("FFFFFFFF")]);
        Assert.NotNull(result);
        Assert.Null(result.Key);
        Assert.Equal(PasswordTransport.OriginalId, transport.ReadId());
        Assert.Equal("20206666", Convert.ToHexString(transport.Password));
        Assert.All(transport.Attempts, r => Assert.Equal(0, r.Data[5]));
    }

    [Fact]
    public async Task MissingOriginalPreventsToolsFromWriting()
    {
        var transport = new PasswordTransport { Missing = true };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => T55xxPasswordRecovery.RecoverAsync(new(connection), [new byte[4]]));
        Assert.Empty(transport.Attempts);
        Assert.Equal(DeviceMode.Tag, transport.Mode);
    }

    [Fact]
    public async Task InvalidInputOrAlreadyCancelledDoesNotTouchDevice()
    {
        var transport = new PasswordTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAsync<ArgumentException>(() => T55xxPasswordRecovery.RecoverAsync(client, [new byte[6]]));
        await Assert.ThrowsAsync<ArgumentException>(() => T55xxPasswordRecovery.RecoverAsync(client, [new byte[4]], new byte[3]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            T55xxPasswordRecovery.RecoverAsync(client, [new byte[4]], ct: new CancellationToken(true)));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void DictionaryTypeSurvivesSaveCloneImportAndExportWithoutChangingLegacyDictionaries()
    {
        var legacy = KeyDictionary.FromJson("{\"name\":\"Classic\",\"keys\":[[255,255,255,255,255,255]]}");
        Assert.Equal(KeyDictionaryType.MifareClassic, legacy.Type);
        Assert.Equal(6, Assert.Single(legacy.Keys).Length);
        var password = KeyDictionary.FromText("Passwords", "# comment\n20206666\n12 34 56 78\n20206666");
        Assert.Equal(KeyDictionaryType.T55xx, password.Type);
        Assert.Equal(2, password.Count);
        Assert.Equal(8, (int)password.ToJson()["keyLength"]!);
        Assert.Equal(KeyDictionaryType.T55xx, KeyDictionary.FromJson(password.ToJson()).Type);
        Assert.Equal(KeyDictionaryType.T55xx, password.Clone().Type);
        Assert.Equal(password.ToText(), KeyDictionary.FromText("Reload", password.ToText()).ToText());
        Assert.Equal(KeyDictionaryType.T55xx,
            KeyDictionary.FromJson("{\"keyLength\":8,\"keys\":[[32,32,102,102]]}").Type);
        // A mixed list keeps both; recovery takes the keys of its own length from it.
        var mixed = KeyDictionary.FromText("Mixed", "20206666\nFFFFFFFFFFFF\n12345678");
        Assert.Equal(KeyDictionaryType.T55xx, mixed.Type);
        Assert.Equal(2, mixed.KeysOfLength(4).Count);
        Assert.Single(mixed.KeysOfLength(6));

        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.AddRange([legacy, password]);
        var reloaded = new DictionaryLibrary(store);
        Assert.Single(reloaded.AllKeys());
        Assert.Equal(2, reloaded.AllKeys(KeyDictionaryType.T55xx).Count);
        Assert.Equal(KeyDictionaryType.T55xx, reloaded.Dictionaries[1].Type);
    }

    [Fact]
    public void EditorKeepsFourByteKeysAsPasswords()
    {
        var editor = new DictionaryEditorViewModel(new KeyDictionary { Name = "Test", Type = KeyDictionaryType.T55xx });
        Assert.Equal(KeyDictionaryType.T55xx, editor.Build().Type);
        var filled = new DictionaryEditorViewModel(KeyDictionary.FromText("Passwords", "20206666"));
        Assert.Equal(4, filled.Keys.RowDataLength(0));
        Assert.Equal(KeyDictionaryType.T55xx, filled.Build().Type);
        Assert.Equal("20206666", Convert.ToHexString(Assert.Single(filled.Build().Keys)));
    }

    [Fact]
    public void ToolAndTryKeysOfferOnlyTheirDictionaryTypeAndKeepSelection()
    {
        var original = AppSettings.Get("dumpUncheckedDictionaries");
        try
        {
            var store = new MemoryKeyValueStore();
            var library = new DictionaryLibrary(store);
            var classic = KeyDictionary.FromText("Classic", "FFFFFFFFFFFF");
            var passwords = KeyDictionary.FromText("Passwords", "20206666\n12345678");
            library.AddRange([classic, passwords]);
            var tools = new ToolsViewModel(new DeviceService([]), library);
            var cards = new CardsViewModel(new DeviceService([]), new CardLibrary(store), library);
            Assert.DoesNotContain(tools.T55xxKeySources, s => s.Id == classic.Id);
            Assert.DoesNotContain(cards.DumpKeySources, s => s.Id == passwords.Id);
            Assert.Contains(cards.DumpKeySources, s => s.Id == classic.Id);
            var selected = tools.T55xxKeySources.Single(s => s.Id == passwords.Id);
            selected.IsSelected = false;
            Assert.False(new ToolsViewModel(new DeviceService([]), library)
                .T55xxKeySources.Single(s => s.Id == passwords.Id).IsSelected);
            // A bad key is only flagged once the user chose to set one.
            tools.T55xxSettings.NewKey = "bad";
            Assert.Null(tools.T55xxSettings.NewKeyError);
            tools.T55xxSettings.SetNewKey = true;
            Assert.NotNull(tools.T55xxSettings.NewKeyError);
            tools.T55xxSettings.NewKey = "";
            Assert.Null(tools.T55xxSettings.NewKeyError);
            tools.T55xxSettings.NewKey = "AA BB CC DD";
            Assert.Null(tools.T55xxSettings.NewKeyError);
        }
        finally { AppSettings.Set("dumpUncheckedDictionaries", original); }
    }

    [Fact]
    public void PasswordModesChooseOnlyTheActiveKeys()
    {
        var settings = new T55xxPasswordSettingsViewModel(new DictionaryLibrary(new MemoryKeyValueStore()));
        settings.KnownKey = "12 34 56 78";
        settings.KnowKey = true;
        Assert.True(settings.ReadyToStart);
        Assert.Equal("12345678", Convert.ToHexString(Assert.Single(settings.BuildOldKeys())));

        var selected = settings.Keys.Sources.Select(source => source.IsSelected).ToArray();
        try
        {
            foreach (var source in settings.Keys.Sources) source.IsSelected = false;
            Assert.True(settings.ReadyToStart);
            settings.UseDictionary = true;
            Assert.False(settings.ReadyToStart);
            Assert.Empty(settings.BuildOldKeys());
        }
        finally
        {
            for (var i = 0; i < selected.Length; i++) settings.Keys.Sources[i].IsSelected = selected[i];
        }

        settings.NewKey = "AA BB CC DD";
        settings.SetNewKey = true;
        Assert.Equal("AABBCCDD", Convert.ToHexString(settings.BuildNewKey()));
        settings.SetNewKey = false;
        Assert.Equal("20206666", Convert.ToHexString(settings.BuildNewKey()));
        settings.NewKey = "bad";
        Assert.Null(settings.NewKeyError);
        settings.SetNewKey = true;
        Assert.NotNull(settings.NewKeyError);
    }

    [Fact]
    public void LfWriterValidatesOnlyTheSelectedChipAndPasswordMode()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        var card = new CardSave { Name = "LF", Tag = TagSpecificType.Em410x, Uid = [1, 2, 3, 4, 5] };
        library.Add(card);
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        vm.OpenCard(card.Id);
        typeof(CardsViewModel).GetProperty(nameof(CardsViewModel.IsConnected))!.SetValue(vm, true);
        vm.WriteCommand.Execute(null);
        Assert.True(vm.IsT55xxWrite);

        var settings = vm.WriteT55xxSettings;
        settings.KnowKey = true;
        settings.KnownKey = "12345678";
        Assert.True(vm.WriteNowCommand.CanExecute(null));
        settings.KnownKey = "bad";
        Assert.False(vm.WriteNowCommand.CanExecute(null));
        settings.KnownKey = "";
        Assert.False(vm.WriteNowCommand.CanExecute(null));
        settings.KnownKey = "12345678";
        settings.NewKey = "bad";
        settings.SetNewKey = true;
        Assert.False(vm.WriteNowCommand.CanExecute(null));
        settings.SetNewKey = false;
        Assert.True(vm.WriteNowCommand.CanExecute(null));

        settings.KnownKey = "bad";
        settings.SetNewKey = true;
        vm.WriteToEm4305 = true;
        Assert.True(vm.WriteNowCommand.CanExecute(null));
        vm.WriteLfPassword = "bad";
        Assert.False(vm.WriteNowCommand.CanExecute(null));
        vm.WriteToT55xx = true;
        settings.KnownKey = "12345678";
        settings.SetNewKey = false;
        Assert.True(vm.WriteNowCommand.CanExecute(null));
    }

    private sealed class PasswordTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        private readonly uint[] _blocks = new uint[8];
        private byte[]? _heldReply;
        private int _protectedWrites;
        private byte[] _vikingId = Hex.Parse("10203040");
        public TagSpecificType CurrentType { get; private set; } = TagSpecificType.Em410x64;
        public TagSpecificType InitialType { init => CurrentType = value; }
        public static byte[] OriginalId => Hex.Parse("0102030405");
        public PasswordTransport() => SetId(OriginalId);
        public List<ChameleonFrame> Requests { get; } = [];
        public List<ChameleonFrame> Attempts { get; } = [];
        public List<byte[]> ScannedIds { get; } = [];
        public byte[] Password { get; private set; } = Hex.Parse("12345678");
        public bool Protected { get; set; } = true;
        public bool Blank { init { if (value) Array.Clear(_blocks); } }
        public bool Missing { get; init; }
        public int MissProbeReads { get; set; }
        public bool RejectFinalWrites { get; init; }
        public bool FailSecondProtectedBlock { get; init; }
        public DeviceMode Mode { get; private set; } = DeviceMode.Tag;
        public bool PauseFirstProtectedReply { get; init; }
        public TaskCompletionSource FirstProtectedAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "T55xx test";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var r in _parser.Push(data))
            {
                Requests.Add(r);
                byte[] payload = [];
                var status = Status.Success;
                switch (r.Command)
                {
                    case Command.GetDeviceMode: payload = [(byte)Mode]; break;
                    case Command.ChangeDeviceMode: Mode = (DeviceMode)r.Data[0]; break;
                    case Command.Em410xScan:
                        var id = CurrentType.IsEm410x() ? ReadId() : null;
                        if (Missing || id is null || (!id.SequenceEqual(OriginalId) && MissProbeReads-- > 0))
                            status = Status.LfTagNoFound;
                        else
                        {
                            status = Status.LfTagOk;
                            var type = (ushort)TagSpecificType.Em410x64;
                            payload = [(byte)(type >> 8), (byte)type, ..id];
                            ScannedIds.Add(id);
                        }
                        break;
                    case Command.HidProxScan:
                    case Command.IoProxScan:
                    case Command.PacScan:
                    case Command.JablotronScan:
                        status = Status.LfTagNoFound; break;
                    case Command.VikingScan:
                        status = !Missing && CurrentType == TagSpecificType.Viking ? Status.LfTagOk : Status.LfTagNoFound;
                        if (status == Status.LfTagOk) payload = (byte[])_vikingId.Clone();
                        break;
                    case Command.LfT55xxWrite:
                        Assert.Equal(DeviceMode.Reader, Mode);
                        Attempts.Add(r);
                        if (r.Data[5] == 1) _protectedWrites++;
                        status = FailSecondProtectedBlock && _protectedWrites == 2 ? Status.ParErr : Status.LfTagOk;
                        if (!Missing && status == Status.LfTagOk && Protected == (r.Data[5] == 1)
                            && (!Protected || r.Data[6..10].SequenceEqual(Password)))
                        {
                            _blocks[r.Data[0]] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(r.Data.AsSpan(1));
                            if (r.Data[0] == 0)
                            {
                                Protected = (_blocks[0] & 0x10) != 0;
                                CurrentType = TagSpecificType.Em410x64;
                            }
                        }
                        break;
                    case Command.Em410xWriteToT55xx:
                    case Command.VikingWriteToT55xx:
                        // Matches app_cmd.c: one old-key slot is mandatory even on blank tags.
                        var idLength = r.Command == Command.VikingWriteToT55xx ? 4 : 5;
                        Assert.True(r.Data.Length >= idLength + 8 && (r.Data.Length - idLength - 4) % 4 == 0);
                        status = Status.LfTagOk;
                        var newKey = r.Data[idLength..(idLength + 4)];
                        var candidates = r.Data[(idLength + 4)..].Chunk(4);
                        if (!Missing && !RejectFinalWrites && (!Protected || newKey.SequenceEqual(Password)
                            || candidates.Any(k => k.SequenceEqual(Password))))
                        {
                            Password = newKey;
                            Protected = true;
                            if (r.Command == Command.VikingWriteToT55xx)
                            {
                                _vikingId = r.Data[..4];
                                CurrentType = TagSpecificType.Viking;
                            }
                            else
                            {
                                SetId(r.Data[..5]);
                                CurrentType = TagSpecificType.Em410x64;
                            }
                        }
                        break;
                    default: throw new InvalidOperationException($"Unexpected command: {r.Command}");
                }
                var reply = FrameCodec.Encode(r.Command, payload, (ushort)status);
                if (r.Command == Command.LfT55xxWrite && r.Data[5] == 1 && _protectedWrites == 1)
                {
                    if (PauseFirstProtectedReply) _heldReply = reply;
                    FirstProtectedAttempt.TrySetResult();
                    if (PauseFirstProtectedReply) continue;
                }
                _replies.Writer.TryWrite(reply);
            }
            return Task.CompletedTask;
        }
        // An independent bit-level decoder verifies headers, row/column parity and stop bit.
        public byte[]? ReadId()
        {
            if (CurrentType == TagSpecificType.Viking) return (byte[])_vikingId.Clone();
            var raw = ((ulong)_blocks[1] << 32) | _blocks[2];
            if ((raw >> 55) != 0x1FF || (raw & 1) != 0) return null;
            byte[] id = new byte[5];
            uint columns = 0;
            for (var row = 0; row < 10; row++)
            {
                uint bits = (uint)(raw >> (50 - row * 5)) & 31;
                if (System.Numerics.BitOperations.PopCount(bits) % 2 != 0) return null;
                var nibble = bits >> 1;
                columns ^= nibble;
                id[row / 2] |= (byte)(nibble << (row % 2 == 0 ? 4 : 0));
            }
            return columns == ((raw >> 1) & 15) ? id : null;
        }
        private void SetId(byte[] id)
        {
            var bits = Enumerable.Repeat(true, 9).ToList();
            bool[] parity = new bool[4];
            for (var row = 0; row < 10; row++)
            {
                var rowParity = false;
                for (var column = 0; column < 4; column++)
                {
                    var bit = (id[row / 2] & (1 << (7 - column - (row % 2) * 4))) != 0;
                    bits.Add(bit);
                    parity[column] ^= bit;
                    rowParity ^= bit;
                }
                bits.Add(rowParity);
            }
            bits.AddRange(parity);
            bits.Add(false);
            _blocks[1] = bits.Take(32).Aggregate(0u, (word, bit) => (word << 1) | (bit ? 1u : 0u));
            _blocks[2] = bits.Skip(32).Aggregate(0u, (word, bit) => (word << 1) | (bit ? 1u : 0u));
        }
        public void ReleaseReply() => _replies.Writer.TryWrite(_heldReply!);
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
