using System;

namespace ChameLab.Services;

/// <summary>
/// Closes the app for good where the platform would otherwise only hide it: from Android 12 an
/// unhandled Back on the start screen just moves the app to the background, keeping the process,
/// and with it the device connection, alive. Set by the platform head; null where Back already
/// ends the app or there is nothing to close.
/// </summary>
public static class PlatformExit
{
    public static Action? Quit { get; set; }
}
