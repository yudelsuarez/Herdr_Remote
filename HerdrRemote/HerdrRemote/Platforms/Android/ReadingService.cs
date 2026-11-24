using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Languages;

namespace HerdrRemote.Services;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class ReadingService : Service
{
	internal const string ContentAction = "herdr.reading.controls";
	internal const string GenerationExtra = "herdr.reading.generation";
	private const string ProcessExtra = "herdr.reading.process";
	private static readonly string ProcessIdentity = Guid.NewGuid().ToString("N");
	private const string Channel = "herdr.reading";
	private const int Notice = 0x52454144;
	internal static ReadingService? Current { get; private set; }
	private MediaSession? _session;
	private AudioManager? _audio;
	private AudioFocusRequestClass? _focus;
	private Focus? _focusListener;
	private BroadcastReceiver? _noisy;
	private bool _noisyRegistered;
	private long _generation;
	private bool _foreground;

	public override IBinder? OnBind(Intent? intent) => null;

	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		long generation = intent?.GetLongExtra(GenerationExtra, 0) ?? 0;
		if (intent?.Action is { } action)
		{
			if (intent.GetStringExtra(ProcessExtra) == ProcessIdentity) { App.Reading.MediaCommand(generation, action); }
			if (_generation == 0) { StopSelf(startId); }
			return StartCommandResult.NotSticky;
		}
		if (generation == 0 || App.Reading.Generation != generation || !App.Reading.IsReading)
		{
			StopSelf(startId);
			return StartCommandResult.NotSticky;
		}
		Current = this;
		_generation = generation;
		try
		{
			((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(
				new NotificationChannel(Channel, i18n.Translate("Reading aloud"), NotificationImportance.Low));
			_session ??= new MediaSession(this, "Remote Shell reading");
			_session.SetCallback(new Commands(generation), new Handler(Looper.MainLooper!));
			_session.Active = true;
			_session.SetMetadata(new MediaMetadata.Builder().PutString(MediaMetadata.MetadataKeyTitle, "Remote Shell")!
				.PutString(MediaMetadata.MetadataKeyArtist, i18n.Translate("Reading aloud"))!.Build());
			StartForeground(Notice, Notification());
			_foreground = true;
			_audio = (AudioManager)GetSystemService(AudioService)!;
			using AudioAttributes attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)?.SetContentType(AudioContentType.Speech)?.Build()
				?? throw new InvalidOperationException("No media audio attributes.");
			_focusListener = new Focus(generation);
			_focus = new AudioFocusRequestClass.Builder(AudioFocus.Gain).SetAudioAttributes(attributes)!
				.SetOnAudioFocusChangeListener(_focusListener, new Handler(Looper.MainLooper!))!.SetWillPauseWhenDucked(true)!.Build()
				?? throw new InvalidOperationException("No audio focus request.");
			_noisy ??= new Noisy(generation);
			RegisterReceiver(_noisy, new IntentFilter(AudioManager.ActionAudioBecomingNoisy), ReceiverFlags.NotExported);
			_noisyRegistered = true;
			bool allowed = _audio.RequestAudioFocus(_focus) == AudioFocusRequest.Granted;
			App.Reading.MediaReady(generation, allowed);
			Update(generation);
		}
		catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
		{
			System.Diagnostics.Debug.WriteLine($"[ReadingService] setup failed: {ex.GetType().Name}");
			App.Reading.MediaReady(generation, false);
			Release(generation);
		}
		return StartCommandResult.NotSticky;
	}

	private sealed class Commands(long generation) : MediaSession.Callback
	{
		public override void OnStop() => App.Reading.MediaCommand(generation, "stop");
		public override void OnPause() => App.Reading.MediaCommand(generation, "pause");
		public override void OnPlay() => App.Reading.MediaCommand(generation, "play");
	}

	private sealed class Noisy(long generation) : BroadcastReceiver
	{
		public override void OnReceive(Context? context, Intent? intent)
		{
			if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
			{
				App.Reading.MediaCommand(generation, "stop");
			}
		}
	}

	private sealed class Focus(long generation) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
	{
		public void OnAudioFocusChange(AudioFocus focusChange)
		{
			if (focusChange != AudioFocus.Gain) { App.Reading.MediaCommand(generation, "stop"); }
		}
	}

