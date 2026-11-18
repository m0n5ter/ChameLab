using System;

namespace ChameLab.Services;

/// <summary>
/// The app's own handling of system Back, for a platform head that receives Back itself instead
/// of through <c>TopLevel.BackRequested</c> (the web app's history guard). Returns true when the
/// app took the Back; false lets the platform go back, which leaves the app. Set by the main view.
/// </summary>
public static class SystemBack
{
    public static Func<bool>? Handler { get; set; }
}
