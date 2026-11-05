using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ChameLab.Services;

/// <summary>How a scan ended, which picks the sound and the vibration pattern.</summary>
public enum ScanOutcome { Found, Missed }

/// <summary>
/// Plays the platform's sound and vibration. The desktop default lives here; the mobile and
/// browser heads register their own through <see cref="AppServices.Register"/>.
/// </summary>
public interface IFeedbackPlayer
{
    /// <summary>Whether the device can vibrate at all; the setting is hidden otherwise.</summary>
    bool CanVibrate { get; }

    void Play(ScanSound sound);

    void Vibrate(ScanOutcome outcome);
}

/// <summary>A short synthesized tone sequence: 16-bit mono PCM, with a WAV copy for players that want a file.</summary>
public sealed class ScanSound
{
    public const int SampleRate = 22050;

    private ScanSound(short[] pcm)
    {
        Pcm = pcm;
        Wav = BuildWav(pcm);
    }

    public short[] Pcm { get; }

    public byte[] Wav { get; }

    /// <summary>A soft rising two-note chime (A5, then E6).</summary>
    public static ScanSound Found { get; } = Chime((880, 90), (1319, 200));

    /// <summary>Two low buzzes.</summary>
    public static ScanSound Missed { get; } = Tones((420, 110), (0, 60), (420, 110));

    public static ScanSound For(ScanOutcome outcome) => outcome == ScanOutcome.Found ? Found : Missed;

    // Each part is (frequency in Hz, length in ms); frequency 0 is silence. Edges fade over a few
    // milliseconds so the speaker does not click.
    private static ScanSound Tones(params (int Hz, int Ms)[] parts)
    {
        var total = 0;
        foreach (var part in parts) total += SampleRate * part.Ms / 1000;
        var pcm = new short[total];
        var at = 0;
        foreach (var (hz, ms) in parts)
        {
            var count = SampleRate * ms / 1000;
            var fade = Math.Min(count / 2, SampleRate * 4 / 1000);
            for (var i = 0; hz > 0 && i < count; i++)
            {
                var edge = Math.Min(1.0, Math.Min(i, count - 1 - i) / (double)fade);
                pcm[at + i] = (short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * edge * short.MaxValue * 0.45);
            }
            at += count;
        }
        return new ScanSound(pcm);
    }

    // Bell-like notes: each starts quickly, then dies away instead of stopping flat, which is
    // what makes a plain beep sound harsh. Quieter than the buzz, since it is heard far more often.
    private static ScanSound Chime(params (int Hz, int Ms)[] notes)
    {
        var total = 0;
        foreach (var note in notes) total += SampleRate * note.Ms / 1000;
        var pcm = new short[total];
        var at = 0;
        var attack = SampleRate * 6 / 1000;
        foreach (var (hz, ms) in notes)
        {
            var count = SampleRate * ms / 1000;
            var decay = count / 3.0;
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1.0, i / (double)attack) * Math.Exp(-i / decay)
                               * Math.Min(1.0, (count - 1 - i) / (double)attack);
                pcm[at + i] = (short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * envelope * short.MaxValue * 0.3);
            }
            at += count;
        }
        return new ScanSound(pcm);
    }

    private static byte[] BuildWav(short[] pcm)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var data = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var sample in pcm) w.Write(sample);
        w.Flush();
        return stream.ToArray();
    }
}

/// <summary>
/// Optional sound and vibration when a card scan or dump finishes. Both are app preferences,
/// on by default; a failing player never gets in the way of the scan itself.
/// </summary>
public static class ScanFeedback
{
    private const string SoundKey = "scanSound";
    private const string VibrationKey = "scanVibration";

    public static bool SoundEnabled
    {
        get => GetBool(SoundKey);
        set => AppSettings.Set(SoundKey, value);
    }

    public static bool VibrationEnabled
    {
        get => GetBool(VibrationKey);
        set => AppSettings.Set(VibrationKey, value);
    }

    public static bool CanVibrate => AppServices.Feedback.CanVibrate;

    public static void Found() => Signal(ScanOutcome.Found);

    public static void Missed() => Signal(ScanOutcome.Missed);

    private static void Signal(ScanOutcome outcome)
    {
        var player = AppServices.Feedback;
        try { if (SoundEnabled) player.Play(ScanSound.For(outcome)); }
        catch { /* no audio device or player: stay silent */ }
        try { if (VibrationEnabled && player.CanVibrate) player.Vibrate(outcome); }
        catch { /* no vibrator */ }
    }

    private static bool GetBool(string name) =>
        AppSettings.Get(name) is not JsonValue value || !value.TryGetValue<bool>(out var on) || on;
}

/// <summary>
/// Desktop sound: winmm's waveOut on Windows, otherwise the system's command-line player
/// (afplay on macOS, paplay or aplay on Linux). Desktops do not vibrate.
/// </summary>
public sealed class DesktopFeedbackPlayer : IFeedbackPlayer
{
    public bool CanVibrate => false;

    public void Play(ScanSound sound)
    {
        if (OperatingSystem.IsWindows())
        {
            // PlaySound would be simpler, but Windows mixes it into "System sounds", which many
            // people mute; waveOut plays in the app's own volume slot.
            _ = Task.Run(() => PlayWaveOut(sound.Pcm));
            return;
        }

        var file = Path.Combine(Path.GetTempPath(), $"chamelab-{(sound == ScanSound.Found ? "found" : "missed")}.wav");
        if (!File.Exists(file)) File.WriteAllBytes(file, sound.Wav);
        if (OperatingSystem.IsMacOS()) Start("afplay", file);
        else if (!Start("paplay", file)) Start("aplay", "-q", file);
    }

    public void Vibrate(ScanOutcome outcome) { }

    private static bool Start(string program, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false });
            return process is not null;
        }
        catch { return false; /* not installed */ }
    }

    // ---- waveOut (Windows) -------------------------------------------------

    private const uint WaveMapper = 0xFFFFFFFF, WhdrDone = 0x1;

    // HDMI / DisplayPort speakers (monitors, TVs) sleep in silence and swallow the first few hundred
    // ms of a new stream while the link wakes, which is longer than the whole beep. The stream
    // therefore opens with this much silence.
    private const int LeadInMs = 400;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    // Queues the samples on the default device, waits for them to play out, then frees everything.
    private static async Task PlayWaveOut(short[] sound)
    {
        var pcm = new short[ScanSound.SampleRate * LeadInMs / 1000 + sound.Length];
        sound.CopyTo(pcm, pcm.Length - sound.Length);

        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSec = ScanSound.SampleRate,
            AvgBytesPerSec = ScanSound.SampleRate * 2, BlockAlign = 2, BitsPerSample = 16,
        };
        if (waveOutOpen(out var device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;

        var bytes = pcm.Length * 2;
        var data = Marshal.AllocHGlobal(bytes);
        var headerSize = Marshal.SizeOf<WaveHeader>();
        var header = Marshal.AllocHGlobal(headerSize);
        try
        {
            Marshal.Copy(pcm, 0, data, pcm.Length);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)bytes }, header, false);
            if (waveOutPrepareHeader(device, header, headerSize) != 0) return;
            if (waveOutWrite(device, header, headerSize) == 0)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(pcm.Length * 1000 / ScanSound.SampleRate + 1000);
                while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & WhdrDone) == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
            }
            waveOutReset(device);
            waveOutUnprepareHeader(device, header, headerSize);
        }
        finally
        {
            waveOutClose(device);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr device);
}
