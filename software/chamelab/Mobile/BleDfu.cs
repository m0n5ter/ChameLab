using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Transport;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using ChameLab.Localization;

namespace ChameLab.Mobile;

/// <summary>The nRF5 SDK bootloader's Secure DFU service.</summary>
internal static class SecureDfuService
{
    public static readonly Guid Service = new("0000FE59-0000-1000-8000-00805F9B34FB");
    /// <summary>Requests (write with response) and their responses (notify).</summary>
    public static readonly Guid ControlPoint = new("8EC90001-F315-4F60-9FB8-838830DAEA50");
    /// <summary>Image data (write without response).</summary>
    public static readonly Guid Packet = new("8EC90002-F315-4F60-9FB8-838830DAEA50");
}

/// <summary>
/// Finds the Chameleon's bootloader over BLE. After <c>ENTER_BOOTLOADER</c> it advertises
/// the Secure DFU service as "CU-xxxx" / "CL-xxxx" from the application's address + 1, and
/// puts the application's address in its manufacturer data; either pins down the right
/// device where addresses are visible (Android).
/// </summary>
public sealed class BleDfuLocator : IDfuLocator
{
    private readonly IBluetoothLE _ble;

    public BleDfuLocator() : this(CrossBluetoothLE.Current) { }

    internal BleDfuLocator(IBluetoothLE ble) => _ble = ble;

    public TransportKind Kind => TransportKind.Ble;

    public IDfuTarget Prepare(TransportCandidate? device) =>
        new Target(_ble, device is not null && Guid.TryParse(device.Id, out var id) ? Address(id) : null);

    /// <summary>
    /// Plugin.BLE on Android builds ids as <c>00000000-0000-0000-0000-{MAC}</c>, most significant
    /// byte first. Elsewhere ids are opaque.
    /// </summary>
    private static byte[]? Address(Guid id)
    {
        var hex = id.ToString("N");
        return hex.StartsWith("00000000000000000000", StringComparison.Ordinal) ? Convert.FromHexString(hex[^12..]) : null;
    }

    internal static bool IsBootloader(IDevice d) =>
        d.Name is { } name && (name.StartsWith("CU-", StringComparison.Ordinal) || name.StartsWith("CL-", StringComparison.Ordinal)) ||
        Records(d).Any(r =>
            r.Type is AdvertisementRecordType.UuidsComplete16Bit or AdvertisementRecordType.UuidsIncomplete16Bit &&
            ListsSecureDfuUuid(r.Data));

    private static bool ListsSecureDfuUuid(byte[]? data)
    {
        if (data is null) return false;
        for (var i = 0; i + 2 <= data.Length; i += 2)
        {
            // Little-endian in raw advertising data (Android), big-endian from iOS CBUUIDs.
            if (data[i] == 0x59 && data[i + 1] == 0xFE || data[i] == 0xFE && data[i + 1] == 0x59)
                return true;
        }
        return false;
    }

    private static IEnumerable<AdvertisementRecord> Records(IDevice d) =>
        d.AdvertisementRecords ?? (IEnumerable<AdvertisementRecord>)Array.Empty<AdvertisementRecord>();

    private sealed class Target(IBluetoothLE ble, byte[]? app) : IDfuTarget
    {
        // One scan for the whole wait: Android ignores an app's scans after five starts in 30 seconds.
        private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(12);

        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            if (!ble.IsAvailable || !ble.IsOn) throw new InvalidOperationException(Strings.Ble_Off);
            var adapter = ble.Adapter;

            // The adapter runs one scan at a time (a Rescan on the Connect page, say).
            await BleScanner.WaitForScanAsync(ct);
            while (adapter.IsScanning) await Task.Delay(250, ct);

            var found = new Dictionary<Guid, IDevice>();
            IDevice? ours = null;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void OnDiscovered(object? sender, DeviceEventArgs e)
            {
                if (e.Device is not { } d || !IsBootloader(d)) return;
                found[d.Id] = d;
                // This device's bootloader (or, where addresses are hidden, the first one seen): connect right away.
                if (app is null || IsBootloaderOf(d, app))
                {
                    ours ??= d;
                    window.Cancel();
                }
            }

