using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;

namespace ChameLab.Protocol;

/// <summary>An nrfutil application DFU package (<c>ultra-dfu-app.zip</c>): init packet + firmware image.</summary>
public sealed record DfuPackage(byte[] InitPacket, byte[] Firmware)
{
    public static DfuPackage Parse(byte[] zip)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            byte[] Read(string name)
            {
                var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing.");
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }

            using var manifest = JsonDocument.Parse(Read("manifest.json"));
            if (!manifest.RootElement.GetProperty("manifest").TryGetProperty("application", out var app))
                throw new InvalidDataException("Only application updates are supported, this package has no application.");
            var init = Read(app.GetProperty("dat_file").GetString()!);
            var fw = Read(app.GetProperty("bin_file").GetString()!);
            if (init.Length == 0 || fw.Length == 0) throw new InvalidDataException("The package is empty.");
            return new DfuPackage(init, fw);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Not a firmware DFU package: {ex.Message}", ex);
        }
    }
}

/// <summary>Raised when the bootloader drops the link; expected right after the last object is executed.</summary>
public sealed class DfuLinkClosedException() : IOException("The connection to the bootloader was lost.");

/// <summary>
/// How Secure DFU requests and image data reach the bootloader: SLIP frames over
/// USB serial, or the control point / packet characteristics over BLE.
/// </summary>
public interface IDfuChannel : IAsyncDisposable
{
    /// <summary>Largest piece of image data per <see cref="WriteDataAsync"/>.</summary>
    int MaxDataSize { get; }

    /// <summary>
    /// Data packets after which the bootloader confirms what it got, 0 for none. BLE needs it:
    /// data goes out without responses and would otherwise overrun the bootloader's buffers.
    /// </summary>
    int PacketReceiptInterval { get; }

    /// <summary>Sends a request and returns the bootloader's response (starting with 0x60).</summary>
    Task<byte[]> RequestAsync(byte[] request, CancellationToken ct);

    /// <summary>Waits for a response without sending anything (a packet receipt).</summary>
    Task<byte[]> ReceiveAsync(CancellationToken ct);

    Task WriteDataAsync(byte[] data, CancellationToken ct);
}

/// <summary>Nordic Secure DFU (nRF5 SDK bootloader), independent of the transport.</summary>
public sealed class SecureDfu(IDfuChannel channel)
{
    public const byte OpCreate = 0x01, OpSetPrn = 0x02, OpCrc = 0x03, OpExecute = 0x04,
        OpSelect = 0x06, OpMtu = 0x07, OpWrite = 0x08, OpPing = 0x09, OpResponse = 0x60;
    private const byte ObjCommand = 0x01, ObjData = 0x02;

    private int _sinceReceipt;

    /// <summary>Uploads the package; <paramref name="progress"/> gets the fraction of the image written.</summary>
    public async Task FlashAsync(DfuPackage package, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var prn = (ushort)channel.PacketReceiptInterval;
        await RequestAsync(ct, OpSetPrn, (byte)prn, (byte)(prn >> 8));

        await RequestAsync(ct, OpSelect, ObjCommand);
        await RequestAsync(ct, [OpCreate, ObjCommand, .. BitConverter.GetBytes((uint)package.InitPacket.Length)]);
        await StreamAsync(package.InitPacket, 0, package.InitPacket.Length, ct);
        await RequestAsync(ct, OpExecute);

        var fw = package.Firmware;
        var maxObject = (int)BitConverter.ToUInt32(await RequestAsync(ct, OpSelect, ObjData), 0);
        for (var offset = 0; offset < fw.Length; offset += maxObject)
        {
            var length = Math.Min(maxObject, fw.Length - offset);
            await RequestAsync(ct, [OpCreate, ObjData, .. BitConverter.GetBytes((uint)length)]);
            await StreamAsync(fw, offset, length, ct);
            var last = offset + length >= fw.Length;
            try
            {
                await RequestAsync(ct, OpExecute);
            }
            // Executing the last object activates the image; the bootloader may reset before it answers.
            catch (Exception ex) when (last && ex is DfuLinkClosedException or TimeoutException) { }
            progress?.Report((double)(offset + length) / fw.Length);
        }
    }

