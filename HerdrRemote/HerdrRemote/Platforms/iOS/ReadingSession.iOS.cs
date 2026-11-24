using AVFoundation;
using Foundation;
using MediaPlayer;
using Languages;

namespace HerdrRemote.Services;

internal sealed partial class ReadingSession
{
	private long _mediaGeneration;
	private NSObject? _interruption, _route;
	private readonly List<(MPRemoteCommand Command, NSObject Target)> _commands = [];

	partial void MediaStarting(long generation, ref Task ready)
	{
		_mediaGeneration = generation;
		AudioPlayer.PrepareSession();
		_interruption = AVAudioSession.Notifications.ObserveInterruption((_, e) =>
		{
			if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
			{
				MainThread.BeginInvokeOnMainThread(() => MediaCommand(generation, "stop"));
			}
		});
		_route = AVAudioSession.Notifications.ObserveRouteChange((_, e) =>
		{
			if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
			{
				MainThread.BeginInvokeOnMainThread(() => MediaCommand(generation, "stop"));
			}
		});
		MPRemoteCommandCenter center = MPRemoteCommandCenter.Shared;
		foreach (var (command, action) in new[] { (center.StopCommand, "stop"), (center.PauseCommand, "pause"), (center.PlayCommand, "play") })
		{
			command.Enabled = action == "stop" || CanPause;
			NSObject target = command.AddTarget(_ =>
			{
				if (generation != Generation || !IsReading) { return MPRemoteCommandHandlerStatus.NoSuchContent; }
				MainThread.BeginInvokeOnMainThread(() => MediaCommand(generation, action));
				return MPRemoteCommandHandlerStatus.Success;
			});
			_commands.Add((command, target));
		}
		center.TogglePlayPauseCommand.Enabled = false;
		center.NextTrackCommand.Enabled = center.PreviousTrackCommand.Enabled = false;
		center.ChangePlaybackPositionCommand.Enabled = false;
		MediaChanged(generation);
	}

	partial void MediaChanged(long generation)
	{
		if (_mediaGeneration != generation) { return; }
		MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo
		{
			Title = "Remote Shell", Artist = i18n.Translate("Reading aloud"), PlaybackRate = Paused || !_playing ? 0 : AppliedRate,
		};
	}

	partial void MediaResuming(long generation, ref bool allowed)
	{
		if (_mediaGeneration != generation) { allowed = false; return; }
		try { AudioPlayer.PrepareSession(); }
		catch (SpeechStreamException) { allowed = false; }
	}

	partial void MediaEnded(long generation)
	{
		if (_mediaGeneration != generation) { return; }
		_mediaGeneration = 0;
		_interruption?.Dispose(); _interruption = null;
		_route?.Dispose(); _route = null;
		foreach (var (command, target) in _commands) { command.RemoveTarget(target); command.Enabled = false; target.Dispose(); }
		_commands.Clear();
		MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
		AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out NSError? error);
		if (error is not null) { System.Diagnostics.Debug.WriteLine("[ReadingSession] audio deactivation failed"); }
	}
}
