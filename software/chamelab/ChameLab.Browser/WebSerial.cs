using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;

namespace ChameLab.Browser;

/// <summary>JS interop bindings to <c>serial.js</c> (the Web Serial API bridge).</summary>
[SupportedOSPlatform("browser")]
internal static partial class WebSerialInterop
{
    private static Task? _import;

    public static Task EnsureModuleAsync() =>
        // Resolve against the page, not the site root: the app may be hosted in a
        // sub-path (GitHub Pages serves it from /ChameLab/app/), and a relative URL
        // would resolve against the runtime's "_framework/" folder instead.
        _import ??= JSHost.ImportAsync("serial", new Uri(new Uri(PageBaseUri()), "serial.js").AbsoluteUri);

    private static string PageBaseUri() =>
        JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;

    [JSImport("isSupported", "serial")]
    public static partial bool IsSupported();

    [JSImport("listGranted", "serial")]
    public static partial Task<int> ListGranted();

    [JSImport("requestPort", "serial")]
    public static partial Task<int> RequestPort();

    [JSImport("portInfo", "serial")]
    public static partial string PortInfo(int index);

    [JSImport("open", "serial")]
    public static partial Task Open(int index, int baud);

    [JSImport("readBase64", "serial")]
    public static partial Task<string> ReadBase64(int index);

    [JSImport("writeBase64", "serial")]
    public static partial Task WriteBase64(int index, string base64);

    [JSImport("close", "serial")]
    public static partial Task Close(int index);
}

/// <summary>
/// Serial transport for the WebAssembly build, backed by the browser's
/// Web Serial API. Bytes cross the JS boundary base64-encoded.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebSerialTransport : ITransport
{
    private readonly int _index;
    private readonly string _name;
    private bool _open;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;

    public WebSerialTransport(int index, string name)
    {
        _index = index;
        _name = name;
    }

    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => _name;
    public bool IsConnected => _open;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await WebSerialInterop.EnsureModuleAsync();
        await WebSerialInterop.Open(_index, 115200);
        _open = true;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await WebSerialInterop.WriteBase64(_index, Convert.ToBase64String(data.Span));
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingOffset < _pending.Length)
            return Drain(buffer);

        var b64 = await WebSerialInterop.ReadBase64(_index);
        if (string.IsNullOrEmpty(b64))
            return 0;

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
        try { await WebSerialInterop.Close(_index); } catch { /* ignore */ }
    }
}

/// <summary>
/// Scans for Web Serial ports. Lists ports the user has already granted; if none
/// are granted yet it opens the browser's port chooser (which requires the user
/// gesture that the Rescan/Connect button provides).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebSerialScanner : ITransportScanner
{
    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => "Web Serial";
    // Opening the port chooser needs a user gesture, so only scan on Rescan.
    public AutoScan AutoScan => AutoScan.Never;

    public async Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default)
    {
        await WebSerialInterop.EnsureModuleAsync();
        if (!WebSerialInterop.IsSupported())
            return Array.Empty<TransportCandidate>();

        var list = await ListChameleonsAsync();
        if (list.Count == 0)
        {
            // No Chameleon among the granted ports (none granted yet, or only other
            // devices): prompt the chooser. A cancelled dialog simply yields no device.
            await WebSerialInterop.RequestPort();
            list = await ListChameleonsAsync();
        }
        return list;
    }

    private static async Task<List<TransportCandidate>> ListChameleonsAsync()
    {
        var count = await WebSerialInterop.ListGranted();
        var list = new List<TransportCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var info = WebSerialInterop.PortInfo(i);
            if (!info.Contains("26728")) continue; // USB VID 0x6868 = Chameleon
            list.Add(new TransportCandidate(
                TransportKind.Serial,
                i.ToString(),
                $"Chameleon (port {i})",
                "Web Serial"));
        }
        return list;
    }

    public ITransport Create(TransportCandidate candidate) =>
        new WebSerialTransport(int.Parse(candidate.Id), candidate.DisplayName);
}
