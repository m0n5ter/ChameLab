using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Transport;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ChameLab.Mobile;

/// <summary>
/// Well-known Nordic UART Service (NUS) UUIDs. The Chameleon firmware exposes
/// its command channel over NUS when connected via BLE.
/// </summary>
internal static class NordicUart
{
    public static readonly Guid Service = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    /// <summary>Central → peripheral (write).</summary>
    public static readonly Guid Rx = new("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    /// <summary>Peripheral → central (notify).</summary>
    public static readonly Guid Tx = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
}

/// <summary>
/// BLE transport over the Nordic UART Service, for Android and iOS. Notifications
/// on the TX characteristic are buffered into a channel that <see cref="ReadAsync"/>
/// drains; <see cref="WriteAsync"/> chunks frames to the negotiated MTU on RX.
/// </summary>
public sealed class BleTransport : ITransport
{
    private readonly Guid _deviceId;
    private readonly string _displayName;
    private readonly IAdapter _adapter;

    private IDevice? _device;
    private ICharacteristic? _rx;
    private ICharacteristic? _tx;
    private int _mtuPayload = 20; // conservative default until MTU is negotiated

    private readonly Channel<byte[]> _incoming =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;

    public BleTransport(Guid deviceId, string displayName)
    {
        _deviceId = deviceId;
        _displayName = displayName;
        _adapter = CrossBluetoothLE.Current.Adapter;
    }

    public TransportKind Kind => TransportKind.Ble;
    public string DisplayName => _displayName;
    public bool IsConnected => _device?.State == DeviceState.Connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _device = await _adapter.ConnectToKnownDeviceAsync(_deviceId, ConnectParameters.None, ct);

        // Larger MTU greatly improves throughput; ignore if unsupported.
        try
        {
            var mtu = await _device.RequestMtuAsync(247, ct);
            if (mtu > 3) _mtuPayload = mtu - 3; // minus ATT header
        }
        catch { /* best effort */ }

        var service = await _device.GetServiceAsync(NordicUart.Service, ct)
            ?? throw new InvalidOperationException("Nordic UART service not found on device.");
        _rx = await service.GetCharacteristicAsync(NordicUart.Rx, ct)
            ?? throw new InvalidOperationException("NUS RX characteristic not found.");
        _tx = await service.GetCharacteristicAsync(NordicUart.Tx, ct)
            ?? throw new InvalidOperationException("NUS TX characteristic not found.");

        if (_rx.CanWrite)
            _rx.WriteType = CharacteristicWriteType.WithoutResponse;

        _tx.ValueUpdated += OnTxValueUpdated;
        await _tx.StartUpdatesAsync(ct);

        _adapter.DeviceDisconnected += OnDeviceDisconnected;
        _adapter.DeviceConnectionLost += OnDeviceConnectionLost;
    }

    private void OnTxValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var value = e.Characteristic.Value;
        if (value is { Length: > 0 })
            _incoming.Writer.TryWrite(value);
    }

    private void OnDeviceDisconnected(object? sender, DeviceEventArgs e) => HandleDrop(e.Device);
    private void OnDeviceConnectionLost(object? sender, DeviceErrorEventArgs e) => HandleDrop(e.Device);

    private void HandleDrop(IDevice? device)
    {
        if (device?.Id == _deviceId)
            _incoming.Writer.TryComplete();
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_rx is null) throw new InvalidOperationException("Not connected.");
        var offset = 0;
        while (offset < data.Length)
        {
            var len = Math.Min(_mtuPayload, data.Length - offset);
            var chunk = data.Slice(offset, len).ToArray();
            await _rx.WriteAsync(chunk, ct);
            offset += len;
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // Serve any leftover from a previous oversized notification first.
        if (_pendingOffset < _pending.Length)
            return DrainPending(buffer);

        byte[] chunk;
        try
        {
            chunk = await _incoming.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("BLE link closed.");
        }

        _pending = chunk;
        _pendingOffset = 0;
        return DrainPending(buffer);
    }

    private int DrainPending(Memory<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _adapter.DeviceDisconnected -= OnDeviceDisconnected;
        _adapter.DeviceConnectionLost -= OnDeviceConnectionLost;
        if (_tx is not null)
        {
            _tx.ValueUpdated -= OnTxValueUpdated;
            try { await _tx.StopUpdatesAsync(); } catch { /* ignore */ }
        }
        if (_device is not null)
        {
            try { await _adapter.DisconnectDeviceAsync(_device); } catch { /* ignore */ }
        }
        _incoming.Writer.TryComplete();
    }
}

/// <summary>
/// Scans for nearby Chameleon devices advertising over BLE. Matches devices that
/// advertise the Nordic UART service or whose name begins with "Chameleon".
/// </summary>
public sealed class BleScanner : ITransportScanner
{
    private readonly IAdapter _adapter = CrossBluetoothLE.Current.Adapter;
    private readonly IBluetoothLE _ble = CrossBluetoothLE.Current;

    public TransportKind Kind => TransportKind.Ble;
    public string DisplayName => "Bluetooth LE";

    // The adapter scans for one caller at a time: a second StartScanning returns at once and its
    // StopScanning would cut the first one short, so overlapping callers share the running scan.
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<TransportCandidate>>? _running;

    public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_running is { IsCompleted: false }) return _running;
            return _running = ScanCoreAsync(ct);
        }
    }

    private async Task<IReadOnlyList<TransportCandidate>> ScanCoreAsync(CancellationToken ct)
    {
        var found = new Dictionary<Guid, TransportCandidate>();
        if (!_ble.IsAvailable || !_ble.IsOn)
            return Array.Empty<TransportCandidate>();

        void OnDiscovered(object? sender, DeviceEventArgs e)
        {
            var d = e.Device;
            if (d is null || !IsChameleon(d)) return;
            found[d.Id] = new TransportCandidate(
                TransportKind.Ble,
                d.Id.ToString(),
                string.IsNullOrWhiteSpace(d.Name) ? "Chameleon (BLE)" : d.Name!,
                $"BLE · RSSI {d.Rssi}");
        }

        _adapter.DeviceDiscovered += OnDiscovered;
        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts.CancelAfter(TimeSpan.FromSeconds(6));
            await _adapter.StartScanningForDevicesAsync(
                new ScanFilterOptions(),
                deviceFilter: IsChameleon,
                allowDuplicatesKey: false,
                cancellationToken: scanCts.Token);
        }
        catch (OperationCanceledException) { /* scan window elapsed */ }
        finally
        {
            try { await _adapter.StopScanningForDevicesAsync(); } catch { /* ignore */ }
            _adapter.DeviceDiscovered -= OnDiscovered;
        }

        return new List<TransportCandidate>(found.Values);
    }

    public ITransport Create(TransportCandidate candidate) =>
        new BleTransport(Guid.Parse(candidate.Id), candidate.DisplayName);

    private static bool IsChameleon(IDevice d)
    {
        if (!string.IsNullOrEmpty(d.Name) &&
            d.Name!.StartsWith("Chameleon", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var record in d.AdvertisementRecords ?? new List<AdvertisementRecord>())
        {
            if (record.Type is AdvertisementRecordType.UuidsComplete128Bit
                or AdvertisementRecordType.UuidsIncomplete128Bit)
                return true; // advertises a 128-bit service (NUS); confirmed on connect
        }
        return false;
    }
}