    /// <summary>Writes one object and checks the device's running CRC of everything sent so far.</summary>
    private async Task StreamAsync(byte[] data, int offset, int length, CancellationToken ct)
    {
        var prn = channel.PacketReceiptInterval;
        var chunk = channel.MaxDataSize;
        _sinceReceipt = 0;
        for (var i = 0; i < length; i += chunk)
        {
            var n = Math.Min(chunk, length - i);
            await channel.WriteDataAsync(data.AsSpan(offset + i, n).ToArray(), ct);
            if (prn > 0 && ++_sinceReceipt == prn)
            {
                _sinceReceipt = 0;
                Check(achannelannel.ReceiveAsync(ct), OpCrc);
            }
        }

        var crc = await RequestAsync(ct, OpCrc);
        uint deviceOffset = BitConverter.ToUInt32(crc, 0), deviceCrc = BitConverter.ToUInt32(crc, 4);
        var expected = Crc32(data, offset + length);
        if (deviceOffset != offset + length || deviceCrc != expected)
            throw new IOException($"Firmware transfer corrupted (device at {deviceOffset} with CRC {deviceCrc:X8}, expected {offset + length} / {expected:X8}).");
    }

    private async Task<byte[]> RequestAsync(CancellationToken ct, params byte[] request) =>
        Check(achannelannel.RequestAsync(request, ct), request[0]);

    /// <summary>Validates a response to <paramref name="op"/> and returns its payload.</summary>
    public static byte[] Check(byte[] r, byte op)
    {
        if (r.Length < 3 || r[0] != OpResponse || r[1] != op)
            throw new IOException($"Unexpected bootloader response {Convert.ToHexString(r)}.");
        if (r[2] != 0x01)
            throw new IOException($"The bootloader rejected step 0x{op:X2} (error 0x{r[2]:X2}{(r.Length > 3 ? $"/0x{r[3]:X2}" : "")}).");
        return r[3..];
    }

    private static uint Crc32(byte[] data, int length)
    {
        var crc = 0xFFFFFFFF;
        for (var i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}

/// <summary>Secure DFU over the bootloader's USB serial port: SLIP frames, as nrfutil sends them.</summary>
public sealed class SerialDfuChannel : IDfuChannel
{
    private const byte SlipEnd = 0xC0, SlipEsc = 0xDB, SlipEscEnd = 0xDC, SlipEscEsc = 0xDD;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly ITransport _transport;
    private readonly byte[] _readBuffer = new byte[256];
    private int _readPos, _readLen;

    private SerialDfuChannel(ITransport transport) => _transport = transport;

    public int MaxDataSize { get; private set; }

    // Every request waits for its answer, which keeps serial in step without receipts.
    public int PacketReceiptInterval => 0;

    /// <summary>Opens <paramref name="transport"/> and asks the bootloader for its frame size.</summary>
    public static async Task<SerialDfuChannel> OpenAsync(ITransport transport, CancellationToken ct)
    {
        await transport.ConnectAsync(ct);
        await Task.Delay(200, ct);
        var channel = new SerialDfuChannel(transport);
        try
        {
            SecureDfu.Check(await channel.RequestAsync([SecureDfu.OpPing, 0x01], ct), SecureDfu.OpPing);
            var mtu = BitConverter.ToUInt16(SecureDfu.Check(await channel.RequestAsync([SecureDfu.OpMtu], ct), SecureDfu.OpMtu), 0);
            // Every byte may need SLIP escaping, and the opcode rides along.
            channel.MaxDataSize = (mtu - 1) / 2 - 1;
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
    {
        await SendAsync(request, ct);
        return await ReceiveAsync(ct);
    }

    public Task WriteDataAsync(byte[] data, CancellationToken ct) => SendAsync([SecureDfu.OpWrite, .. data], ct);

    private Task SendAsync(byte[] payload, CancellationToken ct)
    {
        var o = new List<byte>(payload.Length * 2 + 1);
        foreach (var b in payload)
        {
            if (b == SlipEnd) { o.Add(SlipEsc); o.Add(SlipEscEnd); }
            else if (b == SlipEsc) { o.Add(SlipEsc); o.Add(SlipEscEsc); }
            else o.Add(b);
        }
        o.Add(SlipEnd);
        return _transport.WriteAsync(o.ToArray(), ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ResponseTimeout;
        var frame = new List<byte>();
        var escaped = false;
        while (true)
        {
            if (_readPos == _readLen)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The bootloader stopped answering.");
                try
                {
                    _readLen = await _transport.ReadAsync(_readBuffer, ct);
                }
                catch (IOException ex) when (ex is not DfuLinkClosedException)
                {
                    // The transport noticed the device went away (the bootloader reset).
                    throw new DfuLinkClosedException();
                }
                _readPos = 0;
                continue;
            }
            var b = _readBuffer[_readPos++];
            if (b == SlipEnd)
            {
                if (frame.Count > 0) return frame.ToArray();
            }
            else if (escaped)
            {
                frame.Add(b == SlipEscEnd ? SlipEnd : SlipEsc);
                escaped = false;
            }
            else if (b == SlipEsc) escaped = true;
            else frame.Add(b);
        }
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
