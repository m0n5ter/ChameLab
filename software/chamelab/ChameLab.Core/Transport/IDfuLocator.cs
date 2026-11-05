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
