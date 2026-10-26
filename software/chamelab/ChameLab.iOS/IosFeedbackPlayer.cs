using System.Collections.Generic;
using AVFoundation;
using ChameLab.Services;
using Foundation;
using UIKit;

namespace ChameLab.iOS;

/// <summary>
/// Scan sounds through AVAudioPlayer in the ambient category (mixes with other audio and
/// respects the silent switch), and haptics through the Taptic Engine.
/// </summary>
public sealed class IosFeedbackPlayer : IFeedbackPlayer
{
    // Players stop when collected, so each one is held until it has finished.
    private readonly HashSet<AVAudioPlayer> _playing = new();
    private bool _sessionReady;

    public bool CanVibrate => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;

    public void Play(ScanSound sound)
    {
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            if (!_sessionReady)
            {
                AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Ambient);
                _sessionReady = true;
            }
            var player = AVAudioPlayer.FromData(NSData.FromArray(sound.Wav));
            if (player is null) return;
            _playing.Add(player);
            player.FinishedPlaying += (_, _) => _playing.Remove(player);
            player.Play();
        });
    }

    public void Vibrate(ScanOutcome outcome) =>
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            using var haptic = new UINotificationFeedbackGenerator();
            haptic.NotificationOccurred(outcome == ScanOutcome.Found
                ? UINotificationFeedbackType.Success
                : UINotificationFeedbackType.Error);
        });
}
