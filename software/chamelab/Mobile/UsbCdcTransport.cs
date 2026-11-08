using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Transport;
using ChameLab.Localization;

namespace ChameLab.Mobile;

internal sealed record UsbCdcDevice(string Id, string Name, int VendorId, bool HasCdcAcm, int ProductId = 0)
{
    public const int ChameleonVendorId = 0x6868;
    /// <summary>The nRF5 SDK bootloader's USB serial DFU port, which the Chameleon restarts into.</summary>
    public const int BootloaderVendorId = 0x1915, BootloaderProductId = 0x521F;

    public bool IsBootloader => VendorId == BootloaderVendorId && ProductId == BootloaderProductId;
}

/// <summary>The native USB boundary. Opening includes permission, claiming interfaces and CDC setup.</summary>
internal interface IUsbCdcHost
{
    IReadOnlyList<UsbCdcDevice> GetDevices();
    Task<IUsbCdcConnection> OpenAsync(string id, CancellationToken ct);
}

internal interface IUsbCdcConnection : IDisposable
{
    bool IsAttached { get; }
    int Read(byte[] buffer, int timeoutMs);
    int Write(byte[] buffer, int offset, int count, int timeoutMs);
}

internal sealed class UsbCdcScanner(IUsbCdcHost host) : ITransportScanner
{
    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => "USB";
    public AutoScan AutoScan => AutoScan.Poll;

    public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Match the desktop VID filter, and leave DFU bootloaders and unrelated USB devices out.
        IReadOnlyList<TransportCandidate> devices = host.GetDevices()
            .Where(d => d is { VendorId: UsbCdcDevice.ChameleonVendorId, HasCdcAcm: true })
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => new TransportCandidate(Kind, d.Id, d.Name, "USB serial · " + d.Id))
            .ToArray();
        return Task.FromResult(devices);
    }

    public ITransport Create(TransportCandidate candidate) => new UsbCdcTransport(host, candidate.Id, candidate.DisplayName);
}

/// <summary>
/// Finds the Chameleon's DFU bootloader (USB 1915:521F) after it restarts and talks
/// Secure DFU to it over the bootloader's own CDC port, as on the desktop.
/// </summary>
internal sealed class UsbCdcDfuLocator(IUsbCdcHost host) : IDfuLocator
{
    public TransportKind Kind => TransportKind.Serial;

    public IDfuTarget Prepare(TransportCandidate? device) => new Target(host);

    private sealed class Target(IUsbCdcHost host) : IDfuTarget
    {
        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct) =>
            host.GetDevices().FirstOrDefault(d => d is { IsBootloader: true, HasCdcAcm: true }) is { } bootloader
                ? await SerialDfuChannel.OpenAsync(new UsbCdcTransport(host, bootloader.Id, bootloader.Name), ct)
                : null;
    }
}

/// <summary>CDC byte stream with bounded native transfers, partial writes and buffered packet reads.</summary>
internal sealed class UsbCdcTransport(IUsbCdcHost host, string id, string name) : ITransport
{
    private IUsbCdcConnection? _connection;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed, _connecting;
    // A multiple of the USB packet size: a small caller buffer must not truncate a USB packet.
    private readonly byte[] _readBuffer = new byte[16384];
    private int _readOffset, _readCount;

    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => name;
    public bool IsConnected => Volatile.Read(ref _connection)?.IsAttached == true;
    public bool OneRequestAtATime => true;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null || _connecting) throw new InvalidOperationException(Strings.Usb_AlreadyConnected);
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            _connecting = true;
        }
        IUsbCdcConnection? opened = null;
        using (linked)
        try
        {
            opened = await host.OpenAsync(id, linked.Token);
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                _readOffset = _readCount = 0;
                _connection = opened;
                opened = null;
            }
        }
        finally
        {
            opened?.Dispose();
            lock (_gate) _connecting = false;
        }
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var connection = Connected();
        if (buffer.IsEmpty) return 0;
        if (_readCount == 0)
        {
            var count = connection.Read(_readBuffer, 100);
            ct.ThrowIfCancellationRequested();
            if (!connection.IsAttached) throw new IOException(Strings.Usb_Disconnected);
            // Android's bulk API uses a negative result for a read timeout too.
            if (count <= 0) return 0;
            _readOffset = 0;
            _readCount = count;
        }
        var copied = Math.Min(buffer.Length, _readCount);
        _readBuffer.AsMemory(_readOffset, copied).CopyTo(buffer);
        _readOffset += copied;
        _readCount -= copied;
        return copied;
    }, ct);

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var connection = Connected();
        var bytes = data.ToArray();
        var offset = 0;
        while (offset < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(16384, bytes.Length - offset);
            var written = connection.Write(bytes, offset, count, 1000);
            ct.ThrowIfCancellationRequested();
            if (written <= 0) throw new IOException(Strings.Usb_WriteFailed);
            offset += written;
        }
    }, ct);

    private IUsbCdcConnection Connected() => Volatile.Read(ref _connection) is { IsAttached: true } connection
        ? connection : throw new IOException(Strings.Usb_NotConnected);

    public ValueTask DisposeAsync()
    {
        IUsbCdcConnection? connection;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            connection = _connection;
            _connection = null;
        }
        try { _lifetime.Cancel(); }
        finally
        {
            _lifetime.Dispose();
            connection?.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
