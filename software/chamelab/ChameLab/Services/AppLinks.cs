using System;

namespace ChameLab.Services;

/// <summary>
/// Links the app was opened with (the web app's <c>#card=</c> of a shared card). A head sets
/// <see cref="Pending"/> before the UI exists, or calls <see cref="Open"/> while it runs.
/// </summary>
public static class AppLinks
{
    private static string? _pending;
    private static Action<string>? _handler;

    /// <summary>A link from start-up, handed to the main view model once it subscribes.</summary>
    public static string? Pending
    {
        get => _pending;
        set => _pending = value;
    }

    /// <summary>Takes links: the pending one right away, later ones as they come.</summary>
    public static void Subscribe(Action<string> handler)
    {
        _handler = handler;
        if (_pending is { } link)
        {
            _pending = null;
            handler(link);
        }
    }

    public static void Open(string link)
    {
        if (_handler is { } handler) handler(link);
        else _pending = link;
    }
}
