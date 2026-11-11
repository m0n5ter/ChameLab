using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;

namespace ChameLab.Transport;

/// <summary>
/// Reaches the Chameleon's bootloader once the device has restarted into DFU mode,
/// for devices connected over <see cref="Kind"/>: USB serial on the desktop, BLE on
/// phones. Where no locator is registered firmware can't be installed.
/// </summary>
public interface IDfuLocator
{
    TransportKind Kind { get; }

    /// <summary>
    /// Called before the device restarts, while it is still connected (if it is at all),
    /// to remember what tells its bootloader apart from other devices.
    /// </summary>
    IDfuTarget Prepare(TransportCandidate? device);
}

/// <summary>One device's bootloader, to be found and opened after the restart.</summary>
public interface IDfuTarget
{
    /// <summary>Opens the bootloader, or returns null while it hasn't shown up yet.</summary>
    Task<IDfuChannel?> TryOpenAsync(CancellationToken ct);
}

/// <summary>
/// A bootloader the app reaches only once the user picks it, as in a browser: a page may open
/// a new USB or Bluetooth device only after it was chosen in the browser's own device chooser.
/// The bootloader is a different device from the application, so it needs a pick of its own
/// (once, where the browser remembers the permission).
/// </summary>
public interface IDfuPickTarget : IDfuTarget
{
    /// <summary>Opens the browser's chooser for the bootloader; must run in response to a click.</summary>
    Task PickAsync(CancellationToken ct);
}