            adapter.DeviceDiscovered += OnDiscovered;
            try
            {
                window.CancelAfter(ScanWindow);
                await adapter.StartScanningForDevicesAsync(
                    new ScanFilterOptions { ServiceUuids = new[] { SecureDfuService.Service } },
                    deviceFilter: IsBootloader,
                    allowDuplicatesKey: false,
                    cancellationToken: window.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* found, or the window elapsed */ }
            finally
            {
                try { await adapter.StopScanningForDevicesAsync(); } catch { /* ignore */ }
                adapter.DeviceDiscovered -= OnDiscovered;
            }

            // Not recognized by address: there is rarely more than one bootloader around, take the nearest.
            var device = ours ?? found.Values.OrderByDescending(d => d.Rssi).FirstOrDefault();
            return device is null ? null : await BleDfuChannel.OpenAsync(adapter, device, ct);
        }

        /// <summary>
        /// The bootloader takes the application's address + 1 (least significant byte, no carry), and
        /// advertises the application's address after company id 0xFFFF, most significant byte first.
        /// </summary>
        private static bool IsBootloaderOf(IDevice d, byte[] app)
        {
            var own = Address(d.Id);
            if (own is not null && own.AsSpan(0, 5).SequenceEqual(app.AsSpan(0, 5)) && own[5] == (byte)(app[5] + 1)) return true;
            return Records(d).Any(r =>
                r.Type == AdvertisementRecordType.ManufacturerSpecificData &&
                r.Data is { Length: >= 8 } && r.Data[0] == 0xFF && r.Data[1] == 0xFF && r.Data.AsSpan(2, 6).SequenceEqual(app));
        }
    }
}

/// <summary>Secure DFU over BLE: requests on the control point, image data on the packet characteristic.</summary>
public sealed class BleDfuChannel : IDfuChannel
{
    private static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDisconnectGrace = TimeSpan.FromMilliseconds(250);

    private readonly IAdapter _adapter;
    private readonly IDevice _device;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _disconnectGrace;
    private ICharacteristic _control = null!;
    private ICharacteristic _packet = null!;
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _linkClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disconnected;

    private BleDfuChannel(IAdapter adapter, IDevice device, TimeSpan? responseTimeout = null, TimeSpan? disconnectGrace = null)
    {
        _adapter = adapter;
        _device = device;
        _responseTimeout = responseTimeout ?? DefaultResponseTimeout;
        _disconnectGrace = disconnectGrace ?? DefaultDisconnectGrace;
    }

    internal const int MaxRequestedMtu = 247;
    private const int MaxPayloadSize = MaxRequestedMtu - 3;

    public int MaxDataSize { get; private set; } = 20;

    // Below the bootloader's 8 receive buffers (NRF_DFU_BLE_BUFFERS), so data writes never overrun it.
    public int PacketReceiptInterval => 6;

    /// <summary>
    /// Computes the DFU packet payload size from <see cref="IDevice.RequestMtuAsync"/>.
    /// Android returns the negotiated ATT MTU (including the 3-byte ATT header), whereas
    /// Plugin.BLE on Apple platforms returns <c>GetMaximumWriteValueLength(WithoutResponse)</c>,
    /// which is already the payload length. The nRF5 SDK bootloader's <c>nrf_fstorage_write</c>
    /// requires flash writes to be 4-byte aligned, so the payload is rounded down to a multiple of 4.
    /// </summary>
    internal static int DataSizeForMtu(int reportedMtu, bool returnsPayloadLength)
    {
        var payload = returnsPayloadLength ? reportedMtu : reportedMtu - 3;
        if (payload > MaxPayloadSize) payload = MaxPayloadSize;
        payload &= ~3;
        return payload >= 20 ? payload : 20;
    }

    public static Task<BleDfuChannel> OpenAsync(IAdapter adapter, IDevice device, CancellationToken ct) =>
        OpenAsync(adapter, device, OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst(), ct);

