using ChameLab.Services;
using ChameLab.Sync;
using ChameLab.Transport;

namespace ChameLab.Cli;

/// <summary>
/// <c>chamelab sync …</c>: a headless sync node for hardware that never sleeps (a Raspberry Pi, a
/// NAS, a VPS). It is a full member of the group with its own copy of the library on disk, and
/// keeps the group's room open so the user's phones always find a device that has everything.
/// </summary>
internal static class SyncCommand
{
    public const string Usage = """
        chamelab sync serve [--data DIR] [--name NAME] [--create | --join CODE]
            Runs a sync node until Ctrl+C. --create starts a group with this node; --join joins
            the group of the device showing CODE (Settings → Device sync → Add device there).
        chamelab sync invite [--data DIR]
            Shows a code for another device to join this node's group, and asks to confirm it.
        chamelab sync status [--data DIR]
        chamelab sync leave [--data DIR]
        DIR defaults to the app's data folder; give each node on one machine its own.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[1] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length < 2 ? 2 : 0;
        }
        string? data = null, name = null, join = null;
        var create = false;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length: data = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--join" when i + 1 < args.Length: join = args[++i]; break;
                case "--create": create = true; break;
                default:
                    Console.Error.WriteLine($"Unknown option {args[i]}.\n{Usage}");
                    return 2;
            }
        }

        var store = new FileKeyValueStore(data);
        AppServices.Register(Array.Empty<ITransportScanner>(), store: store);
        var cards = new CardLibrary(store);
        var dictionaries = new DictionaryLibrary(store);
        await using var sync = new SyncService(store, cards, dictionaries);
        sync.Changed += () => Console.WriteLine($"[{DateTime.Now:T}] {Describe(sync)}");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            switch (args[1])
            {
                case "status":
                    Console.WriteLine(sync.IsEnabled ? Describe(sync) : "Not in a sync group.");
                    foreach (var m in sync.Members)
                        Console.WriteLine($"  {m.Name}{(m.IsThis ? " (this node)" : "")}  {m.LastSeen?.ToLocalTime():g}");
                    Console.WriteLine($"  {cards.Cards.Count} cards, {dictionaries.Dictionaries.Count} dictionaries");
                    return 0;
                case "leave":
                    await sync.LeaveAsync();
                    Console.WriteLine("Left the sync group; the library stays.");
                    return 0;
                case "invite":
                    return await InviteAsync(sync, stop.Token);
                case "serve":
                    if (create && !sync.IsEnabled) sync.CreateGroup(name ?? SyncService.DefaultDeviceName);
                    if (join is not null && !sync.IsEnabled)
                    {
                        Console.WriteLine("Joining…");
                        await sync.JoinAsync(join, name ?? SyncService.DefaultDeviceName,
                            digits => Console.WriteLine($"Check that the other device shows {digits}, and add this node there."),
                            stop.Token);
                    }
                    if (!sync.IsEnabled)
                    {
                        Console.Error.WriteLine("Not in a sync group: use --create or --join CODE.");
                        return 2;
                    }
                    sync.Start();
                    Console.WriteLine($"Syncing as {sync.DeviceName}. Ctrl+C stops.");
                    await Task.Delay(Timeout.Infinite, stop.Token);
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException
                                       or System.Net.WebSockets.WebSocketException or TimeoutException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> InviteAsync(SyncService sync, CancellationToken ct)
    {
        if (!sync.IsEnabled)
        {
            Console.Error.WriteLine("Not in a sync group.");
            return 2;
        }
        sync.Start();
        await using var invitation = await sync.InviteAsync(ct);
        Console.WriteLine($"On the other device: Settings → Device sync → Join another device, code {invitation.TypedCode}");
        var request = await invitation.WaitAsync(ct);
        Console.Write(request.Digits is { } digits
            ? $"{request.Name} wants to join and shows {digits}? Add it [y/N] "
            : $"{request.Name} wants to join. Add it [y/N] ");
        if (Console.ReadLine()?.Trim().StartsWith('y') != true)
        {
            await invitation.RejectAsync();
            return 1;
        }
        await invitation.AcceptAsync(ct);
        Console.WriteLine("Added; syncing…");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        return 0;
    }

    private static string Describe(SyncService sync) => sync.Status switch
    {
        SyncStatus.Online => $"online, {sync.ConnectedCount} device(s) connected",
        SyncStatus.Offline => $"no relay reachable{(sync.Error is { } e ? $" ({e})" : "")}, {sync.ConnectedCount} device(s) on the local network",
        var other => other.ToString().ToLowerInvariant(),
    };
}
