using System.Diagnostics;
using ChameLab.Crypto;
using ChameLab.Services;

uint uid;
ulong key;
HardnestedSample[] samples;
if (args.FirstOrDefault() == "--capture")
{
    if (args.Length != 4) throw new ArgumentException("Usage: --capture FILE UID_HEX EXPECTED_KEY_HEX");
    uid = Convert.ToUInt32(args[2], 16);
    key = Convert.ToUInt64(args[3], 16);
    samples = File.ReadLines(args[1]).Select(line =>
    {
        string[] fields = line.Split('|');
        return new HardnestedSample(uint.Parse(fields[0]), byte.Parse(fields[1]));
    }).ToArray();
}
else
{
    int count = args.Length > 0 ? int.Parse(args[0]) : 12000;
    key = args.Length > 1 ? Convert.ToUInt64(args[1], 16) : 0x112233445566;
    uid = args.Length > 3 ? Convert.ToUInt32(args[3], 16) : 0x12345678;
    uint random = args.Length > 4 ? Convert.ToUInt32(args[4], 16) : 0x9325abcd;
    if (count <= 0 || random == 0 || key > 0xffffffffffff)
        throw new ArgumentException("Use a positive sample count, nonzero xorshift seed and 48-bit key.");
    samples = new HardnestedSample[count];
    for (int i = 0; i < count; i++)
    {
        random ^= random << 13; random ^= random >> 17; random ^= random << 5;
        var state = new Crypto1State(key);
        uint encrypted = 0; byte parity = 0;
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte plain = (byte)(random >> shift);
            byte stream = state.Crypto1Byte((byte)(plain ^ (uid >> shift)));
            encrypted = (encrypted << 8) | (uint)(plain ^ stream);
            parity = (byte)((parity << 1) | (Crypto1.OddParity8(plain) ^ Crypto1.Filter(state.Odd)));
        }
        samples[i] = new(encrypted, parity);
    }
    if (args.Length > 2) File.WriteAllLines(args[2], samples.Select(s => $"{s.EncryptedNonce}|{s.EncryptedParity}"));
    if (args.Length > 5 && args[5] == "--generate-only") return 0;
}
Console.WriteLine($"UID={uid:X8}; samples={samples.Length}");
var watch = Stopwatch.StartNew();
var progress = new Reporter(watch);
ulong? result = ManagedHardnestedEngine.Recover(new(uid, samples), progress);
Console.WriteLine($"Key={result:X12}; expected={key:X12}; elapsed={watch.Elapsed.TotalSeconds:F3}s; peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1}MiB");
return result == key ? 0 : 1;

sealed class Reporter(Stopwatch watch) : IProgress<string>
{
    public void Report(string value) => Console.WriteLine($"{watch.Elapsed.TotalSeconds,8:F3}s {value}");
}
