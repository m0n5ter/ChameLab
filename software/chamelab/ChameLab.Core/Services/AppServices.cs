using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Transport;

namespace ChameLab.Services;

/// <summary>
/// Minimal composition root. Each platform head registers its transport
/// scanners (serial on desktop, BLE on mobile) before the Avalonia app starts;
/// the TCP scanner and the relay (shared devices) are always available.
/// </summary>
public static class AppServices
{
    private static DeviceService? _device;
    private static IKeyValueStore? _store;
    private static IReadOnlyList<IDfuLocator> _dfu = Array.Empty<IDfuLocator>();
    private static IFeedbackPlayer? _feedback;
    public static IHardnestedEngine Hardnested { get; private set; } = new UnavailableHardnestedEngine();
    public static IKeyRecoveryEngine KeyRecovery { get; private set; } = new ManagedKeyRecoveryEngine();
    public static WakeLockService WakeLock { get; private set; } = new();

    public static DeviceService Device =>
        _device ??= Build(Enumerable.Empty<ITransportScanner>());

    /// <summary>Persistent app data; files under LocalApplicationData unless a head overrides it.</summary>
    public static IKeyValueStore Store => _store ??= new FileKeyValueStore();

    public static CardLibrary Cards => field ??= new CardLibrary(Store);

    public static DictionaryLibrary Dictionaries => field ??= new DictionaryLibrary(Store);

    /// <summary>Sharing a local device over the internet through the relay.</summary>
    public static DeviceSharingService Sharing => field ??= new DeviceSharingService(Device, WakeLock);

    public static FirmwareUpdateService Firmware => field ??= new FirmwareUpdateService(Device, _dfu);

    /// <summary>Scan sounds and vibration; the desktop player unless a head registers its own.</summary>
    public static IFeedbackPlayer Feedback => _feedback ??= new DesktopFeedbackPlayer();

    /// <summary>
    /// Called by each platform entry point with its native scanners, the bootloader
    /// locators for the transports firmware can be installed over (USB serial, BLE),
    /// and its sound and vibration player.
    /// </summary>
    public static void Register(IEnumerable<ITransportScanner> platformScanners, IKeyValueStore? store = null,
        IEnumerable<IDfuLocator>? dfu = null, IFeedbackPlayer? feedback = null,
        IHardnestedEngine? hardnested = null, IKeyRecoveryEngine? keyRecovery = null,
        WakeLockService? wakeLock = null)
    {
        _device = Build(platformScanners);
        if (store is not null) _store = store;
        _dfu = dfu?.ToList() ?? new List<IDfuLocator>();
        if (feedback is not null) _feedback = feedback;
        Hardnested = hardnested ?? new UnavailableHardnestedEngine();
        KeyRecovery = keyRecovery ?? new ManagedKeyRecoveryEngine();
        WakeLock = wakeLock ?? new WakeLockService();
    }

    private static DeviceService Build(IEnumerable<ITransportScanner> platformScanners)
    {
        var scanners = new List<ITransportScanner>(platformScanners) { new TcpScanner(), new RelayScanner() };
        return new DeviceService(scanners);
    }
}
