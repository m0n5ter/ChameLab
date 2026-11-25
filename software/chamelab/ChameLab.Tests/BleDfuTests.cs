using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Mobile;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ChameLab.Tests;

public class BleDfuTests
{
    [Theory]
    // Apple / iOS: Plugin.BLE RequestMtuAsync returns GetMaximumWriteValueLength(WithoutResponse),
    // which is already the payload length (ATT MTU - 3) and must NOT have 3 subtracted again.
    [InlineData(244, true, 244)]
    [InlineData(180, true, 180)]
    [InlineData(182, true, 180)]
    [InlineData(512, true, 244)]
    [InlineData(20, true, 20)]
    [InlineData(0, true, 20)]
    [InlineData(-1, true, 20)]
    // Android: RequestMtuAsync returns the negotiated ATT MTU including the 3-byte ATT header.
    [InlineData(247, false, 244)]
    [InlineData(185, false, 180)]
    [InlineData(183, false, 180)]
    [InlineData(517, false, 244)]
    [InlineData(23, false, 20)]
    [InlineData(20, false, 20)]
    [InlineData(0, false, 20)]
    public void DataSizeForMtuIsAlwaysWordAlignedAndWithinBounds(int reportedMtu, bool returnsPayloadLength, int expected)
    {
        var actual = BleDfuChannel.DataSizeForMtu(reportedMtu, returnsPayloadLength);

        Assert.Equal(expected, actual);
        Assert.InRange(actual, 20, 244);
        Assert.Equal(0, actual % 4);
    }

    [Fact]
    public void IsBootloaderRecognizesNamesAndBothByteOrdersOfSecureDfuUuid()
    {
        var byUltraName = MakeDevice("CU-1A2B");
        var byLiteName = MakeDevice("CL-9F00");
        var byLittleEndianUuid = MakeDevice("", new AdvertisementRecord(AdvertisementRecordType.UuidsIncomplete16Bit, [0x59, 0xFE]));
        var byBigEndianUuidIos = MakeDevice("", new AdvertisementRecord(AdvertisementRecordType.UuidsComplete16Bit, [0xFE, 0x59]));
        var bySecondUuidInList = MakeDevice("", new AdvertisementRecord(AdvertisementRecordType.UuidsComplete16Bit, [0x18, 0x0A, 0xFE, 0x59]));
        var unrelated = MakeDevice("Chameleon Ultra", new AdvertisementRecord(AdvertisementRecordType.UuidsComplete16Bit, [0x0A, 0x18]));
        var nullDataRecord = MakeDevice("", new AdvertisementRecord(AdvertisementRecordType.UuidsComplete16Bit, null!));

        Assert.True(BleDfuLocator.IsBootloader(byUltraName));
        Assert.True(BleDfuLocator.IsBootloader(byLiteName));
        Assert.True(BleDfuLocator.IsBootloader(byLittleEndianUuid));
        Assert.True(BleDfuLocator.IsBootloader(byBigEndianUuidIos));
        Assert.True(BleDfuLocator.IsBootloader(bySecondUuidInList));
        Assert.False(BleDfuLocator.IsBootloader(unrelated));
        Assert.False(BleDfuLocator.IsBootloader(nullDataRecord));
    }

