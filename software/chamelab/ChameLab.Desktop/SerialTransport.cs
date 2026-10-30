using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Desktop;

/// <summary>Serial (USB-CDC) transport backed by <see cref="SerialPort"/>.</summary>
public sealed class SerialTransport(string portName) : ITransport
{
    private SerialPort? _port;

    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => portName;
    public bool IsConnected => _port?.IsOpen ?? false;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        var port = new SerialPort(portName, 115200)
        {
            ReadTimeout = 100,
            WriteTimeout = 1000,
            DtrEnable = true, // required to wake the CDC endpoint
        };
        port.Open();
        _port = port;
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_port is null) throw new InvalidOperationException("Not connected.");
        await _port.BaseStream.WriteAsync(data, ct);
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_port is null) throw new InvalidOperationException("Not connected.");
        try
        {
            return await _port.BaseStream.ReadAsync(buffer, ct);
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Windows aborts the pending read (ERROR_OPERATION_ABORTED) when the USB device is
            // unplugged, which surfaces as a cancellation nobody asked for.
            throw new IOException($"{portName} was disconnected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        // SerialPort.Close flushes first, which throws once the device is unplugged and leaves the
        // handle open (keeping a ghost COM port registered). Disposing the stream itself always
        // releases the handle, even when its own cleanup fails.
        try { _port?.BaseStream.Dispose(); } catch { /* ignore: the device may already be gone */ }
        try { _port?.Close(); } catch { /* ignore */ }
        try { _port?.Dispose(); } catch { /* ignore */ }
        _port = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Enumerates serial ports and returns only those that belong to a Chameleon
/// (USB VID 0x6868); unrelated COM ports are not offered at all.
/// </summary>
public sealed class SerialScanner : ITransportScanner
{
    private const string ChameleonVidHex = "6868";

    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => "Serial (USB)";
    public AutoScan AutoScan => AutoScan.Poll;

    public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default)
    {
        var result = new List<TransportCandidate>();
        foreach (var name in SafeGetPortNames())
        {
            if (!PortIsChameleon(name)) continue;
            result.Add(new TransportCandidate(
                TransportKind.Serial,
                name,
                $"Chameleon ({PortLabel(name)})",
                $"USB serial · {name}"));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Task.FromResult<IReadOnlyList<TransportCandidate>>(result);
    }

    private static string PortLabel(string portName) =>
        portName.StartsWith("/dev/", StringComparison.Ordinal) ? portName[5..] : portName;

    public ITransport Create(TransportCandidate candidate) => new SerialTransport(candidate.Id);

    private static string[] SafeGetPortNames()
    {
        // Windows lists a port twice while a handle to an unplugged device is still open and the
        // device has been plugged back in under the same COM name.
        try { return SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static bool PortIsChameleon(string portName)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowsSerialVid.PortMatchesVid(portName, ChameleonVidHex);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return LinuxPortMatchesVid(portName, ChameleonVidHex);
            // macOS: no cheap VID lookup; the CDC-ACM call-out node is the best signal.
            return portName.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool LinuxPortMatchesVid(string portName, string vidHex) =>
        string.Equals(LinuxUsbId(portName, "idVendor"), vidHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a USB id (idVendor / idProduct) for a tty from sysfs: /sys/class/tty/ttyACM0/device
    /// is the USB interface, whose parent directory is the USB device holding the ids.
    /// </summary>
    internal static string? LinuxUsbId(string portName, string file)
    {
        var tty = Path.GetFileName(portName);
        var iface = new DirectoryInfo($"/sys/class/tty/{tty}/device");
        if (!iface.Exists) return null;
        var target = iface.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? iface;
        var idFile = Path.Combine(target.Parent?.FullName ?? "", file);
        return File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
    }

    internal static string[] PortNames() => SafeGetPortNames();
}

/// <summary>Finds the Chameleon's DFU bootloader (USB 1915:521F) among the serial ports.</summary>
public sealed class SerialDfuLocator : IDfuLocator
{
    public TransportKind Kind => TransportKind.Serial;

    public IDfuTarget Prepare(TransportCandidate? device) => new Target(SerialScanner.PortNames());

    private sealed class Target(IReadOnlyCollection<string> before) : IDfuTarget
    {
        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct) =>
            FindBootloader() is { } port ? await SerialDfuChannel.OpenAsync(new SerialTransport(port), ct) : null;

        private string? FindBootloader()
        {
            foreach (var name in SerialScanner.PortNames())
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (WindowsSerialVid.PortMatchesUsbId(name, "VID_1915&PID_521F")) return name;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (string.Equals(SerialScanner.LinuxUsbId(name, "idVendor"), "1915", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(SerialScanner.LinuxUsbId(name, "idProduct"), "521f", StringComparison.OrdinalIgnoreCase))
                        return name;
                }
                // macOS: no cheap USB id lookup; the bootloader is the modem port that wasn't there before.
                else if (name.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal) && !before.Contains(name))
                {
                    return name;
                }
            }
            return null;
        }
    }
}
