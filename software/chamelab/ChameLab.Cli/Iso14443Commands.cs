using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Crypto;
using ChameLab.Protocol;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private partial void RegisterIso14443()
    {
        Add("hf seos eview", async (_, ct) =>
        {
            var (fields, hash, encryption) = await SeosData(ct);
            foreach (var (name, value) in fields) Print($"{name}: {Convert.ToHexString(value)}");
            Print($"Hash: {hash}; encryption: {encryption}");
        });
        Add("hf seos eload", async (a, ct) =>
        {
            var (fields, hash, encryption) = await SeosData(ct);
            foreach (var name in fields.Keys.ToArray()) if (a.Has(name)) fields[name] = a.Hex(name);
            Range(fields["data"].Length, 2, 255, "data length"); Range(fields["oid"].Length, 1, 32, "OID length");
            Range(fields["tag"].Length, 1, 2, "tag length"); Range(fields["diversifier"].Length, 1, 16, "diversifier length");
            _ = BerTlv.Read(fields["data"]);
            var payload = fields.Values.SelectMany(b => new[] { B(b.Length) }.Concat(b)).Concat(new[] { hash, encryption }).ToArray();
            await UpdateIdentity(a, ct); await Wire(4043, payload, ct);
        });
        Add("hf seos keys", async (a, ct) => { await Wire(4044, [.. a.Hex("auth", 16), .. a.Hex("privenc", 16), .. a.Hex("privmac", 16)], ct); });
        Add("emv scan", EmvScanAsync);
        Add("emv load", EmvLoadAsync);
        Add("emv debug", async (_, ct) =>
        {
            var b = await Wire(6010, null, ct);
            if (b.Length < 4) throw new InvalidDataException("Invalid debug response.");
            Print($"I-blocks received: {b[0]}; sent: {b[1]}; PCB: {b[2]:X2}; static match: {b[3] != 0}");
        });
        Add("emv apdu", async (a, ct) =>
        {
            if (Console.IsInputRedirected) throw new InvalidOperationException("Manual APDU responses require an interactive terminal.");
            var timeout = Range(a.Int("timeout"), 1000, 60000, "timeout");
            while (true)
            {
                var watch = Stopwatch.StartNew(); byte[]? request = null;
                while (watch.ElapsedMilliseconds < timeout)
                {
                    var f = await Client.Connection.SendAsync((Command)6000, ct: ct);
                    if (f.Status == Status.Success) { request = f.Data; break; }
                    if (f.Status != Status.HfTagNo) throw new ChameleonException(f.Status.Describe(), f.Status);
                    await Task.Delay(20, ct);
                }
                if (request is null) { Print("No APDU received within timeout."); return; }
                Print("Reader: " + Convert.ToHexString(request)); Output.Write("Response (hex, or quit): ");
                var line = await Console.In.ReadLineAsync(ct);
                if (line is null || line.Trim() is "quit" or "exit") return;
                var response = HexText(line); await Wire(6001, [.. U16(response.Length), .. response], ct);
            }
        });
        Add("hf des info", async (_, ct) =>
        {
            var version = await DesExchange(0x60, [], ct);
            Print("Hardware: " + Convert.ToHexString(version[..^1]));
            if (version[^1] == 0xAF)
            {
                var sw = await DesExchange(0xAF, [], ct); Print("Software: " + Convert.ToHexString(sw[..^1]));
                if (sw[^1] == 0xAF) Print("Manufacturing: " + Convert.ToHexString((await DesExchange(0xAF, [], ct))[..^1]));
            }
            foreach (var aid in await DesApplications(ct))
            {
                Print("AID: " + Convert.ToHexString(aid));
                var selected = await DesExchange(0x5A, aid, ct);
                if (selected[^1] == 0) Print("Key settings: " + Convert.ToHexString(await DesExchange(0x45, [], ct)));
            }
        });
        Add("hf des chk", DesCheckAsync);
    }

    private async Task<(Dictionary<string, byte[]> Fields, byte Hash, byte Encryption)> SeosData(CancellationToken ct)
    {
        var active = await Client.GetActiveSlotAsync(ct);
        if ((await Client.GetAllSlotsAsync(ct)).Single(s => s.Index == active).HfType != TagSpecificType.Seos)
            throw new InvalidOperationException("The active slot is not SEOS.");
        var r = new PacketReader(await Wire(4042, null, ct));
        var fields = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "data", "oid", "tag", "diversifier" }) fields[name] = r.Take(r.Byte());
        return (fields, r.Byte(), r.Byte());
    }

    private async Task EmvScanAsync(CliArguments a, CancellationToken ct)
    {
        var mode = await Client.GetDeviceModeAsync(ct);
        try
        {
            await Client.SetDeviceModeAsync(DeviceMode.Reader, ct);
            var capture = EmvCapture.Decode(await Wire(6005, null, ct, 15000));
            var document = capture.ToJson(); var text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            Print(text);
            if (!string.IsNullOrEmpty(a.Get("file"))) await File.WriteAllTextAsync(a.Text("file"), text, ct);
            if (a.Has("slot")) await InstallEmv(capture, Range(a.Int("slot"), 1, 8, "slot"), ct);
        }
        finally { if (Connected) await Client.SetDeviceModeAsync(mode, CancellationToken.None); }
    }

    private async Task EmvLoadAsync(CliArguments a, CancellationToken ct)
    {
        if (a.Flag("clear")) { await Wire(6003, [0], ct); return; }
        if (a.Has("cmd") || a.Has("resp"))
        {
            if (!a.Has("cmd") || !a.Has("resp")) throw new ArgumentException("Provide both --cmd and --resp.");
            await AddApduPair(HexText(a.Text("cmd")), HexText(a.Text("resp")), ct); return;
        }
        if (a.Flag("defaults"))
        {
            await Wire(6003, [0], ct);
            foreach (var pair in new[] {
                ("00A404000E325041592E5359532E4444463031", "6F23840E325041592E5359532E4444463031A511BF0C0E610C4F07A00000000410108701019000"),
                ("00A4040007A0000000041010", "6F1D8407A0000000041010A512500A4D6173746572436172648701019F38009000"),
                ("80A80000", "6985") }) await AddApduPair(HexText(pair.Item1), HexText(pair.Item2), ct);
            return;
        }
        if (string.IsNullOrEmpty(a.Get("file"))) throw new ArgumentException("Specify --file, --cmd/--resp, --clear or --defaults.");
        var capture = EmvCapture.FromJson(JsonNode.Parse(await File.ReadAllTextAsync(a.Text("file"), ct))!.AsObject());
        await InstallEmv(capture, await Slot(a, ct), ct);
    }
    private async Task InstallEmv(EmvCapture capture, int slot, CancellationToken ct)
    {
        var previous = await Client.GetActiveSlotAsync(ct);
        try
        {
            await Client.SetActiveSlotAsync(slot, ct);
            await Client.SetSlotTagTypeAsync(slot, TagSpecificType.Hf14a4, ct);
            await Client.SetSlotDataDefaultAsync(slot, TagSpecificType.Hf14a4, ct);
            await Client.SetSlotEnableAsync(slot, TagSenseType.Hf, true, ct);
            await Wire(6002, [B(capture.Uid.Length), .. capture.Uid, .. capture.Atqa, capture.Sak, B(capture.Ats.Length), .. capture.Ats], ct);
            await Wire(6003, [0], ct);
            foreach (var pair in capture.Pairs) await AddApduPair(pair.Command, pair.Response, ct);
            await Client.SaveSlotConfigAsync(ct);
            Print($"Slot {slot}: installed {capture.Pairs.Count} APDU responses.");
        }
        finally { if (Connected) await Client.SetActiveSlotAsync(previous, CancellationToken.None); }
    }
    private async Task AddApduPair(byte[] command, byte[] response, CancellationToken ct)
    {
        Range(command.Length, 1, 255, "APDU command length"); Range(response.Length, 2, 4096 - command.Length - 3, "APDU response length");
        await Wire(6003, [B(command.Length), .. command, .. U16(response.Length), .. response], ct);
    }
    internal static byte[] HexText(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    private async Task<byte[]> DesExchange(byte ins, byte[] data, CancellationToken ct)
    {
        var apdu = data.Length == 0 ? new byte[] { 0x90, ins, 0, 0, 0 } : [0x90, ins, 0, 0, B(data.Length), .. data, 0];
        var reply = await Wire(6004, apdu, ct);
        if (reply.Length < 2 || reply[^2] != 0x91) throw new InvalidDataException("Invalid DESFire response: " + Convert.ToHexString(reply));
        return [.. reply[..^2], reply[^1]];
    }
    private async Task<List<byte[]>> DesApplications(CancellationToken ct)
    {
        var result = new List<byte[]>(); var response = await DesExchange(0x6A, [], ct);
        for (var frame = 0; frame < 128; frame++)
        {
            for (var i = 0; i + 3 <= response.Length - 1; i += 3) result.Add(response[i..(i + 3)]);
            if (response[^1] != 0xAF) return result;
            response = await DesExchange(0xAF, [], ct);
        }
        throw new InvalidDataException("Too many DESFire continuation frames.");
    }
    private async Task DesCheckAsync(CliArguments a, CancellationToken ct)
    {
        Range(a.Int("keyno"), 0, 13, "keyno");
        var deadline = Stopwatch.StartNew(); var timeout = Range(a.Int("timeout"), 1, 60000, "timeout");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { if ((await Client.Hf14aScanAsync(ct)).Count > 0) break; }
            catch (ChameleonException ex) when (ex.Status == Status.HfTagNo && deadline.ElapsedMilliseconds < timeout) { }
            if (deadline.ElapsedMilliseconds >= timeout) throw new TimeoutException("No card found.");
            await Task.Delay(100, ct);
        }
        var aids = a.Has("aid") ? new List<byte[]> { a.Hex("aid", 3) } : new List<byte[]> { new byte[3] };
        if (!a.Has("aid")) aids.AddRange(await DesApplications(ct));
        var keys = new List<byte[]>();
        if (a.Has("key")) keys.Add(HexText(a.Text("key")));
        else
        {
            using var s = typeof(CliSession).Assembly.GetManifestResourceStream("ChameLab.Cli.DesfireKeys.json")!;
            keys.AddRange(JsonSerializer.Deserialize<string[]>(s)!.Select(HexText));
            if (a.Has("file")) keys.AddRange(File.ReadLines(a.Text("file")).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).Select(HexText));
            if (a.Flag("pattern1b")) for (var b = 0; b < 256; b++) { keys.Add(Enumerable.Repeat((byte)b, 8).ToArray()); keys.Add(Enumerable.Repeat((byte)b, 16).ToArray()); }
            if (a.Flag("pattern2b")) for (var n = 0; n < 65536; n++) keys.Add(Enumerable.Range(0, 16).Select(i => (byte)(i % 2 == 0 ? n >> 8 : n & 255)).ToArray());
        }
        if (keys.Any(k => k.Length is not (8 or 16 or 24))) throw new ArgumentException("DESFire keys must contain 8, 16 or 24 bytes.");
        keys = keys.DistinctBy(Convert.ToHexString).ToList();
        foreach (var aid in aids)
        {
            var found = false;
            foreach (var key in keys)
            {
                foreach (var aes in key.Length == 16 ? new[] { true, false } : new[] { false })
                {
                    ct.ThrowIfCancellationRequested();
                    if ((await DesExchange(0x5A, aid, ct))[^1] != 0) throw new IOException("Could not select application.");
                    if (await DesAuthenticate(key, B(a.Int("keyno")), aes, ct))
                    { Print($"AID {Convert.ToHexString(aid)} {(aes ? "AES" : "DES/3DES")}: {Convert.ToHexString(key)}"); found = true; break; }
                }
                if (found) break;
            }
            if (!found) Print($"AID {Convert.ToHexString(aid)}: no key found.");
        }
    }
    private async Task<bool> DesAuthenticate(byte[] key, byte keyNumber, bool aes, CancellationToken ct)
    {
        var ins = aes ? (byte)0xAA : key.Length == 24 ? (byte)0x1A : (byte)0x0A;
        var first = await DesExchange(ins, [keyNumber], ct);
        if (first[^1] != 0xAF) return false;
        var encB = first[..^1]; var block = aes ? 16 : 8;
        if (encB.Length != block) throw new InvalidDataException("Invalid encrypted nonce length.");
        var b = CardCipher.Cbc(false, aes, key, new byte[block], encB);
        var rndA = RandomNumberGenerator.GetBytes(block);
        var encrypted = CardCipher.Cbc(true, aes, key, encB, [.. rndA, .. b[1..], b[0]]);
        var response = await DesExchange(0xAF, encrypted, ct);
        if (response[^1] != 0 || response.Length != block + 1) return false;
        var verified = CardCipher.Cbc(false, aes, key, encrypted[^block..], response[..^1]);
        return CryptographicOperations.FixedTimeEquals(verified, [.. rndA[1..], rndA[0]]);
    }
}
