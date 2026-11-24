namespace HerdrRemote.Pages;

// One native compositor animation per spinner: a linear 0.9-second turn matching herdr-app, using Core Animation
// on iOS or a centered RotateAnimation on Android. Never tick a managed UI-thread animation or set MAUI Rotation,
// which would compete with native property mapping. Start on load when a platform view exists; stop on unload
// and restart after row moves. The native animation retains no managed closure.
internal sealed partial class Spinner
{
	private readonly View _view;
	private bool _running;

	public Spinner(View view)
	{
		_view = view;
		_view.Loaded += (_, _) => Apply();
		_view.Unloaded += (_, _) => PlatformStop();
		_view.HandlerChanging += (_, _) => PlatformStop();
		_view.HandlerChanged += (_, _) => Apply();
	}

	public void Start()
	{
		_running = true;
		Apply();
	}

	// Stopped upright.
	public void Stop()
	{
		_running = false;
		PlatformStop();
	}

	private void Apply()
	{
		if (_running && _view.IsLoaded && _view.Handler?.PlatformView is { } platformView) { PlatformStart(platformView); }
	}

	// Starts the turn on the platform view unless it already turns there.
	partial void PlatformStart(object platformView);

	partial void PlatformStop();
}
