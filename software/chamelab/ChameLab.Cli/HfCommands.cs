using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Crypto;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private partial void RegisterHf()
    {
        Add("hf 14a scan", async (_, ct) =>
        {
            foreach (var t in await Client.Hf14aScanAsync(ct)) Print($"UID: {t.UidHex}; ATQA: {t.AtqaHex}; SAK: {t.SakHex}; ATS: {t.AtsHex}");
        });
        // Both names have referred to sniffing in the reference command tree.
        foreach (var name in new[] { "hf 14a info", "hf 14a sniff" })
            Add(name, async (a, ct) => PrintHf(await Client.Hf14aSniffAsync(Range(a.Int("timeout"), 1, 30000, "timeout"), ct)));
        Add("hf 14a auth-trace", async (a, ct) =>
        {
            var timeout = Range(a.Int("timeout"), 1, 30000, "timeout");
            var key = a.Hex("key", 6);
            var response = await Client.Connection.SendAsync((Command)2017,
                [(byte)KeyType(a), B(a.Int("blk")), .. key, .. U16(timeout)], timeoutMs: timeout + 3000, ct: ct);
            // Failed authentication still carries useful partial frames. Auth-trace bytes already have parity removed.
            PrintHf(response.Data, includesParity: false, knownKey: Convert.ToUInt64(Convert.ToHexString(key), 16));
            if (!response.Status.IsOk()) throw new ChameleonException(response.Status.Describe(), response.Status);
        });
        Add("hf 14a config", async (a, ct) =>
        {
            var names = new[] { "bcc", "cl2", "cl3", "rats" };
            var config = a.Flag("std") ? new byte[4] : await Wire(2200, null, ct);
            if (config.Length != 4) throw new InvalidDataException("Invalid ISO14443 configuration response.");
            for (var i = 0; i < names.Length; i++)
                if (a.Has(names[i])) config[i] = a.Text(names[i]) switch { "std" => 0, "fix" or "force" => 1, _ => 2 };
            if (a.Flag("std") || names.Any(a.Has)) await Wire(2201, config, ct);
            for (var i = 0; i < names.Length; i++) Print($"{names[i]}: {config[i]}");
        });
        Add("hf 14a raw", async (a, ct) =>
        {
            var data = a.Hex("data"); var bits = a.Int("bits", data.Length * 8);
            if (a.Has("bits") && (data.Length == 0 || bits <= (data.Length - 1) * 8 || bits > data.Length * 8))
                throw new ArgumentException("Bit count must describe the supplied data including its last byte.");
            var flags = (a.Flag("activate_rf") ? 128 : 0) | (!a.Flag("no_response") ? 64 : 0) |
                (a.Flag("crc") ? 32 : 0) | (a.Flag("select_tag") ? 16 : 0) | (a.Flag("keep_rf") ? 8 : 0) | (a.Flag("crc_clear") ? 4 : 0);
            var timeout = Range(a.Int("timeout"), 0, ushort.MaxValue, "timeout");
            Print(await Wire(2010, [B(flags), .. U16(timeout), .. U16(bits), .. data], ct, timeout + 2000));
        });
    }

    private void PrintHf(byte[] buffer, bool includesParity = true, ulong? knownKey = null)
    {
        var frames = HfSniff.ParseChameleon(buffer, includesParity);
        foreach (var f in HfSniff.Annotate(frames)) Print($"{f.Index,4} {f.Frame.Direction,-6} {f.Frame.Hex,-40} {f.Label}");
        if (knownKey is { } key)
        {
            foreach (var auth in HfSniff.AuthExchanges(frames))
            {
                var state = new Crypto1State(key);
                state.Crypto1Word(auth.Uid ^ auth.Nt);
                var nr = auth.NrEnc ^ state.Crypto1Word(auth.NrEnc, true);
                var ar = auth.ArEnc ^ state.Crypto1Word();
                Print($"UID={auth.Uid:X8} NT={auth.Nt:X8} NR={nr:X8}");
                Print($"AR={ar:X8}; expected={Crypto1.PrngSuccessor(auth.Nt, 64):X8}; {(ar == Crypto1.PrngSuccessor(auth.Nt, 64) ? "MATCH" : "MISMATCH")}");
                if (auth.AtEnc is { } encryptedAt)
                {
                    var at = encryptedAt ^ state.Crypto1Word();
                    Print($"AT={at:X8}; expected={Crypto1.PrngSuccessor(auth.Nt, 96):X8}; {(at == Crypto1.PrngSuccessor(auth.Nt, 96) ? "MATCH" : "MISMATCH")}");
                }
                else Print("AT not received; authentication was not completed.");
            }
            return;
        }
        foreach (var group in HfSniff.GroupAuths(HfSniff.AuthExchanges(frames)))
            if (Mfkey32Recovery.Recover(group) is { } result) Print($"Block {group.Block}, key {group.KeyChar}: {result.Key:X12} ({result.Method})");
    }

    private Task<byte[]> RawHf(byte[] data, CancellationToken ct, bool select = true, bool keep = false, int timeout = 1000)
        => Wire(2010, [B(0x64 | (select ? 0x10 : 0) | (keep ? 8 : 0)), .. U16(timeout), .. U16(data.Length * 8), .. data], ct, timeout + 2000);

    private async Task UpdateIdentity(CliArguments a, CancellationToken ct)
    {
        if (!new[] { "uid", "atqa", "sak", "ats", "delete_ats" }.Any(a.Has)) return;
        var old = await Client.GetHf14aEmulatorIdentityAsync(ct);
        var uid = a.Has("uid") ? a.Hex("uid") : old.Uid;
        if (uid.Length is not (4 or 7 or 10)) throw new ArgumentException("UID must contain 4, 7 or 10 bytes.");
        await Client.Hf14aSetAntiCollDataAsync(uid, a.Has("atqa") ? a.Hex("atqa", 2) : old.Atqa,
            a.Has("sak") ? a.Hex("sak", 1)[0] : old.Sak,
            a.Flag("delete_ats") ? [] : a.Has("ats") ? a.Hex("ats") : old.Ats, ct);
    }
}