    [Theory]
    [InlineData(true, 244, 244)]
    [InlineData(true, 180, 180)]
    [InlineData(false, 247, 244)]
    [InlineData(false, 185, 180)]
    public async Task OpenAsyncQueriesMtuAtPlatformOrderAndStreamsWordAlignedFirmwareChunks(
        bool returnsPayloadLength, int reportedMtu, int expectedChunkSize)
    {
        var calls = new List<string>();
        var packetChunks = new List<byte[]>();

        var adapter = FakeProxy.Create<IAdapter>();
        var device = FakeProxy.Create<IDevice>();
        var service = FakeProxy.Create<IService>();
        var control = FakeProxy.Create<ICharacteristic>();
        var packet = FakeProxy.Create<ICharacteristic>();

        device.Values["Id"] = Guid.NewGuid();
        device.Values["Name"] = "CU-1234";

        // Simulate a minimal nRF5 SDK bootloader that enforces nrf_fstorage_write's 4-byte alignment rule.
        byte currentObj = 0;
        uint offset = 0;
        uint crc = 0;
        var prn = 0;
        var sincePrn = 0;
        const int maxObject = 4096;

        void Notify(byte[] payload)
        {
            control.Values["Value"] = payload;
            control.RaiseValueUpdated();
        }

        byte[] Ok(byte op, params byte[] extra) => [SecureDfu.OpResponse, op, 0x01, .. extra];

        control.OnMethod = (name, args) =>
        {
            if (name == "StartUpdatesAsync")
            {
                calls.Add("StartUpdates");
                return Task.CompletedTask;
            }
            if (name == "StopUpdatesAsync") return Task.CompletedTask;
            if (name == "WriteAsync")
            {
                var req = (byte[])args[0]!;
                switch (req[0])
                {
                    case SecureDfu.OpSetPrn:
                        prn = BitConverter.ToUInt16(req, 1);
                        sincePrn = 0;
                        Notify(Ok(SecureDfu.OpSetPrn));
                        break;
                    case SecureDfu.OpSelect:
                        currentObj = req[1];
                        Notify(Ok(SecureDfu.OpSelect, [.. BitConverter.GetBytes((uint)maxObject), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u)]));
                        break;
                    case SecureDfu.OpCreate:
                        currentObj = req[1];
                        sincePrn = 0;
                        if (currentObj == 0x01) { offset = 0; crc = 0; }
                        Notify(Ok(SecureDfu.OpCreate));
                        break;
                    case SecureDfu.OpCrc:
                        Notify(Ok(SecureDfu.OpCrc, [.. BitConverter.GetBytes(offset), .. BitConverter.GetBytes(crc)]));
                        break;
                    case SecureDfu.OpExecute:
                        if (currentObj == 0x01) { offset = 0; crc = 0; }
                        Notify(Ok(SecureDfu.OpExecute));
                        break;
                }
                return Task.FromResult(0);
            }
            return null;
        };

        packet.OnMethod = (name, args) =>
        {
            if (name == "WriteAsync")
            {
                var data = (byte[])args[0]!;
                if (currentObj == 0x02)
                {
                    packetChunks.Add(data);
                    // Mirror nrf_fstorage_write(): reject non-word-aligned lengths by dropping the write.
                    if (data.Length % 4 != 0)
                        return Task.FromResult(0);
                }
                crc = AppendCrc32(crc, data);
                offset += (uint)data.Length;
                if (prn > 0 && ++sincePrn == prn)
                {
                    sincePrn = 0;
                    Notify(Ok(SecureDfu.OpCrc, [.. BitConverter.GetBytes(offset), .. BitConverter.GetBytes(crc)]));
                }
                return Task.FromResult(0);
            }
            return null;
        };

        service.OnMethod = (name, args) =>
        {
            if (name == "GetCharacteristicAsync")
            {
                var id = (Guid)args[0]!;
                if (id == SecureDfuService.ControlPoint) return Task.FromResult(control.Object);
                if (id == SecureDfuService.Packet) return Task.FromResult(packet.Object);
            }
            return null;
        };

        device.OnMethod = (name, args) =>
        {
            if (name == "RequestMtuAsync")
            {
                calls.Add("RequestMtu");
                return Task.FromResult(reportedMtu);
            }
            if (name == "GetServiceAsync")
            {
                calls.Add("GetService");
                return Task.FromResult(service.Object);
            }
            return null;
        };

        adapter.OnMethod = (name, _) =>
        {
            if (name is "ConnectToDeviceAsync" or "DisconnectDeviceAsync")
                return Task.CompletedTask;
            return null;
        };

        await using var channel = await BleDfuChannel.OpenAsync(adapter.Object, device.Object, returnsPayloadLength, CancellationToken.None);

        Assert.Equal(expectedChunkSize, channel.MaxDataSize);
        if (returnsPayloadLength)
            Assert.True(calls.IndexOf("RequestMtu") > calls.IndexOf("StartUpdates"), "iOS should read MTU after GATT discovery/CCCD.");
        else
            Assert.True(calls.IndexOf("RequestMtu") < calls.IndexOf("GetService"), "Android should request MTU before service discovery.");

        var init = Enumerable.Range(0, 137).Select(i => (byte)(i & 0xFF)).ToArray();
        var firmware = Enumerable.Range(0, 5000).Select(i => (byte)((i * 31) & 0xFF)).ToArray(); // 4-byte aligned, > 1 object

