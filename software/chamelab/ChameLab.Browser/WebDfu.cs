using System;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Browser;

/// <summary>
/// Finds the Chameleon's DFU bootloader (USB 1915:521F) among the Web Serial ports. The page sees
/// only ports it was granted, so the first update needs the bootloader picked in the browser's
/// chooser; the browser remembers the grant, and later updates find it on their own.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebSerialDfuLocator : IDfuLocator
{
    private const int BootloaderVendorId = 0x1915, BootloaderProductId = 0x521F;

    public TransportKind Kind => TransportKind.Serial;

    public IDfuTarget Prepare(TransportCandidate? device) => new Target();

    private sealed class Target : IDfuPickTarget
    {
        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            await WebSerialInterop.EnsureModuleAsync();
            foreach (var part in (await WebSerialInterop.ListGranted()).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = int.Parse(part);
                if (WebSerialInterop.HasUsbId(index, BootloaderVendorId, BootloaderProductId))
                    return await SerialDfuChannel.OpenAsync(new WebSerialDfuTransport(index), ct);
            }
            return null;
        }

        // The granted port shows up in the next TryOpenAsync.
        public async Task PickAsync(CancellationToken ct)
        {
            await WebSerialInterop.EnsureModuleAsync();
            await WebSerialInterop.RequestPort(BootloaderVendorId, BootloaderProductId);
        }
    }
}

/// <summary>
/// The bootloader's Web Serial port, for <see cref="SerialDfuChannel"/>. Reads give up after a
/// moment (the channel keeps its own deadline), and a port that went away surfaces as an
/// <see cref="IOException"/>, which the channel takes for the bootloader having reset.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WebSerialDfuTransport(int index) : ITransport
{
    private const int ReadWindowMs = 500;

    private bool _open;
    private byte[] _pending = [];
    private int _pendingOffset;

    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => "DFU bootloader";
    public bool IsConnected => _open;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await WebSerialInterop.Open(index, 115200);
        _open = true;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try
        {
            await WebSerialInterop.WriteBase64(index, Convert.ToBase64String(data.Span));
        }
        catch (JSException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingOffset == _pending.Length)
        {
            string b64;
            try
            {
                b64 = await WebSerialInterop.ReadBase64Within(index, ReadWindowMs);
            }
            catch (JSException ex)
            {
                throw new IOException(ex.Message, ex);
            }
            if (b64.Length == 0) return 0;
            _pending = Convert.FromBase64String(b64);
            _pendingOffset = 0;
        }

        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _open = false;
        try { await WebSerialInterop.Close(index); } catch { /* the bootloader may have reset already */ }
    }
}

/// <summary>
/// Finds the Chameleon's bootloader over Web Bluetooth: after the restart it is a new device
/// ("CU-xxxx" / "CL-xxxx"), which the page may open only once it was picked in the browser's
/// chooser. Where the browser lists earlier grants, a bootloader picked in a past update is
/// tried on its own. Addresses are hidden from pages, so any Chameleon bootloader will do.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebBluetoothDfuLocator : IDfuLocator
{
    public TransportKind Kind => TransportKind.Ble;

    public IDfuTarget Prepare(TransportCandidate? device) => new Target();

    private sealed class Target : IDfuPickTarget
    {
        // Picked in the chooser: it is advertising, so a connection gets a long while.
        private const int PickedConnectMs = 15000;
        // Granted earlier: it may not be there yet, so don't hold up the next try.
        private const int KnownConnectMs = 4000;

        private string? _picked;

        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            await WebBluetoothInterop.EnsureModuleAsync();
            if (_picked is { } picked)
                return await WebBluetoothDfuChannel.OpenAsync(picked, PickedConnectMs);

            Exception? last = null;
            foreach (var line in (await WebBluetoothInterop.ListKnownBootloaders()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (_picked is not null) break; // picked meanwhile: the next try takes it
                try
                {
                    return await WebBluetoothDfuChannel.OpenAsync(line[..line.IndexOf('\t')], KnownConnectMs);
                }
                catch (Exception ex) { last = ex; }
            }
            if (last is not null) throw last;
            return null;
        }

        public async Task PickAsync(CancellationToken ct)
        {
            await WebBluetoothInterop.EnsureModuleAsync();
            var line = await WebBluetoothInterop.RequestBootloader();
            if (line.Length > 0) _picked = line[..line.IndexOf('\t')];
        }
    }
}

/// <summary>Secure DFU over Web Bluetooth: requests on the control point, image data on the packet characteristic.</summary>
[SupportedOSPlatform("browser")]
internal sealed class WebBluetoothDfuChannel : IDfuChannel
{
    private const int ResponseTimeoutMs = 10000;

    private readonly string _id;

    private WebBluetoothDfuChannel(string id) => _id = id;

    // The browser doesn't tell the negotiated MTU, so data stays within the minimum (23 - 3 bytes).
    public int MaxDataSize => 20;

    // Below the bootloader's 8 receive buffers (NRF_DFU_BLE_BUFFERS), so data writes never overrun it.
    public int PacketReceiptInterval => 6;

    public static async Task<WebBluetoothDfuChannel> OpenAsync(string id, int connectTimeoutMs)
    {
        try
        {
            await WebBluetoothInterop.DfuOpen(id, connectTimeoutMs);
        }
        catch (JSException ex)
        {
            throw new InvalidOperationException(Message(ex), ex);
        }
        return new WebBluetoothDfuChannel(id);
    }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
    {
        await CallAsync(() => WebBluetoothInterop.DfuWriteControl(_id, Convert.ToBase64String(request)));
        return await ReceiveAsync(ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var b64 = "";
        await CallAsync(async () => b64 = await WebBluetoothInterop.DfuReceive(_id, ResponseTimeoutMs));
        if (b64.Length == 0) throw new TimeoutException(Strings.Dfu_Stopped);
        return Convert.FromBase64String(b64);
    }

    public Task WriteDataAsync(byte[] data, CancellationToken ct) =>
        CallAsync(() => WebBluetoothInterop.DfuWritePacket(_id, Convert.ToBase64String(data)));

    /// <summary>A failure on a link that dropped means the bootloader reset (expected after the last object).</summary>
    private async Task CallAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (JSException ex)
        {
            if (!WebBluetoothInterop.DfuIsOpen(_id)) throw new DfuLinkClosedException();
            throw new IOException(Message(ex), ex);
        }
    }

    // JS errors arrive as "Error: <message>"; keep just the message.
    private static string Message(JSException ex) =>
        ex.Message.StartsWith("Error: ", StringComparison.Ordinal) ? ex.Message[7..] : ex.Message;

    public async ValueTask DisposeAsync()
    {
        try { await WebBluetoothInterop.DfuClose(_id); } catch { /* the bootloader may have reset already */ }
    }
}
