using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Services;

/// <summary>What the scanner does after a code was read: keep scanning with a new status line, or close.</summary>
/// <param name="Done">Close the scanner.</param>
/// <param name="Status">The line shown over the camera view (e.g. which parts are still missing).</param>
public readonly record struct QrScanStep(bool Done, string Status);

/// <summary>The camera can't be used (no camera, permission denied, scanner missing); the message says why.</summary>
public sealed class QrCameraUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A platform's camera QR scanner (phones and the browser).</summary>
public interface IQrCamera
{
    /// <summary>
    /// Opens the camera and hands each code read to <paramref name="onCode"/> (once per distinct text in a row)
    /// until it returns <see cref="QrScanStep.Done"/>, the user closes the scanner, or <paramref name="ct"/> fires.
    /// </summary>
    /// <param name="status">The line shown before the first code.</param>
    /// <returns>False when the user closed the scanner before it was done.</returns>
    /// <exception cref="QrCameraUnavailableException">The camera can't be used.</exception>
    Task<bool> ScanAsync(string status, Func<string, QrScanStep> onCode, CancellationToken ct = default);
}