        await new SecureDfu(channel).FlashAsync(new DfuPackage(init, firmware));

        Assert.NotEmpty(packetChunks);
        Assert.All(packetChunks, chunk => Assert.Equal(0, chunk.Length % 4));
        Assert.Equal(firmware.Length, packetChunks.Sum(c => c.Length));
    }

    [Fact]
    public async Task FirmwareUpdateServiceAcquiresAndReleasesWakeLockDuringInstall()
    {
        var acquired = 0;
        var released = 0;
        var wakeLock = new WakeLockService(() =>
        {
            acquired++;
            return Task.FromResult<IAsyncDisposable?>(WakeLockService.ReleaseWith(() =>
            {
                released++;
                return default;
            }));
        });

        var device = new DeviceService(Array.Empty<ITransportScanner>());
        var service = new FirmwareUpdateService(device, Array.Empty<IDfuLocator>(), wakeLock);
        var target = new FailingTarget(() =>
        {
            Assert.Equal(1, acquired);
            Assert.Equal(0, released);
        });

        var package = new DfuPackage([1, 2, 3, 4], [5, 6, 7, 8]);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.InstallAsync(package, target, new Progress<FirmwareUpdateProgress>(), cts.Token));

        Assert.Equal(1, acquired);
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData("disconnect_while_writing")]
    [InlineData("write_throws_before_disconnect")]
    [InlineData("write_hangs")]
    public async Task FinalExecuteLinkLossOrHangStillCompletesFlash(string failureMode)
    {
        var adapter = FakeProxy.Create<IAdapter>();
        var device = FakeProxy.Create<IDevice>();
        var service = FakeProxy.Create<IService>();
        var control = FakeProxy.Create<ICharacteristic>();
        var packet = FakeProxy.Create<ICharacteristic>();

        device.Values["Id"] = Guid.NewGuid();
        device.Values["Name"] = "CU-1234";
        device.Values["State"] = DeviceState.Connected;

        byte currentObj = 0;
        uint offset = 0;
        uint crc = 0;
        var stopUpdatesCalled = false;

        void Notify(byte[] payload)
        {
            control.Values["Value"] = payload;
            control.RaiseValueUpdated();
        }

        byte[] Ok(byte op, params byte[] extra) => [SecureDfu.OpResponse, op, 0x01, .. extra];

        control.OnMethod = (name, args) =>
        {
            if (name == "StartUpdatesAsync") return Task.CompletedTask;
            if (name == "StopUpdatesAsync")
            {
                stopUpdatesCalled = true;
                return Task.CompletedTask;
            }
            if (name == "WriteAsync")
            {
                var req = (byte[])args[0]!;
                switch (req[0])
                {
                    case SecureDfu.OpSetPrn:
                        Notify(Ok(SecureDfu.OpSetPrn));
                        break;
                    case SecureDfu.OpSelect:
                        currentObj = req[1];
                        Notify(Ok(SecureDfu.OpSelect, [.. BitConverter.GetBytes(4096u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u)]));
                        break;
                    case SecureDfu.OpCreate:
                        currentObj = req[1];
                        if (currentObj == 0x01) { offset = 0; crc = 0; }
                        Notify(Ok(SecureDfu.OpCreate));
                        break;
                    case SecureDfu.OpCrc:
                        Notify(Ok(SecureDfu.OpCrc, [.. BitConverter.GetBytes(offset), .. BitConverter.GetBytes(crc)]));
                        break;
                    case SecureDfu.OpExecute when currentObj == 0x01:
                        offset = 0;
                        crc = 0;
                        Notify(Ok(SecureDfu.OpExecute));
                        break;
                    case SecureDfu.OpExecute when currentObj == 0x02:
                        if (failureMode == "disconnect_while_writing")
                        {
                            var never = new TaskCompletionSource<int>();
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(20);
                                device.Values["State"] = DeviceState.Disconnected;
                                adapter.RaiseDeviceDisconnected(device.Object);
                            });
                            return never.Task;
                        }
                        if (failureMode == "write_throws_before_disconnect")
                        {
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(20);
                                device.Values["State"] = DeviceState.Disconnected;
                                adapter.RaiseDeviceDisconnected(device.Object);
                            });
                            return Task.FromException<int>(new InvalidOperationException("GATT write failed on disconnect"));
                        }
                        if (failureMode == "write_hangs")
                            return new TaskCompletionSource<int>().Task;
                        break;
                }
                return Task.FromResult(0);
            }
            return null;
        };

        packet.OnMethod = (name, args) =>
        {
            if (name == "WriteAsync")
            {
                var data = (byte[])args[0]!;
                crc = AppendCrc32(crc, data);
                offset += (uint)data.Length;
                return Task.FromResult(0);
            }
            return null;
        };

        service.OnMethod = (name, args) =>
        {
            if (name == "GetCharacteristicAsync")
            {
                var id = (Guid)args[0]!;
                if (id == SecureDfuService.ControlPoint) return Task.FromResult(control.Object);
                if (id == SecureDfuService.Packet) return Task.FromResult(packet.Object);
            }
            return null;
        };

        device.OnMethod = (name, _) =>
        {
            if (name == "RequestMtuAsync") return Task.FromResult(180);
            if (name == "GetServiceAsync") return Task.FromResult(service.Object);
            return null;
        };

        adapter.OnMethod = (name, _) =>
        {
            if (name is "ConnectToDeviceAsync" or "DisconnectDeviceAsync") return Task.CompletedTask;
            return null;
        };

        await using (var channel = await BleDfuChannel.OpenAsync(
            adapter.Object, device.Object, returnsPayloadLength: true, CancellationToken.None,
            responseTimeout: TimeSpan.FromMilliseconds(100),
            disconnectGrace: TimeSpan.FromMilliseconds(150)))
        {
            var package = new DfuPackage([1, 2, 3, 4], [5, 6, 7, 8, 9, 10, 11, 12]);
            await new SecureDfu(channel).FlashAsync(package);
        }

        if (failureMode != "write_hangs")
            Assert.False(stopUpdatesCalled, "Should skip StopUpdatesAsync once the bootloader has already disconnected.");
    }

    [Fact]
    public async Task InstallAsyncReturnsNotReconnectedWhenDevicePowersOffAfterFlash()
    {
        var scanner = new EmptyBleScanner();
        var device = new DeviceService([scanner]);
        // Simulate a previously connected BLE device (e.g. Chameleon on battery) that won't return without a button press.
        typeof(DeviceService).GetProperty(nameof(DeviceService.Candidate))!
            .SetValue(device, new TransportCandidate(TransportKind.Ble, Guid.NewGuid().ToString(), "Chameleon Ultra", "BLE"));

        var service = new FirmwareUpdateService(device, Array.Empty<IDfuLocator>())
        {
            ReconnectTimeout = TimeSpan.FromMilliseconds(80),
        };
        var target = new ScriptedDfuTarget();
        var steps = new List<FirmwareUpdateStep>();
        var progress = new SyncProgress<FirmwareUpdateProgress>(p => steps.Add(p.Step));

        var result = await service.InstallAsync(new DfuPackage([1, 2, 3, 4], [5, 6, 7, 8]), target, progress);

        Assert.Equal(FirmwareInstallResult.NotReconnected, result);
        Assert.Contains(FirmwareUpdateStep.Flashing, steps);
        Assert.Contains(FirmwareUpdateStep.Reconnecting, steps);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class EmptyBleScanner : ITransportScanner
    {
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "BLE";
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>(Array.Empty<TransportCandidate>());
        public ITransport Create(TransportCandidate candidate) => throw new NotSupportedException();
    }

    private sealed class ScriptedDfuTarget : IDfuTarget
    {
        public Task<IDfuChannel?> TryOpenAsync(CancellationToken ct) =>
            Task.FromResult<IDfuChannel?>(new ScriptedDfuChannel());
    }

    private sealed class ScriptedDfuChannel : IDfuChannel
    {
        private byte _obj;
        private uint _offset;
        private uint _crc;

        public int MaxDataSize => 20;
        public int PacketReceiptInterval => 0;

        public Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
        {
            var op = request[0];
            byte[] resp = op switch
            {
                SecureDfu.OpSetPrn => [SecureDfu.OpResponse, op, 0x01],
                SecureDfu.OpSelect => SetObj(request[1], [SecureDfu.OpResponse, op, 0x01, .. BitConverter.GetBytes(4096u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u)]),
                SecureDfu.OpCreate => CreateObj(request[1]),
                SecureDfu.OpCrc => [SecureDfu.OpResponse, op, 0x01, .. BitConverter.GetBytes(_offset), .. BitConverter.GetBytes(_crc)],
                SecureDfu.OpExecute => ExecuteObj(),
                _ => throw new NotSupportedException(),
            };
            return Task.FromResult(resp);
        }

        private byte[] SetObj(byte obj, byte[] resp) { _obj = obj; return resp; }

        private byte[] CreateObj(byte obj)
        {
            _obj = obj;
            if (obj == 0x01) { _offset = 0; _crc = 0; }
            return [SecureDfu.OpResponse, SecureDfu.OpCreate, 0x01];
        }

        private byte[] ExecuteObj()
        {
            if (_obj == 0x01) { _offset = 0; _crc = 0; }
            return [SecureDfu.OpResponse, SecureDfu.OpExecute, 0x01];
        }

        public Task<byte[]> ReceiveAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task WriteDataAsync(byte[] data, CancellationToken ct)
        {
            _crc = AppendCrc32(_crc, data);
            _offset += (uint)data.Length;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class FailingTarget(Action onTryOpen) : IDfuTarget
    {
        public Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            onTryOpen();
            throw new OperationCanceledException();
        }
    }

    private static IDevice MakeDevice(string name, params AdvertisementRecord[] records)
    {
        var device = FakeProxy.Create<IDevice>();
        device.Values["Id"] = Guid.NewGuid();
        device.Values["Name"] = name;
        device.Values["AdvertisementRecords"] = (IReadOnlyList<AdvertisementRecord>)records;
        return device.Object;
    }

    private static uint AppendCrc32(uint previous, byte[] data)
    {
        var crc = ~previous;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    public class FakeProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; } = new();
        public Func<string, object?[], object?>? OnMethod;
        private EventHandler<CharacteristicUpdatedEventArgs>? _valueUpdated;
        private EventHandler<DeviceEventArgs>? _disconnected;
        private EventHandler<DeviceErrorEventArgs>? _connectionLost;
        private object? _self;

        public void RaiseValueUpdated() =>
            _valueUpdated?.Invoke(_self, new CharacteristicUpdatedEventArgs((ICharacteristic)_self!));

        public void RaiseDeviceDisconnected(IDevice device) =>
            _disconnected?.Invoke(_self, new DeviceEventArgs { Device = device });

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method!.Name;
            args ??= Array.Empty<object?>();
            if (name == "add_ValueUpdated") { _valueUpdated += (EventHandler<CharacteristicUpdatedEventArgs>)args[0]!; return null; }
            if (name == "remove_ValueUpdated") { _valueUpdated -= (EventHandler<CharacteristicUpdatedEventArgs>)args[0]!; return null; }
            if (name == "add_DeviceDisconnected") { _disconnected += (EventHandler<DeviceEventArgs>)args[0]!; return null; }
            if (name == "remove_DeviceDisconnected") { _disconnected -= (EventHandler<DeviceEventArgs>)args[0]!; return null; }
            if (name == "add_DeviceConnectionLost") { _connectionLost += (EventHandler<DeviceErrorEventArgs>)args[0]!; return null; }
            if (name == "remove_DeviceConnectionLost") { _connectionLost -= (EventHandler<DeviceErrorEventArgs>)args[0]!; return null; }
            if (name.StartsWith("get_"))
                return Values.TryGetValue(name[4..], out var value) ? value :
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            if (name.StartsWith("set_")) { Values[name[4..]] = args[0]; return null; }
            if (OnMethod is not null && OnMethod(name, args) is { } result) return result;
            throw new NotSupportedException(name);
        }

        public static Handle<T> Create<T>() where T : class
        {
            var obj = DispatchProxy.Create<T, FakeProxy>();
            var proxy = (FakeProxy)(object)obj;
            proxy._self = obj;
            return new Handle<T>(obj, proxy);
        }
    }

    public record Handle<T>(T Object, FakeProxy Handler)
    {
        public Dictionary<string, object?> Values => Handler.Values;
        public Func<string, object?[], object?>? OnMethod { set => Handler.OnMethod = value; }
        public void RaiseValueUpdated() => Handler.RaiseValueUpdated();
        public void RaiseDeviceDisconnected(IDevice device) => Handler.RaiseDeviceDisconnected(device);
    }
}
