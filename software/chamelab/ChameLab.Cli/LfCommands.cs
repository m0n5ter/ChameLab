using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private static readonly (string Path, TagSpecificType Type, int Scan, int Write)[] LfFamilies =
    [
        ("em 410x", TagSpecificType.Em410x, 3000, 3001),
        ("hid prox", TagSpecificType.HidProx, 3002, 3003),
        ("ioprox", TagSpecificType.IoProx, 3010, 3011),
        ("pac", TagSpecificType.Pac, 3014, 3015),
        ("viking", TagSpecificType.Viking, 3004, 3005),
        ("jablotron", TagSpecificType.Jablotron, 3019, 3020),
        ("idteck", TagSpecificType.Idteck, 0, 3018),
        ("pyramid", TagSpecificType.FarpointePyramid, 3021, 0)
    ];

    private partial void RegisterLf()
    {
        foreach (var family in LfFamilies)
        {
            if (family.Scan != 0) Add($"lf {family.Path} read", async (a, ct) =>
            {
                if (family.Type == TagSpecificType.Em410x)
                {
                    var card = await Client.Em410xScanAsync(ct); Print($"{card.Type}: {card.IdHex}");
                }
                else
                {
                    var bytes = await Wire(family.Scan, family.Type == TagSpecificType.HidProx ? [B(a.Has("format") ? CommandCatalog.EnumValue("HIDFormat", a.Text("format")) : 0)] : null, ct, 4000);
                    PrintLf(family.Type, bytes);
                }
            });
            if (family.Write != 0) Add($"lf {family.Path} write", async (a, ct) => await WriteLf(family.Type, family.Write, await LfId(family.Type, a, ct), ct));
            Add($"lf {family.Path} econfig", async (a, ct) =>
            {
                if (new[] { "id", "cn", "raw", "raw8", "fc", "ver" }.Any(a.Has))
                    await Client.SetLfEmulatorIdAsync(family.Type, await LfId(family.Type, a, ct), ct);
                PrintLf(family.Type, await Client.GetLfEmulatorIdAsync(family.Type, ct));
            });
        }
        Add("lf em 4x05 read", async (_, ct) => Print(await Wire(3030, U32(0), ct, 5000)));
        Add("lf generic adcread", async (_, ct) => Print(await Wire(3009, null, ct)));
        Add("lf clone", async (a, ct) =>
        {
            var path = a.Text("type") switch { "em410x" or "electra" => "em 410x", "hid" => "hid prox", var p => p };
            var f = LfFamilies.Single(x => x.Path == path);
            var electra = a.Text("type") == "electra";
            var type = electra ? TagSpecificType.Em410xElectra : f.Type;
            await WriteLf(type, electra ? 3006 : f.Write, await LfId(type, a, ct), ct);
        });
        Add("lf sniff", async (a, ct) =>
        {
            _lfCapture = await Client.LfSniffAsync(Range(a.Int("timeout"), 1, 10000, "timeout"), ct);
            Json(LfSniff.Summarize(_lfCapture));
            if (a.Has("out")) await File.WriteAllBytesAsync(a.Text("out"), _lfCapture, ct);
            if (a.Flag("hex")) Dump(_lfCapture, 16);
        });
        Add("data hexsamples", (a, _) => { Dump(Capture().Take(Range(a.Int("num"), 1, int.MaxValue, "num")).ToArray(), 16); return Task.CompletedTask; });
        Add("data modulation", (_, _) => { Json(LfSniff.DetectModulation(Capture())); return Task.CompletedTask; });
        Add("data manrawdecode", (a, _) =>
        {
            var d = LfSniff.DecodeManchester(Capture(), Range(a.Int("clock"), 2, 65535, "clock"), a.Flag("invert"));
            Print(d.BitString); Print(d.Hex); Print($"Errors: {d.Errors}"); return Task.CompletedTask;
        });
        Add("data plot", PlotLfAsync);
    }

    private byte[] Capture() => _lfCapture ?? throw new InvalidOperationException("No capture in buffer — run lf sniff first.");
    private void Dump(byte[] data, int width)
    {
        for (var i = 0; i < data.Length; i += width)
        {
            var row = data.AsSpan(i, Math.Min(width, data.Length - i));
            Print($"{i / width,4} | {Convert.ToHexString(row)} | {new string(row.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray())}");
        }
    }

    private async Task WriteLf(TagSpecificType type, int command, byte[] id, CancellationToken ct)
    {
        var expected = type.LfIdLength();
        if (id.Length != expected) throw new ArgumentException($"{type} requires {expected} ID bytes.");
        await Wire(command, [.. id, .. ChameleonClient.DefaultT55xxKey, .. ChameleonClient.DefaultT55xxOldKeys.SelectMany(k => k)], ct, 5000);
        Print("Write sent. T55xx does not acknowledge writes; scan the tag to verify it.");
    }

    private async Task<byte[]> LfId(TagSpecificType type, CliArguments a, CancellationToken ct)
    {
        switch (type)
        {
            case TagSpecificType.HidProx:
                var format = CommandCatalog.EnumValue("HIDFormat", a.Text("format", "H10301"));
                var cn = a.Long("cn"); if (cn < 0 || cn > 0xFFFFFFFFFFL) throw new ArgumentException("Invalid HID card number.");
                return [B(format), .. U32(checked((uint)a.Long("fc"))), B((int)(cn >> 32)), .. U32((uint)(cn & uint.MaxValue)), B(a.Int("il")), .. U16(a.Int("oem"))];
            case TagSpecificType.IoProx:
                if (a.Has("raw8")) return await Wire(3012, a.Hex("raw8", 8), ct);
                var fc = a.Text("fc", "0");
                var facility = fc.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(fc[2..], NumberStyles.HexNumber) : int.Parse(fc, CultureInfo.InvariantCulture);
                return await Wire(3013, [B(a.Int("ver")), B(facility), .. U16(a.Int("cn"))], ct);
            case TagSpecificType.Pac:
                if (a.Has("raw")) return DecodePac(a.Hex("raw", 16));
                var card = a.Text("cn", a.Text("id"));
                if (card.Length != 8 || card.Any(c => c > 127)) throw new ArgumentException("PAC card number must be eight ASCII characters.");
                return Encoding.ASCII.GetBytes(card);
            case TagSpecificType.FarpointePyramid:
                if (a.Has("raw")) return a.Hex("raw", 16);
                if (!a.Has("fc") || !a.Has("cn")) throw new ArgumentException("Provide both --fc and --cn, or --raw.");
                return PyramidFrame.Encode26(B(a.Int("fc")), checked((ushort)a.Int("cn")));
            case TagSpecificType.Idteck:
                var id = a.Hex("id"); if (id.Length == 4) id = [0x49, 0x44, 0x54, 0x4B, .. id];
                if (id.Length != 8) throw new ArgumentException("IDTECK ID must contain four or eight bytes.");
                return id;
            default: return a.Hex("id", type.LfIdLength());
        }
    }

    private void PrintLf(TagSpecificType type, byte[] id)
    {
        Print($"{type.DisplayName()}: {Convert.ToHexString(id)}");
        if (type == TagSpecificType.HidProx && id.Length >= 13)
            Print($"Format: {id[0]}; FC: {BinaryPrimitives.ReadUInt32BigEndian(id.AsSpan(1))}; CN: {((ulong)id[5] << 32) | BinaryPrimitives.ReadUInt32BigEndian(id.AsSpan(6))}; IL: {id[10]}; OEM: {BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(11))}");
        if (type == TagSpecificType.IoProx && id.Length >= 12)
            Print($"Version: {id[0]}; FC: {id[1]}; CN: {BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(2))}; Raw: {Convert.ToHexString(id.AsSpan(4, 8))}");
        if (type == TagSpecificType.Pac) Print("CN: " + Encoding.ASCII.GetString(id));
        if (type == TagSpecificType.FarpointePyramid) Print(PyramidFrame.Decode(id));
    }

    internal static byte[] DecodePac(byte[] raw)
    {
        if (raw.Length != 16 || raw[0] != 255) throw new ArgumentException("Invalid PAC sync marker.");
        int Bit(int pos) => (raw[pos >> 3] >> (7 - (pos & 7))) & 1;
        var decoded = new byte[12];
        for (var frame = 0; frame < 12; frame++)
        {
            var start = 8 + frame * 10; var ones = Bit(start + 8);
            if (Bit(start) != 0 || Bit(start + 9) != 1) throw new ArgumentException("Invalid PAC UART framing.");
            for (var i = 0; i < 7; i++) { var bit = Bit(start + 1 + i); decoded[frame] |= (byte)(bit << i); ones += bit; }
            if ((ones & 1) == 0) throw new ArgumentException("Invalid PAC parity.");
        }
        if (decoded[0] != 2 || decoded[1] != '2' || decoded[2] != '0' || decoded[3..11].Aggregate(0, (a, b) => a ^ b) != decoded[11])
            throw new ArgumentException("Invalid PAC header/checksum.");
        return decoded[3..11];
    }

    private async Task PlotLfAsync(CliArguments a, CancellationToken ct)
    {
        var capture = Capture(); var start = Range(a.Int("start"), 0, capture.Length - 1, "start");
        var samples = capture.Skip(start).Take(Range(a.Int("len"), 1, int.MaxValue, "len")).ToArray();
        if (!a.Flag("ascii")) { await WaveformPlot.ShowAsync(samples, start, ct); return; }
        const int columns = 64;
        var buckets = samples.Chunk(Math.Max(1, (samples.Length + columns - 1) / columns)).Select(c => c.Average(b => (double)b)).ToArray();
        for (var level = 224; level >= 0; level -= 32)
            Print($"{level:X2} |" + new string(buckets.Select(v => v >= level ? '#' : ' ').ToArray()));
        Print($"Samples {start}–{start + samples.Length}, 8 us/sample");
    }
}
