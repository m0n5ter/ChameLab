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
/// the Secure DFU service as "CU" / "CL"; unbonded it uses the application's address + 1,
/// which pins down the right device where the address is visible (Android).
/// </summary>
public sealed class BleDfuLocator : IDfuLocator
{
    public TransportKind Kind => TransportKind.Ble;

    public IDfuTarget Prepare(TransportCandidate? device) =>
        new Target(device is not null && Guid.TryParse(device.Id, out var id) ? BootloaderId(id) : null);

    /// <summary>
    /// Plugin.BLE on Android builds ids as <c>00000000-0000-0000-0000-{MAC}</c>; the bootloader
    /// adds 1 to the address's least significant byte (no carry). Elsewhere ids are opaque.
    /// </summary>
    private static Guid? BootloaderId(Guid appId)
    {
        var hex = appId.ToString("N");
        if (!hex.StartsWith("00000000000000000000", StringComparison.Ordinal)) return null;
        var last = (byte)(Convert.ToByte(hex[^2..], 16) + 1);
        return Guid.ParseExact(hex[..^2] + last.ToString("x2"), "N");
    }

    private sealed class Target(Guid? expected) : IDfuTarget
    {
        private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(5);

        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            var ble = CrossBluetoothLE.Current;
            if (!ble.IsAvailable || !ble.IsOn) throw new InvalidOperationException("Bluetooth is off.");
            var adapter = ble.Adapter;

            // The adapter runs one scan at a time (the Connect page may be scanning for the restarted device).
            while (adapter.IsScanning) await Task.Delay(250, ct);

            var found = new Dictionary<Guid, IDevice>();
            void OnDiscovered(object? sender, DeviceEventArgs e)
            {
                if (e.Device is { } d) found[d.Id] = d;
            }

            adapter.DeviceDiscovered += OnDiscovered;
            try
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(ScanWindow);
                await adapter.StartScanningForDevicesAsync(
                    new ScanFilterOptions { ServiceUuids = new[] { SecureDfuService.Service } },
                    deviceFilter: IsBootloader,
                    allowDuplicatesKey: false,
                    cancellationToken: window.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* scan window elapsed */ }
            finally
            {
                try { await adapter.StopScanningForDevicesAsync(); } catch { /* ignore */ }
                adapter.DeviceDiscovered -= OnDiscovered;
            }

            var device = expected is { } id
                ? found.GetValueOrDefault(id)
                : found.Values.OrderByDescending(d => d.Rssi).FirstOrDefault();
            return device is null ? null : await BleDfuChannel.OpenAsync(adapter, device, ct);
        }

        private static bool IsBootloader(IDevice d) =>
            d.Name is "CU" or "CL" ||
            (d.AdvertisementRecords ?? new List<AdvertisementRecord>()).Any(r =>
                r.Type is AdvertisementRecordType.UuidsComplete16Bit or AdvertisementRecordType.UuidsIncomple16Bit /* sic, Plugin.BLE */ &&
                r.Data.Length >= 2 && r.Data[0] == 0x59 && r.Data[1] == 0xFE);
    }
}

/// <summary>Secure DFU over BLE: requests on the control point, image data on the packet characteristic.</summary>
public sealed class BleDfuChannel : IDfuChannel
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly IAdapter _adapter;
    private readonly IDevice _device;
    private ICharacteristic _control = null!;
    private ICharacteristic _packet = null!;
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();

    private BleDfuChannel(IAdapter adapter, IDevice device)
    {
        _adapter = adapter;
        _device = device;
    }

    public int MaxDataSize { get; private set; } = 20;

    // Below the bootloader's 8 receive buffers (NRF_DFU_BLE_BUFFERS), so data writes never overrun it.
    public int PacketReceiptInterval => 6;

    public static async Task<BleDfuChannel> OpenAsync(IAdapter adapter, IDevice device, CancellationToken ct)
    {
        var channel = new BleDfuChannel(adapter, device);
        try
        {
            await adapter.ConnectToDeviceAsync(device, ConnectParameters.None, ct);
            adapter.DeviceDisconnected += channel.OnDisconnected;
            adapter.DeviceConnectionLost += channel.OnConnectionLost;

            try
            {
                var mtu = await device.RequestMtuAsync(247, ct);
                if (mtu > 23) channel.MaxDataSize = mtu - 3; // minus the ATT header
            }
            catch { /* stay at the default 23-byte MTU */ }

            var service = await device.GetServiceAsync(SecureDfuService.Service, ct)
                ?? throw new InvalidOperationException("The bootloader has no Secure DFU service.");
            channel._control = await service.GetCharacteristicAsync(SecureDfuService.ControlPoint, ct)
                ?? throw new InvalidOperationException("The DFU control point is missing.");
            channel._packet = await service.GetCharacteristicAsync(SecureDfuService.Packet, ct)
                ?? throw new InvalidOperationException("The DFU packet characteristic is missing.");
            channel._control.WriteType = CharacteristicWriteType.WithResponse;
            channel._packet.WriteType = CharacteristicWriteType.WithoutResponse;

            channel._control.ValueUpdated += channel.OnControlValue;
            await channel._control.StartUpdatesAsync(ct);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
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
        if (device?.Id == _device.Id) _responses.Writer.TryComplete();
    }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
    {
        try
        {
            await _control.WriteAsync(request, ct);
        }
        catch when (_responses.Reader.Completion.IsCompleted)
        {
            throw new DfuLinkClosedException();
        }
        return await ReceiveAsync(ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeout);
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
            throw new TimeoutException("The bootloader stopped answering.");
        }
    }

    public async Task WriteDataAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            await _packet.WriteAsync(data, ct);
        }
        catch when (_responses.Reader.Completion.IsCompleted)
        {
            throw new DfuLinkClosedException();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _adapter.DeviceDisconnected -= OnDisconnected;
        _adapter.DeviceConnectionLost -= OnConnectionLost;
        if (_control is not null)
        {
            _control.ValueUpdated -= OnControlValue;
            try { await _control.StopUpdatesAsync(); } catch { /* the bootloader may have reset already */ }
        }
        try { await _adapter.DisconnectDeviceAsync(_device); } catch { /* ignore */ }
        _responses.Writer.TryComplete();
    }
}
