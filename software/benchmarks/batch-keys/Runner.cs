using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;

internal static class Runner
{
    public static async Task Run(ITransport transport, string output, int repetitions = 3, bool probe = false, string? caseFilter = null)
    {
        // Read-only RF benchmark. Restores the initial device mode; never writes card/slot memory.
        var endpoint = transport.DisplayName;
        if (repetitions is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(repetitions));
        var measured = new MeasuredTransport(transport);
        await using var connection = new ChameleonConnection(measured);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var previous = await client.GetDeviceModeAsync();
        var rows = new List<object>();
        var metadata = new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["utc"] = DateTimeOffset.UtcNow,
            ["framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        };
        metadata["initial_mode"] = previous.ToString();
        async Task Save() => await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { metadata, rows }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            metadata["firmware"] = await client.GetGitVersionAsync();
            metadata["ble_address"] = await client.GetAddressAsync();
            await client.SetDeviceModeAsync(DeviceMode.Reader);
            var tag = (await client.Hf14aScanAsync()).Single();
            var type = MifareClassic.FromSak(tag.Sak);
            var sectors = MifareClassic.SectorCount(type);
            metadata["sak"] = tag.Sak;
            metadata["sectors"] = sectors;
            metadata["ble_att_payload_bytes"] = transport.GetType().GetField("_mtuPayload", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(transport);
            metadata["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            var capabilities = (await client.GetCapabilitiesAsync()).ToArray();
            if (!capabilities.Contains((ushort)Command.Mf1CheckKeysOfSectors)) throw new InvalidOperationException("Firmware has no batch command.");
            Console.WriteLine(JsonSerializer.Serialize(metadata));
            if (probe) return;

            // The old dictionary workflow is still the unsupported-firmware fallback. Hide just 2012
            // in the client's cached capabilities, leaving the same real firmware/transport in use.
            var capabilitiesField = typeof(ChameleonClient).GetField("_capabilities", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rng = new Random(73919);
            var wrong = Enumerable.Range(0, 32).Select(_ => { var k = new byte[6]; rng.NextBytes(k); return k; }).ToArray();
            var working = Convert.FromHexString("FFFFFFFFFFFF");
            var hasDefault = await client.Mf1AuthAsync(0, MfKeyType.A, working);
            metadata["default_key_works_sector0A"] = hasDefault;
            var cases = new List<(string Name, int Sectors, byte[][] Keys)>
    {
        ("miss-onepair-32keys", 1, wrong),
        ("miss-1sector-16keys", 1, wrong[..16]),
        ("miss-16sectors-16keys", Math.Min(16, sectors), wrong[..16]),
        ("miss-fullcard-1key", sectors, [wrong[0]]),
        ("miss-fullcard-16keys", sectors, wrong[..16])
    };
            if (hasDefault)
            {
                cases.Add(("hit-first-fullcard-17keys", sectors, [working, .. wrong[..16]]));
                cases.Add(("hit-last-fullcard-17keys", sectors, [.. wrong[..16], working]));
            }
            foreach (var c in cases.Where(c => caseFilter is null || c.Name.Contains(caseFilter)))
            {
                // Warm up both paths outside measurement. Their cached capability query is excluded.
                foreach (var batch in new[] { false, true })
                {
                    capabilitiesField.SetValue(client, batch ? capabilities : capabilities.Where(v => v != (ushort)Command.Mf1CheckKeysOfSectors).ToArray());
                    var warm = new MifareDump(type, tag.Uid, tag.Atqa, tag.Sak);
                    for (var s = 1; s < sectors; s++) { warm.KeyA[s] = working; warm.KeyB[s] = working; }
                    await client.FindMifareKeysAsync(warm, [wrong[0]]);
                }
                for (var repetition = 0; repetition < repetitions; repetition++)
                    foreach (var batch in repetition % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    {
                        capabilitiesField.SetValue(client, batch ? capabilities : capabilities.Where(v => v != (ushort)Command.Mf1CheckKeysOfSectors).ToArray());
                        // Pre-filled sectors outside the target must not supply preferred keys. For subsets
                        // use a matching card geometry (Mini isn't 1 sector), so measure pairs directly below.
                        int found;
                        measured.Reset();
                        var watch = Stopwatch.StartNew();
                        if (c.Sectors == sectors)
                        {
                            var dump = new MifareDump(type, tag.Uid, tag.Atqa, tag.Sak);
                            await client.FindMifareKeysAsync(dump, c.Keys);
                            found = dump.KeyA.Concat(dump.KeyB).Count(k => k is not null);
                        }
                        else
                        {
                            var wanted = Enumerable.Range(0, c.Sectors).SelectMany(s => c.Name.Contains("onepair")
                                ? new[] { (s, MfKeyType.A) } : new[] { (s, MfKeyType.A), (s, MfKeyType.B) }).ToArray();
                            found = 0;
                            if (batch) found = (await client.CheckMifareKeysOfSectorsAsync(wanted, c.Keys)).Count;
                            else foreach (var (s, kt) in wanted)
                                foreach (var k in c.Keys)
                                    if (await client.Mf1AuthAsync(MifareClassic.SectorFirstBlock(s), kt, k)) { found++; break; }
                        }
                        watch.Stop();
                        var row = new
                        {
                            @case = c.Name,
                            batch,
                            repetition,
                            seconds = watch.Elapsed.TotalSeconds,
                            found,
                            sectors = c.Sectors,
                            dictionary_keys = c.Keys.Length,
                            miss_attempts = c.Sectors * (c.Name.Contains("onepair") ? 1 : 2) * c.Keys.Length,
                            commands = measured.Commands,
                            tx_bytes = measured.TxBytes,
                            rx_bytes = measured.RxBytes,
                            auth_commands = measured.AuthCommands,
                            batch_commands = measured.BatchCommands,
                            batch_sizes = measured.BatchSizes.ToArray(),
                            statuses = measured.Statuses.ToDictionary()
                        };
                        if (measured.Statuses.Keys.Any(s => s is not (Status.Success or Status.HfTagOk or Status.MfErrAuth)))
                            throw new IOException("Unexpected device status; refusing to benchmark RF failures.");
                        rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row)); await Save();
                    }
            }
        }
        finally
        {
            try { await client.SetDeviceModeAsync(previous); }
            finally { await Save(); }
        }
    }
}
