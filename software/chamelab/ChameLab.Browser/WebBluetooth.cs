using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;

namespace ChameLab.Browser;

/// <summary>JS interop bindings to <c>ble.js</c> (the Web Bluetooth API bridge).</summary>
[SupportedOSPlatform("browser")]
internal static partial class WebBluetoothInterop
{
    private static Task? _import;

    public static Task EnsureModuleAsync() =>
        // Resolved against the page for the same reason as serial.js (see WebSerialInterop).
        _import ??= JSHost.ImportAsync("ble", new Uri(new Uri(PageBaseUri()), "ble.js").AbsoluteUri);

    private static string PageBaseUri() =>
        JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;

    /// <summary>Whether the browser has Web Bluetooth; checked without loading the module.</summary>
    public static bool IsAvailable() =>
        JSHost.GlobalThis.GetPropertyAsJSObject("navigator")?.HasProperty("bluetooth") == true;

    /// <summary>One "id&lt;TAB&gt;name" line per device the page may use without a prompt.</summary>
    [JSImport("listKnown", "ble")]
    public static partial Task<string> ListKnown();

    /// <summary>"id&lt;TAB&gt;name" of the picked device, or "" if the chooser was dismissed.</summary>
    [JSImport("requestDevice", "ble")]
    public static partial Task<string> RequestDevice();

    [JSImport("connect", "ble")]
    public static partial Task Connect(string id);

    [JSImport("isConnected", "ble")]
    public static partial bool IsConnected(string id);

    [JSImport("readBase64", "ble")]
    public static partial Task<string> ReadBase64(string id);

    [JSImport("writeBase64", "ble")]
    public static partial Task WriteBase64(string id, string base64);

    [JSImport("close", "ble")]
    public static partial Task Close(string id);
}

/// <summary>
/// BLE transport for the WebAssembly build, over the Nordic UART Service via the
/// browser's Web Bluetooth API. Bytes cross the JS boundary base64-encoded.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebBluetoothTransport(string id, string name) : ITransport
{
    private bool _open;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;

    public TransportKind Kind => TransportKind.Ble;
    public string DisplayName => name;
    public bool IsConnected => _open && WebBluetoothInterop.IsConnected(id);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await WebBluetoothInterop.EnsureModuleAsync();
        try
        {
            await WebBluetoothInterop.Connect(id);
        }
        catch (JSException ex)
        {
            // JS errors arrive as "Error: <message>"; show just the message.
            throw new InvalidOperationException(ex.Message.StartsWith("Error: ", StringComparison.Ordinal) ? ex.Message[7..] : ex.Message);
        }
        _open = true;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await WebBluetoothInterop.WriteBase64(id, Convert.ToBase64String(data.Span));
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingOffset < _pending.Length)
            return Drain(buffer);

        string b64;
        try
        {
            b64 = await WebBluetoothInterop.ReadBase64(id);
        }
        catch (JSException)
        {
            throw new InvalidOperationException("BLE link closed.");
        }

        _pending = Convert.FromBase64String(b64);
        _pendingOffset = 0;
        return Drain(buffer);
    }

    private int Drain(Memory<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _open = false;
        try { await WebBluetoothInterop.Close(id); } catch { /* ignore */ }
    }
}

/// <summary>
/// Lists Chameleons over Web Bluetooth. A page can't scan on its own: a device must first be
/// picked in the browser's chooser (<see cref="PickAsync"/>, from its own button), after which
/// scans list it without a prompt. The browser can't tell whether a listed device is awake;
/// connecting to a sleeping one fails with a hint to wake it.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebBluetoothScanner : ITransportScanner, ITransportPicker
{
    public TransportKind Kind => TransportKind.Ble;
    public string DisplayName => "Web Bluetooth";
    // Listing known devices is cheap and needs no prompt, so it can be polled.
    public AutoScan AutoScan => AutoScan.Poll;

    public async Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default)
    {
        await WebBluetoothInterop.EnsureModuleAsync();
        var list = new List<TransportCandidate>();
        foreach (var line in (await WebBluetoothInterop.ListKnown()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            list.Add(Candidate(line));
        return list;
    }

    public async Task<TransportCandidate?> PickAsync(CancellationToken ct = default)
    {
        await WebBluetoothInterop.EnsureModuleAsync();
        var picked = await WebBluetoothInterop.RequestDevice();
        return picked.Length == 0 ? null : Candidate(picked);
    }

    private static TransportCandidate Candidate(string line)
    {
        var tab = line.IndexOf('\t');
        var id = line[..tab];
        var name = line[(tab + 1)..];
        return new TransportCandidate(
            TransportKind.Ble,
            id,
            string.IsNullOrWhiteSpace(name) ? "Chameleon (BLE)" : name,
            "Web Bluetooth");
    }

    public ITransport Create(TransportCandidate candidate) =>
        new WebBluetoothTransport(candidate.Id, candidate.DisplayName);
}
