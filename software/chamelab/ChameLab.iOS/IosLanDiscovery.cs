using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using ChameLab.Sync;
using Foundation;

namespace ChameLab.iOS;

/// <summary>
/// Local sync discovery through the system's Bonjour: iOS lets apps use multicast sockets only
/// with a special entitlement, but announces and browses DNS-SD services for them. Speaks the same
/// <c>_chamelab-sync._tcp</c> records as <see cref="MdnsDiscovery"/> on the other platforms.
/// </summary>
#pragma warning disable CA1422, CS0618 // NSNetService is deprecated but still the simplest Bonjour API from .NET.
internal sealed class IosLanDiscovery : ILanDiscovery
{
    private const string Type = "_chamelab-sync._tcp.";
    private readonly string _name = $"chamelab-{Guid.NewGuid():N}"[..17];
    private readonly List<NSNetService> _resolving = new();
    private NSNetService? _service;
    private NSNetServiceBrowser? _browser;

    public event Action<LanAnnouncement>? Found;

    public void Start(int port, IReadOnlyDictionary<string, string> text)
    {
        _service = new NSNetService("local.", Type, _name, port);
        _service.SetTxtRecordData(Txt(text));
        _service.Publish();
        _browser = new NSNetServiceBrowser();
        _browser.FoundService += (_, e) => Resolve(e.Service);
        _browser.SearchForServices(Type, "local.");
    }

    public void Update(IReadOnlyDictionary<string, string> text) => _service?.SetTxtRecordData(Txt(text));

    public void Browse()
    {
        if (_browser is null) return;
        _browser.Stop();
        _browser.SearchForServices(Type, "local.");
    }

    private void Resolve(NSNetService service)
    {
        if (service.Name == _name) return;
        _resolving.Add(service);
        service.AddressResolved += (_, _) =>
        {
            _resolving.Remove(service);
            var endpoints = (service.Addresses ?? Array.Empty<NSData>())
                .Select(Endpoint).OfType<IPEndPoint>().ToList();
            if (endpoints.Count == 0 || service.TxtRecordData is not { } data) return;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in NSNetService.DictionaryFromTxtRecord(data))
                if (value is NSData bytes) values[key.ToString()] = Encoding.UTF8.GetString(bytes.ToArray());
            Found?.Invoke(new LanAnnouncement(endpoints, values));
        };
        service.ResolveFailure += (_, _) => _resolving.Remove(service);
        service.Resolve(5);
    }

    /// <summary>An IPv4 <c>sockaddr_in</c>: length, family (2), port (big endian), address.</summary>
    private static IPEndPoint? Endpoint(NSData data)
    {
        var bytes = data.ToArray();
        if (bytes.Length < 8 || bytes[1] != 2) return null;
        return new IPEndPoint(new IPAddress(bytes.AsSpan(4, 4)), bytes[2] << 8 | bytes[3]);
    }

    private static NSData Txt(IReadOnlyDictionary<string, string> text)
    {
        var dict = new NSMutableDictionary();
        foreach (var (key, value) in text) dict[key] = NSData.FromString(value, NSStringEncoding.UTF8);
        return NSNetService.DataFromTxtRecord(dict);
    }

    public void Dispose()
    {
        _browser?.Stop();
        _service?.Stop();
        _browser = null;
        _service = null;
    }
}
#pragma warning restore CA1422, CS0618