    internal static async Task<BleDfuChannel> OpenAsync(
        IAdapter adapter, IDevice device, bool returnsPayloadLength, CancellationToken ct,
        TimeSpan? responseTimeout = null, TimeSpan? disconnectGrace = null)
    {
        var channel = new BleDfuChannel(adapter, device, responseTimeout, disconnectGrace);
        try
        {
            // LE explicitly: Android's default transport may try classic Bluetooth on a device it just met (GATT error 133).
            await adapter.ConnectToDeviceAsync(device, new ConnectParameters(autoConnect: false, forceBleTransport: true), ct);
            adapter.DeviceDisconnected += channel.OnDisconnected;
            adapter.DeviceConnectionLost += channel.OnConnectionLost;

            if (!returnsPayloadLength)
                await channel.UpdateMaxDataSizeAsync(returnsPayloadLength: false, ct);

            var service = await device.GetServiceAsync(SecureDfuService.Service, ct)
                ?? throw new InvalidOperationException(Strings.Dfu_NoService);
            channel._control = await service.GetCharacteristicAsync(SecureDfuService.ControlPoint, ct)
                ?? throw new InvalidOperationException(Strings.Dfu_NoControlPoint);
            channel._packet = await service.GetCharacteristicAsync(SecureDfuService.Packet, ct)
                ?? throw new InvalidOperationException(Strings.Dfu_NoPacketChar);
            channel._control.WriteType = CharacteristicWriteType.WithResponse;
            channel._packet.WriteType = CharacteristicWriteType.WithoutResponse;

            channel._control.ValueUpdated += channel.OnControlValue;
            await channel._control.StartUpdatesAsync(ct);

            // CoreBluetooth negotiates MTU automatically; querying after service discovery and CCCD
            // setup ensures the negotiated value has settled rather than reading the initial 20-byte default.
            if (returnsPayloadLength)
                await channel.UpdateMaxDataSizeAsync(returnsPayloadLength: true, ct);

            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    private static readonly TimeSpan MtuTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);

    private async Task UpdateMaxDataSizeAsync(bool returnsPayloadLength, CancellationToken ct)
    {
        using var mtuCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        mtuCts.CancelAfter(MtuTimeout);
        try
        {
            var mtu = await _device.RequestMtuAsync(MaxRequestedMtu, mtuCts.Token).WaitAsync(mtuCts.Token);
            MaxDataSize = DataSizeForMtu(mtu, returnsPayloadLength);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* stay at the default 23-byte MTU */ }
    }

    private void OnControlValue(object? sender, CharacteristicUpdatedEventArgs e)
    {
        if (e.Characteristic.Value is { Length: > 0 } value)
            _responses.Writer.TryWrite(value.ToArray());
    }

    private void OnDisconnected(object? sender, DeviceEventArgs e) => OnDrop(e.Device);
    private void OnConnectionLost(object? sender, DeviceErrorEventArgs e) => OnDrop(e.Device);

    private void OnDrop(IDevice? device)
    {
        if (device?.Id != _device.Id) return;
        _disconnected = true;
        _linkClosed.TrySetResult();
        _responses.Writer.TryComplete();
    }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
    {
        await WriteCheckedAsync(_control, request, ct);
        return await ReceiveAsync(ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_responseTimeout);
        try
        {
            return await _responses.Reader.ReadAsync(timeout.Token);
        }
        catch (ChannelClosedException)
        {
            throw new DfuLinkClosedException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Strings.Dfu_Stopped);
        }
    }

    public Task WriteDataAsync(byte[] data, CancellationToken ct) =>
        WriteCheckedAsync(_packet, data, ct);

    private async Task WriteCheckedAsync(ICharacteristic characteristic, byte[] payload, CancellationToken ct)
    {
        if (_disconnected) throw new DfuLinkClosedException();
        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeCts.CancelAfter(_responseTimeout);
        var writeTask = characteristic.WriteAsync(payload, writeCts.Token);
        _ = writeTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            var finished = await Task.WhenAny(writeTask, _linkClosed.Task).WaitAsync(writeCts.Token);
            if (ReferenceEquals(finished, _linkClosed.Task))
            {
                writeCts.Cancel();
                throw new DfuLinkClosedException();
            }
            await writeTask;
        }
        catch (DfuLinkClosedException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw _disconnected ? new DfuLinkClosedException() : new TimeoutException(Strings.Dfu_Stopped);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            if (_disconnected || _device.State != DeviceState.Connected)
                throw new DfuLinkClosedException();
            // A native write failure on disconnect can race slightly ahead of DeviceDisconnected.
            if (await Task.WhenAny(_linkClosed.Task, Task.Delay(_disconnectGrace, ct)) == _linkClosed.Task)
                throw new DfuLinkClosedException();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _adapter.DeviceDisconnected -= OnDisconnected;
        _adapter.DeviceConnectionLost -= OnConnectionLost;
        if (_control is not null)
        {
            _control.ValueUpdated -= OnControlValue;
            if (!_disconnected)
            {
                using var stopCts = new CancellationTokenSource(DisposeTimeout);
                try { await _control.StopUpdatesAsync(stopCts.Token).WaitAsync(stopCts.Token); } catch { /* the bootloader may have reset already */ }
            }
        }
        using var discCts = new CancellationTokenSource(DisposeTimeout);
        try { await _adapter.DisconnectDeviceAsync(_device).WaitAsync(discCts.Token); } catch { /* ignore */ }
        _disconnected = true;
        _linkClosed.TrySetResult();
        _responses.Writer.TryComplete();
    }
}
