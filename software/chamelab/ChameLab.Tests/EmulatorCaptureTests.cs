using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class EmulatorCaptureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackgroundRefreshDoesNotShowWindowActivityOrBlockSavingExistingKeys(bool isMifare)
    {
        var transport = new CaptureTransport { Count = 2 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var (page, editor) = CreatePollingPage(client, isMifare);
        var capture = isMifare ? editor.MfcCapture : editor.UlCapture;
        capture.CollectionEnabled = true;
        await capture.RefreshAsync();
        Assert.True(capture.HasKeys);
        var keys = capture.KeysText;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = client.WithSlotLockAsync(async () => { entered.SetResult(); await release.Task; });
        await entered.Task;
        using var stop = new CancellationTokenSource();
        var refresh = capture.RefreshAsync(stop.Token);
        try
        {
            Assert.True(capture.IsRefreshing);
            Assert.False(capture.IsBusy);
            Assert.False(page.HasActivity);
            Assert.False(capture.RefreshCommand.CanExecute(null));
            Assert.True(capture.HasKeys);
            Assert.Equal(keys, capture.KeysText);
            Assert.True(capture.SaveToNewDictionaryCommand.CanExecute(null));
            Assert.True(capture.SaveToFileCommand.CanExecute(null));
            capture.SaveToNewDictionaryCommand.Execute(null);
            capture.RebuildKeysTargets();
            var saved = Assert.Single(capture.KeysTargets).Item.Dictionary;
            Assert.Equal(keys, string.Concat(saved.Keys.Select(k => Convert.ToHexString(k) + "\n")));
        }
        finally
        {
            stop.Cancel();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await held;
        }
        Assert.False(capture.IsRefreshing);
        Assert.False(page.HasActivity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackgroundRefreshRequiresAnActiveConnectedPageAndSavedCollection(bool isMifare)
    {
        var transport = new CaptureTransport { Count = 1 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var (page, editor) = CreatePollingPage(new ChameleonClient(connection), isMifare);
        var capture = isMifare ? editor.MfcCapture : editor.UlCapture;
        try
        {
            await page.RefreshCaptureAsync();
            Assert.Empty(transport.Requests);
            page.StartCaptureRefresh();
            if (isMifare) editor.MfcDetection = true; else editor.UlDetection = true;
            await page.RefreshCaptureAsync();
            Assert.Empty(transport.Requests);
            capture.CollectionEnabled = true;
            await page.RefreshCaptureAsync();
            Assert.Equal(1, capture.Count);
            Assert.Equal(2, transport.Requests.First(r => r.Command == Command.SetActiveSlot).Data[0]);
            var count = transport.Requests.Count;

            var busy = typeof(PageViewModelBase).GetProperty(nameof(PageViewModelBase.IsBusy))!;
            busy.SetValue(editor, true);
            await page.RefreshCaptureAsync();
            busy.SetValue(editor, false);
            typeof(SlotsViewModel).GetProperty(nameof(SlotsViewModel.IsConnected))!.SetValue(page, false);
            await page.RefreshCaptureAsync();
            typeof(SlotsViewModel).GetProperty(nameof(SlotsViewModel.IsConnected))!.SetValue(page, true);
            capture.CollectionEnabled = false;
            await page.RefreshCaptureAsync();
            capture.CollectionEnabled = true;
            page.StopCaptureRefresh();
            await page.RefreshCaptureAsync();
            Assert.Equal(count, transport.Requests.Count);
        }
        finally { page.StopCaptureRefresh(); }
    }

    [Fact]
    public async Task BackgroundRefreshSchedulesThePauseAfterCompletionAndCancelsWhenSlotCloses()
    {
        var transport = new CaptureTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var (page, editor) = CreatePollingPage(client, true);
        editor.MfcCapture.CollectionEnabled = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = client.WithSlotLockAsync(async () => { entered.SetResult(); await release.Task; });
        await entered.Task;
        try
        {
            page.StartCaptureRefresh();
            var timer = (DispatcherTimer)typeof(SlotsViewModel).GetField("_captureTimer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;
            var tick = page.TickCaptureRefreshAsync();
            Assert.False(timer.IsEnabled);
            await page.RefreshCaptureAsync();
            Assert.Empty(transport.Requests);
            release.SetResult();
            await held;
            await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(timer.IsEnabled);
            Assert.Equal(TimeSpan.FromSeconds(1), timer.Interval);

            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            held = client.WithSlotLockAsync(async () => { entered.SetResult(); await release.Task; });
            await entered.Task;
            var count = transport.Requests.Count;
            tick = page.TickCaptureRefreshAsync();
            typeof(SlotsViewModel).GetProperty(nameof(SlotsViewModel.Editor))!.SetValue(page, null);
            await tick.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(timer.IsEnabled);
            Assert.Equal(count, transport.Requests.Count);
        }
        finally
        {
            release.TrySetResult();
            await held;
            page.StopCaptureRefresh();
        }
    }

    [Fact]
    public async Task RepeatedBackgroundReadsReuseKeysUntilNonceContentsChange()
    {
        var transport = new CaptureTransport { Count = 2 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var engine = new RecordingEngine();
        var capture = new EmulatorCaptureViewModel(() => client, 1, true,
            new DictionaryLibrary(new MemoryKeyValueStore()), engine);
        await capture.RefreshAsync();
        var rows = capture.KeyRows;
        await capture.RefreshAsync();
        Assert.Single(engine.Requests);
        Assert.Same(rows, capture.KeyRows);
        transport.NonceBlock = 7;
        await capture.RefreshAsync();
        Assert.Equal(2, engine.Requests.Count);
        capture.Clear();
        await capture.RefreshAsync();
        Assert.Equal(3, engine.Requests.Count);
    }

    private static (SlotsViewModel Page, SlotEditorViewModel Editor) CreatePollingPage(ChameleonClient client, bool isMifare)
    {
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, client);
        typeof(DeviceService).GetProperty(nameof(DeviceService.State))!.SetValue(device, ConnectionState.Connected);
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        var dictionaries = new DictionaryLibrary(store);
        var page = new SlotsViewModel(device, cards, dictionaries);
        var editor = new SlotEditorViewModel(device, cards,
            new SlotInfo { Index = 3, HfType = isMifare ? TagSpecificType.Mifare1024 : TagSpecificType.Ntag213 },
            () => { }, _ => { }, dictionaries);
        typeof(SlotsViewModel).GetProperty(nameof(SlotsViewModel.Editor))!.SetValue(page, editor);
        return (page, editor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshAvailabilityChangesOnlyAfterSuccessfulSave(bool isMifare)
    {
        var transport = new CaptureTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));
        var store = new MemoryKeyValueStore();
        var editor = new SlotEditorViewModel(device, new CardLibrary(store),
            new SlotInfo { Index = 1, HfType = isMifare ? TagSpecificType.Mifare1024 : TagSpecificType.Ntag213 },
            () => { }, _ => { }, new DictionaryLibrary(store));
        var capture = isMifare ? editor.MfcCapture : editor.UlCapture;
        void Toggle(bool enabled)
        {
            if (isMifare) editor.MfcDetection = enabled; else editor.UlDetection = enabled;
        }

        Assert.False(capture.RefreshCommand.CanExecute(null));
        Toggle(true);
        Assert.False(capture.RefreshCommand.CanExecute(null));
        transport.SaveSucceeds = false;
        Assert.False(await editor.SaveAsync());
        Assert.False(capture.RefreshCommand.CanExecute(null));
        transport.SaveSucceeds = true;
        Assert.True(await editor.SaveAsync());
        Assert.True(capture.RefreshCommand.CanExecute(null));

        var busyProperty = typeof(PageViewModelBase).GetProperty(nameof(PageViewModelBase.IsBusy))!;
        busyProperty.SetValue(capture, true);
        Assert.False(capture.RefreshCommand.CanExecute(null));
        busyProperty.SetValue(capture, false);
        Assert.True(capture.RefreshCommand.CanExecute(null));

        Toggle(false);
        Assert.True(capture.RefreshCommand.CanExecute(null));
        transport.SaveSucceeds = false;
        Assert.False(await editor.SaveAsync());
        Assert.True(capture.RefreshCommand.CanExecute(null));
        transport.SaveSucceeds = true;
        Assert.True(await editor.SaveAsync());
        Assert.False(capture.RefreshCommand.CanExecute(null));
        Toggle(true);
        Assert.False(capture.RefreshCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task OpeningSlotUsesSavedCollectionFlagForRefreshAvailability(bool isMifare, bool enabled)
    {
        var transport = new CaptureTransport { DetectionEnabled = enabled };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));
        var store = new MemoryKeyValueStore();
        var editor = new SlotEditorViewModel(device, new CardLibrary(store),
            new SlotInfo { Index = 1, HfType = isMifare ? TagSpecificType.Mifare1024 : TagSpecificType.Ntag213 },
            () => { }, _ => { }, new DictionaryLibrary(store));
        await editor.OnActivatedAsync();
        Assert.Equal(enabled, (isMifare ? editor.MfcCapture : editor.UlCapture).RefreshCommand.CanExecute(null));
        Assert.False((isMifare ? editor.UlCapture : editor.MfcCapture).RefreshCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task SavingWithCollectionDisabledClearsUiOnlyAfterSuccessfulSave(bool isMifare, bool succeeds)
    {
        var transport = new CaptureTransport { Count = 2, SaveSucceeds = succeeds };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));
        var store = new MemoryKeyValueStore();
        var editor = new SlotEditorViewModel(device, new CardLibrary(store),
            new SlotInfo { Index = 1, HfType = isMifare ? TagSpecificType.Mifare1024 : TagSpecificType.Ntag213 },
            () => { }, _ => { }, new DictionaryLibrary(store));
        var capture = isMifare ? editor.MfcCapture : editor.UlCapture;
        await capture.RefreshAsync();
        Assert.True(capture.HasKeys);

        Assert.Equal(succeeds, await editor.SaveAsync());
        Assert.Equal(!succeeds, capture.HasKeys);
        Assert.Equal(succeeds ? 0 : 2, capture.Count);
        if (succeeds)
        {
            Assert.Empty(capture.KeyRows);
            Assert.Null(capture.KeysText);
            Assert.Null(capture.Results);
            Assert.Null(capture.ResultNote);
        }
    }

    [Fact]
    public async Task ClearingCollectionDiscardsResultsFromRecoveryAlreadyInProgress()
    {
        var transport = new CaptureTransport { Count = 2 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var engine = new DelayedEngine();
        var dictionaries = new DictionaryLibrary(new MemoryKeyValueStore());
        var capture = new EmulatorCaptureViewModel(() => client, 1, true,
            dictionaries, engine);
        var refresh = capture.RefreshAsync();
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capture.Clear();
        engine.Result.SetResult([0x112233445566]);
        await refresh;
        Assert.Equal(0, capture.Count);
        Assert.False(capture.HasKeys);
        Assert.Empty(capture.KeyRows);
        Assert.Null(capture.KeysText);
        Assert.Equal(0, dictionaries.KnownKeys.CardCount);
    }

    [Fact]
    public async Task PasswordRefreshReadsAllBatchesDeduplicatesAndSavesWithoutClearingCollection()
    {
        var transport = new CaptureTransport { Count = 3 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var store = new MemoryKeyValueStore();
        var dictionaries = new DictionaryLibrary(store);
        var existing = new KeyDictionary { Name = "Passwords", Keys = [Convert.FromHexString("01020304")] };
        dictionaries.Add(existing);
        var vm = new EmulatorCaptureViewModel(() => client, 3, false, dictionaries);
        Assert.False(vm.HasKeys);
        await vm.RefreshAsync();
        Assert.Equal(3, vm.Count);
        Assert.Equal("01020304\nAABBCCDD\n", vm.KeysText);
        Assert.Contains("AABBCCDD", vm.Results);
        Assert.True(vm.HasKeys);
        Assert.Equal(new uint[] { 0, 2 }, transport.Requests.Where(r => r.Command == Command.Mf0NtagGetDetectionLog)
            .Select(r => BinaryPrimitives.ReadUInt32BigEndian(r.Data)));
        Assert.Equal(2, transport.Requests.First(r => r.Command == Command.SetActiveSlot).Data[0]);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf0NtagSetDetectionEnable);

        vm.RebuildKeysTargets();
        vm.KeysTargets.Single().SaveCommand.Execute(null);
        vm.KeysTargets.Single().SaveCommand.Execute(null);
        Assert.Equal(2, existing.Keys.Count);
        vm.SaveToNewDictionaryCommand.Execute(null);
        var saved = new DictionaryLibrary(store);
        Assert.Equal(2, saved.Dictionaries.Count);
        Assert.All(saved.Dictionaries, d => Assert.Equal(2, d.Keys.Count));

        transport.Count = 0;
        await vm.RefreshAsync();
        Assert.False(vm.HasKeys);
        Assert.Null(vm.KeysText);
    }

    [Theory]
    [InlineData(16, " 4A")]
    [InlineData(160, "34A")]
    public async Task NonceRefreshAutomaticallyRecoversARealReferenceKeyWhenEnoughNoncesExist(byte block, string sectorKey)
    {
        var transport = new CaptureTransport { Count = 1, NonceBlock = block };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var store = new MemoryKeyValueStore();
        var dictionaries = new DictionaryLibrary(store);
        var vm = new EmulatorCaptureViewModel(() => client, 2, true,
            dictionaries, new ManagedKeyRecoveryEngine());
        Assert.False(vm.HasKeys);
        await vm.RefreshAsync();
        Assert.Equal(1, vm.Count);
        Assert.False(vm.HasKeys);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1GetDetectionLog);
        Assert.Equal(0, dictionaries.KnownKeys.CardCount);
        transport.Count = 2;
        await vm.RefreshAsync();
        Assert.True(vm.HasKeys);
        Assert.Equal("000000000000\n", vm.KeysText);
        var row = Assert.Single(vm.KeyRows);
        Assert.Equal($"12345678 {sectorKey} ", row.Prefix);
        Assert.Equal("000000000000", row.Key);
        Assert.Equal($"12345678 {sectorKey} 000000000000", vm.Results);
        var uid = Convert.FromHexString("12345678");
        var known = new DictionaryLibrary(store).KnownKeys;
        Assert.Equal(new byte[6], Assert.Single(known.ForUid(uid)));
        var position = Assert.Single(known.PositionsForUid(uid));
        Assert.Equal(MifareClassic.SectorOfBlock(block), position.Sector);
        Assert.Equal(MfKeyType.A, position.KeyType);
        Assert.Equal(new byte[6], position.Key);
        Assert.Empty(known.ForUid(Convert.FromHexString("87654321")));
        Assert.Empty(dictionaries.Dictionaries);
        Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1SetDetectionEnable);
        transport.Count = 0;
        await vm.RefreshAsync();
        Assert.False(vm.HasKeys);
        Assert.Equal(new byte[6], Assert.Single(dictionaries.KnownKeys.ForUid(uid)));
    }

    [Fact]
    public void CaptureUserOperationActivityReachesTheWindowWithoutMakingTheWholeSlotsPageBusy()
    {
        var store = new MemoryKeyValueStore();
        var device = new DeviceService([]);
        var cards = new CardLibrary(store);
        var dictionaries = new DictionaryLibrary(store);
        var page = new SlotsViewModel(device, cards, dictionaries);
        var editor = new SlotEditorViewModel(device, cards,
            new SlotInfo { Index = 1, HfType = TagSpecificType.Mifare1024 }, () => { }, _ => { }, dictionaries);
        var editorProperty = typeof(SlotsViewModel).GetProperty(nameof(SlotsViewModel.Editor))!;
        var busyProperty = typeof(PageViewModelBase).GetProperty(nameof(PageViewModelBase.IsBusy))!;
        editorProperty.SetValue(page, editor);
        var notifications = 0;
        page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(page.HasActivity)) notifications++; };

        foreach (var capture in new[] { editor.MfcCapture, editor.UlCapture })
        {
            busyProperty.SetValue(capture, true);
            Assert.True(page.HasActivity);
            Assert.False(page.IsBusy);
            busyProperty.SetValue(capture, false);
            Assert.False(page.HasActivity);
        }
        Assert.Equal(4, notifications);
        busyProperty.SetValue(editor.MfcCapture, true);
        editorProperty.SetValue(page, null);
        Assert.False(page.HasActivity);
        var detachedNotifications = notifications;
        busyProperty.SetValue(editor.MfcCapture, false);
        Assert.Equal(detachedNotifications, notifications);
    }

    [Fact]
    public async Task Mfkey32DoesNotPairDifferentCardsBlocksKeyTypesOrNestedAndDuplicateRecords()
    {
        DetectionLog Log(string nt) => new(3, MfKeyType.A, false, [1, 2, 3, 4],
            Convert.FromHexString(nt), [5, 6, 7, 8], [9, 10, 11, 12]);
        var a = Log("01020304");
        var b = Log("05060708");
        var engine = new RecordingEngine();
        var found = await EmulatorCaptureViewModel.RecoverKeysAsync(
            [a, a, b, b with { Uid = [4, 3, 2, 1] }, b with { Block = 7 },
                b with { KeyType = MfKeyType.B }, a with { IsNested = true }, b with { IsNested = true }], engine);
        Assert.Single(engine.Requests);
        Assert.Single(found);
        Assert.Equal(0xAABBCCDDEEFFUL, found[0].Key);
        Assert.Equal(new[] { a, b }, engine.Requests[0].Logs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("010203")]
    [InlineData("0102030405")]
    public async Task IncompletePasswordBatchFailsWithoutLoopingOrReturningPartialKeys(string batch)
    {
        var transport = new CaptureTransport { Count = 3, PasswordBatch = Convert.FromHexString(batch) };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ChameleonClient(connection).GetAllMf0DetectionPasswordsAsync());
        Assert.Single(transport.Requests, r => r.Command == Command.Mf0NtagGetDetectionLog);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavingWriteModePreservesExistingCollectionWhenDetectionIsUnchanged(bool isMifare)
    {
        var transport = new CaptureTransport { Count = 2 };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var settings = isMifare
            ? new SlotEmulatorSettings(new Mf1EmulatorSettings(false, false, false, true, EmulatorWriteMode.Denied, null, null), null)
            : new SlotEmulatorSettings(null, new Mf0EmulatorSettings(false, EmulatorWriteMode.Denied, true));
        await new ChameleonClient(connection).ApplyEmulatorSettingsAsync(settings);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1SetDetectionEnable or Command.Mf0NtagSetDetectionEnable);
        Assert.Contains(transport.Requests, r => r.Command == (isMifare ? Command.Mf1SetWriteMode : Command.Mf0NtagSetWriteMode));
    }

    private sealed class RecordingEngine : IKeyRecoveryEngine
    {
        public List<KeyRecoveryRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<ulong>>([0xAABBCCDDEEFF]);
        }
    }

    private sealed class DelayedEngine : IKeyRecoveryEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<ulong>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
        {
            Started.SetResult();
            return Result.Task;
        }
    }

    private sealed class CaptureTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public int Count { get; set; }
        public byte NonceBlock { get; set; } = 3;
        public bool SaveSucceeds { get; set; } = true;
        public bool DetectionEnabled { get; init; } = true;
        public byte[]? PasswordBatch { get; init; }
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Capture test";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                byte[] payload = request.Command switch
                {
                    Command.Hf14aGetAntiCollData => [4, 0x12, 0x34, 0x56, 0x78, 4, 0, 8, 0],
                    Command.Mf1GetEmulatorConfig => [(byte)(DetectionEnabled ? 1 : 0), 0, 0, 0, 0],
                    Command.Mf0NtagGetDetectionEnable => [(byte)(DetectionEnabled ? 1 : 0)],
                    Command.Mf0NtagGetDetectionLog => PasswordBatch ??
                        (BinaryPrimitives.ReadUInt32BigEndian(request.Data) == 0
                            ? Convert.FromHexString("01020304AABBCCDD") : Convert.FromHexString("01020304")),
                    Command.Mf1GetDetectionLog => Convert.FromHexString(
                        "03001234567887654321E6B99E333748C47B030012345678876543215C62D73055F2311D"),
                    _ => [],
                };
                if (request.Command is Command.Mf1GetDetectionCount or Command.Mf0NtagGetDetectionCount)
                {
                    payload = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)Count);
                }
                if (request.Command == Command.Mf1GetDetectionLog) payload[0] = payload[18] = NonceBlock;
                var status = request.Command == Command.SlotDataConfigSave && !SaveSucceeds ? Status.ParErr : Status.Success;
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
