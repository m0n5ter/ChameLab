using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Transport;

namespace ChameLab.Services;

/// <summary>
/// Minimal composition root. Each platform head registers its transport
/// scanners (serial on desktop, BLE on mobile) before the Avalonia app starts;
/// the TCP scanner is always available as a cross-platform fallback.
/// </summary>
public static class AppServices
{
    private static DeviceService? _device;
    private static IKeyValueStore? _store;
    private static CardLibrary? _cards;
    private static DictionaryLibrary? _dictionaries;
    private static IReadOnlyList<IDfuLocator> _dfu = Array.Empty<IDfuLocator>();
    private static FirmwareUpdateService? _firmware;
    private static IFeedbackPlayer? _feedback;

    public static DeviceService Device =>
        _device ??= Build(Enumerable.Empty<ITransportScanner>());

    /// <summary>Persistent app data; files under LocalApplicationData unless a head overrides it.</summary>
    public static IKeyValueStore Store => _store ??= new FileKeyValueStore();

    public static CardLibrary Cards => _cards ??= new CardLibrary(Store);

    public static DictionaryLibrary Dictionaries => _dictionaries ??= new DictionaryLibrary(Store);

    public static FirmwareUpdateService Firmware => _firmware ??= new FirmwareUpdateService(Device, _dfu);

    /// <summary>Scan sounds and vibration; the desktop player unless a head registers its own.</summary>
    public static IFeedbackPlayer Feedback => _feedback ??= new DesktopFeedbackPlayer();

    /// <summary>
    /// Called by each platform entry point with its native scanners, the bootloader
    /// locators for the transports firmware can be installed over (USB serial, BLE),
    /// and its sound and vibration player.
    /// </summary>
    public static void Register(IEnumerable<ITransportScanner> platformScanners, IKeyValueStore? store = null,
        IEnumerable<IDfuLocator>? dfu = null, IFeedbackPlayer? feedback = null)
    {
        _device = Build(platformScanners);
        if (store is not null) _store = store;
        _dfu = dfu?.ToList() ?? new List<IDfuLocator>();
        if (feedback is not null) _feedback = feedback;
    }

    private static DeviceService Build(IEnumerable<ITransportScanner> platformScanners)
    {
        var scanners = new List<ITransportScanner>(platformScanners) { new TcpScanner() };
        return new DeviceService(scanners);
    }
}
