using System.Diagnostics;
using System.Text.Json;
using ChameLab.Protocol;
using ChameLab.Transport;
using ChameLab.Mobile;

internal static class LfProbe
{
    public static async Task Flash(Guid id, string packagePath, bool alreadyInBootloader)
    {
        var package = DfuPackage.Parse(await File.ReadAllBytesAsync(packagePath));
        var target = new BleDfuLocator().Prepare(new TransportCandidate(TransportKind.Ble, id.ToString(), "LF test device"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        if (!alreadyInBootloader)
        {
            await using var connection = new ChameleonConnection(new BleTransport(id, "LF test device"));
            await connection.OpenAsync(timeout.Token);
            new ChameleonClient(connection).EnterBootloader();
            await Task.Delay(500, timeout.Token);
        }
        IDfuChannel? channel = null;
        for (var attempt = 0; attempt < 5 && channel is null; attempt++)
            channel = await target.TryOpenAsync(timeout.Token);
        if (channel is null) throw new IOException("Device bootloader was not found");
        await using (channel)
            await new SecureDfu(channel).FlashAsync(package,
                new Progress<double>(f => Console.WriteLine($"DFU_PROGRESS {f:P0}")), timeout.Token);
        Console.WriteLine("DFU_COMPLETE");
    }

    // Preserve raw firmware responses so RF classification and transport parsing
    // can be diagnosed independently. Card and slot memory are never written.
    public static async Task Run(ITransport transport, string output, int repetitions)
    {
        if (repetitions is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(repetitions));
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var previous = await client.GetDeviceModeAsync();
        var metadata = new { firmware = await client.GetGitVersionAsync(), address = await client.GetAddressAsync(),
            utc = DateTimeOffset.UtcNow, initialMode = previous.ToString() };
        var rows = new List<object>();
        async Task Save() => await File.WriteAllTextAsync(output,
            JsonSerializer.Serialize(new { metadata, rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(metadata));
        try
        {
            await client.SetDeviceModeAsync(DeviceMode.Reader);
            for (var i = 0; i < repetitions; i++)
            {
                var command = i % 2 == 0 ? Command.Em410xScan : Command.LfAutoScan;
                var watch = Stopwatch.StartNew();
                var response = await connection.SendAsync(command, timeoutMs: 8000);
                var row = new { iteration = i, command = (ushort)command, status = response.Status.ToString(),
                    milliseconds = watch.ElapsedMilliseconds, data = Convert.ToHexString(response.Data) };
                rows.Add(row);
                Console.WriteLine(JsonSerializer.Serialize(row));
                await Save();
                if (response.Status != Status.LfTagOk || response.Data.Length != 15)
                {
                    for (byte half = 2; half <= 3; half++)
                    {
                        var capture = await connection.SendAsync((Command)3033, [half], timeoutMs: 8000);
                        rows.Add(new { failedIteration = i, dmaHalf = half - 2,
                            status = capture.Status.ToString(), data = Convert.ToHexString(capture.Data) });
                        await Save();
                    }
                    var diagnostic = await connection.SendAsync((Command)3033, [4], timeoutMs: 8000);
                    rows.Add(new { failedIteration = i, dmaDiagnostic = true,
                        status = diagnostic.Status.ToString(), data = Convert.ToHexString(diagnostic.Data) });
                    await Save();
                }
            }
            for (var i = 0; i < 5; i++)
            {
                var response = await connection.SendAsync((Command)3033, timeoutMs: 8000);
                rows.Add(new { capture = i, status = response.Status.ToString(), data = Convert.ToHexString(response.Data) });
                await Save();
            }
            var captureWatch = Stopwatch.StartNew();
            var samples = await connection.SendAsync((Command)3033, [1], timeoutMs: 8000);
            rows.Add(new { dmaCapture = true, status = samples.Status.ToString(),
                milliseconds = captureWatch.ElapsedMilliseconds, data = Convert.ToHexString(samples.Data) });
            await Save();
        }
        finally
        {
            try { await client.SetDeviceModeAsync(previous); }
            finally { await Save(); }
        }
    }
}
