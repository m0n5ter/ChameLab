using System.Diagnostics;
using ChameLab.Crypto;
using ChameLab.Services;

uint uid = 0x12345678, random = 0x9325abcd;
ulong key = args.Length > 1 ? Convert.ToUInt64(args[1], 16) : 0x112233445566;
int count = args.Length > 0 ? int.Parse(args[0]) : 12000;
var samples = new HardnestedSample[count];
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
var watch = Stopwatch.StartNew();
var progress = new Reporter(watch);
ulong? result = ManagedHardnestedEngine.Recover(new(uid, samples), progress);
Console.WriteLine($"Key={result:X12}; expected={key:X12}; elapsed={watch.Elapsed.TotalSeconds:F3}s; peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1}MiB");
return result == key ? 0 : 1;

sealed class Reporter(Stopwatch watch) : IProgress<string>
{
    public void Report(string value) => Console.WriteLine($"{watch.Elapsed.TotalSeconds,8:F3}s {value}");
}
