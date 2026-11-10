using ChameLab.Transport.Serial;

var endpoint = args.ElementAtOrDefault(0) ?? "COM8";
await Runner.Run(new SerialTransport(endpoint), args.ElementAtOrDefault(1) ?? "results.json",
    int.Parse(args.ElementAtOrDefault(2) ?? "3"), args.Contains("--probe"),
    args.ElementAtOrDefault(3) is { } filter && filter != "--probe" ? filter : null);
