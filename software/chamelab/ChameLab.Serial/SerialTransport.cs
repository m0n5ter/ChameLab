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
using ChameLab.Localization;

namespace ChameLab.Transport.Serial;

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
        if (_port is null) throw new InvalidOperationException(Strings.Tcp_NotConnected);
        await _port.BaseStream.WriteAsync(data, ct);
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_port is null) throw new InvalidOperationException(Strings.Tcp_NotConnected);
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
            throw new IOException(string.Format(Strings.Serial_Disconnected, portName));
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
    public string DisplayName => Strings.Serial_Name;
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
                return LinuxSerialUsbId.Read(portName)?.Vid == Convert.ToInt32(ChameleonVidHex, 16);
            // macOS: no cheap VID lookup; the CDC-ACM call-out node is the best signal.
            return portName.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
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
        // macOS fallback: ports listed before the restart, minus those seen gone since. macOS can
        // name the bootloader's port exactly like the application's (the name comes from the USB
        // serial number or location), so a port that dropped out and came back counts as new.
        private readonly HashSet<string> _stale = new(before, StringComparer.Ordinal);

        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct) =>
            FindBootloader() is { } port ? await SerialDfuChannel.OpenAsync(new SerialTransport(port), ct) : null;

        private string? FindBootloader()
        {
            var names = SerialScanner.PortNames();
            if (OperatingSystem.IsWindows())
            {
                foreach (var name in names)
                    if (WindowsSerialVid.PortMatchesUsbId(name, "VID_1915&PID_521F")) return name;
                return null;
            }
            if (OperatingSystem.IsLinux())
                return names.FirstOrDefault(n => LinuxSerialUsbId.Read(n) == (0x1915, 0x521F));

            var modems = names.Where(n => n.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal)).ToList();
            var ids = MacSerialUsbId.ReadAll();
            if (modems.FirstOrDefault(n => ids.TryGetValue(n, out var id) && id == (0x1915, 0x521F)) is { } bootloader)
                return bootloader;
            _stale.IntersectWith(names);
            // With ids known, a port that is not the bootloader never is; without them, guess by novelty.
            return modems.FirstOrDefault(n => !_stale.Contains(n) && !ids.ContainsKey(n));
        }
    }
}