	internal bool Resume(long generation)
	{
		if (generation != _generation || _audio is null || _focus is null) { return false; }
		try
		{
			StartForeground(Notice, Notification());
			_foreground = true;
			return _audio.RequestAudioFocus(_focus) == AudioFocusRequest.Granted;
		}
		catch (Java.Lang.Exception) { return false; }
	}

	internal void Update(long generation)
	{
		if (generation != _generation || _session is null) { return; }
		ReadingSession read = App.Reading;
		PlaybackStateCode phase = read.Paused ? PlaybackStateCode.Paused : read.Phase == "playing" ? PlaybackStateCode.Playing : PlaybackStateCode.Buffering;
		long actions = PlaybackState.ActionStop;
		if (read.CanPause) { actions |= PlaybackState.ActionPause | PlaybackState.ActionPlay; }
		_session.SetPlaybackState(new PlaybackState.Builder().SetActions(actions)!
			.SetState(phase, PlaybackState.PlaybackPositionUnknown, read.Paused || read.Phase != "playing" ? 0 : read.AppliedRate)!.Build());
		if (read.Paused && _foreground)
		{
			StopForeground(StopForegroundFlags.Detach);
			_foreground = false;
			if (_focus is not null) { _audio?.AbandonAudioFocusRequest(_focus); }
		}
		((NotificationManager)GetSystemService(NotificationService)!).Notify(Notice, Notification());
	}

	private Notification Notification()
	{
		Intent open = new(this, typeof(MainActivity));
		open.SetAction(ContentAction);
		open.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
		var builder = new Notification.Builder(this, Channel);
		builder.SetSmallIcon(Resource.Mipmap.appicon);
		builder.SetContentTitle("Remote Shell");
		builder.SetContentText(i18n.Translate("Reading aloud"));
		builder.SetContentIntent(PendingIntent.GetActivity(this, Notice, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
		builder.SetOngoing(!App.Reading.Paused);
		builder.SetVisibility(NotificationVisibility.Public);
		int index = 0;
		if (App.Reading.CanPause)
		{
			string action = App.Reading.Paused ? "play" : "pause";
			builder.AddAction(new Notification.Action.Builder(null, i18n.Translate(App.Reading.Paused ? "Resume" : "Pause"), Transport(action, index++)).Build());
		}
		builder.AddAction(new Notification.Action.Builder(null, i18n.Translate("Stop reading"), Transport("stop", index)).Build());
		builder.SetStyle(new Notification.MediaStyle().SetMediaSession(_session!.SessionToken)!.SetShowActionsInCompactView(App.Reading.CanPause ? [0, 1] : [0]));
		return builder.Build();
	}

	private PendingIntent? Transport(string command, int index)
	{
		Intent intent = new(this, typeof(ReadingService));
		// Generation is also part of identity: an old PendingIntent must never acquire a new read's authority.
		intent.SetAction(command);
		intent.SetData(Android.Net.Uri.Parse($"herdr-reading://{ProcessIdentity}/{_generation}/{command}"));
		intent.PutExtra(GenerationExtra, _generation);
		intent.PutExtra(ProcessExtra, ProcessIdentity);
		return PendingIntent.GetService(this, index, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
	}

	internal void Release(long generation)
	{
		if (_generation != generation) { return; }
		_generation = 0;
		if (_focus is not null) { _audio?.AbandonAudioFocusRequest(_focus); _focus.Dispose(); _focus = null; }
		_focusListener?.Dispose(); _focusListener = null;
		if (_noisy is not null)
		{
			if (_noisyRegistered) { UnregisterReceiver(_noisy); _noisyRegistered = false; }
			_noisy.Dispose(); _noisy = null;
		}
		if (_session is not null) { _session.Active = false; _session.Release(); _session.Dispose(); _session = null; }
		StopForeground(StopForegroundFlags.Remove);
		_foreground = false;
		((NotificationManager)GetSystemService(NotificationService)!).Cancel(Notice);
		if (Current == this) { Current = null; }
		StopSelf();
	}

	public override void OnDestroy()
	{
		long generation = _generation;
		if (generation != 0) { App.Reading.MediaCommand(generation, "interrupt"); Release(generation); }
		base.OnDestroy();
	}
}
