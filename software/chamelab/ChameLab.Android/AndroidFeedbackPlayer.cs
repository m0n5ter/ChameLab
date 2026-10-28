using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Media;
using Android.OS;
using ChameLab.Services;

namespace ChameLab.Android;

/// <summary>Scan sounds through a one-shot AudioTrack (sonification, so it follows the notification volume) and the vibrator.</summary>
public sealed class AndroidFeedbackPlayer : IFeedbackPlayer
{
    private static Context Context => global::Android.App.Application.Context;

    private static Vibrator? Vibrator => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? ((VibratorManager?)Context.GetSystemService(Context.VibratorManagerService))?.DefaultVibrator
#pragma warning disable CA1422 // the per-app vibrator service, for Android 11 and older
        : (Vibrator?)Context.GetSystemService(Context.VibratorService);
#pragma warning restore CA1422

    public bool CanVibrate => Vibrator?.HasVibrator == true;

    public void Play(ScanSound sound)
    {
        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.AssistanceSonification)!
                .SetContentType(AudioContentType.Sonification)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(ScanSound.SampleRate)!
                .SetChannelMask(ChannelOut.Mono)!
                .Build()!)
            .SetTransferMode(AudioTrackMode.Static)
            .SetBufferSizeInBytes(sound.Pcm.Length * 2)
            .Build();
        track.Write(sound.Pcm, 0, sound.Pcm.Length);
        track.Play();

        // A static track is freed once it has played out.
        var length = TimeSpan.FromSeconds(sound.Pcm.Length / (double)ScanSound.SampleRate);
        _ = Task.Delay(length + TimeSpan.FromMilliseconds(200)).ContinueWith(_ => track.Release());
    }

    public void Vibrate(ScanOutcome outcome)
    {
        // Off/on timings in ms: one short tap when found, two firmer ones when not.
        long[] pattern = outcome == ScanOutcome.Found ? [0, 40] : [0, 70, 80, 70];
        var vibrator = Vibrator;
        if (vibrator is null) return;
        // Scan completion is a notification. Without an explicit usage Android can classify
        // short effects as touch feedback and suppress them when touch vibration is disabled.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            using var attributes = new VibrationAttributes.Builder()
                .SetUsage((int)VibrationAttributesUsageType.Notification)!
                .Build();
            using var effect = VibrationEffect.CreateWaveform(pattern, -1)!;
            vibrator.Vibrate(effect, attributes);
        }
        else
        {
            using var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Notification)!
                .SetContentType(AudioContentType.Sonification)!
                .Build();
#pragma warning disable CA1422 // AudioAttributes overloads for Android 12L and older
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var effect = VibrationEffect.CreateWaveform(pattern, -1);
                vibrator.Vibrate(effect, attributes);
            }
            else
                vibrator.Vibrate(pattern, -1, attributes);
#pragma warning restore CA1422
        }
    }
}
