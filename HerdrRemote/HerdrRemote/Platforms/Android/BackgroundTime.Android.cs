using Android.App;
using Android.Content;
using Android.OS;

namespace HerdrRemote.Services;

public static partial class BackgroundTime
{
	private static bool _started;

	public static partial void Begin(Action expired)
	{
		Context context = Platform.AppContext;
		if (_started || context.GetSystemService(Context.PowerService) is not PowerManager power
			|| !power.IsIgnoringBatteryOptimizations(context.PackageName!)) { return; }
		try { _started = context.StartService(new Intent(context, typeof(ConnectionBackgroundService))) is not null; }
		catch (Java.Lang.IllegalStateException ex)
		{
			System.Diagnostics.Debug.WriteLine($"[BackgroundTime] service not started: {ex.GetType().Name}");
		}
	}

	public static partial void End()
	{
		if (!_started) { return; }
		_started = false;
		Context context = Platform.AppContext;
		context.StopService(new Intent(context, typeof(ConnectionBackgroundService)));
	}
}

// Keeps the current connection out of Android's cached process freezer while the battery exemption is active.
[Service(Exported = false)]
public sealed class ConnectionBackgroundService : Service
{
	public override IBinder? OnBind(Intent? intent) => null;
	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId) => StartCommandResult.NotSticky;
	public override void OnTaskRemoved(Intent? rootIntent) => StopSelf();
}
