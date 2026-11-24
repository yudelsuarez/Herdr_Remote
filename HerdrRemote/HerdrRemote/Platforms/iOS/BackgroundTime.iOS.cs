using UIKit;

namespace HerdrRemote.Services;

// iOS half of BackgroundTime: up to 15 minutes, subject to earlier OS expiry. The system decides the background task's duration;
// its expiration handler runs on the main thread just before the app is suspended.
public static partial class BackgroundTime
{
	private static nint _task = UIApplication.BackgroundTaskInvalid;

	public static partial void Begin(Action expired)
	{
		End();
		_task = UIApplication.SharedApplication.BeginBackgroundTask("ssh-session", () =>
		{
			expired();
			End();
		});
	}

	public static partial void End()
	{
		if (_task == UIApplication.BackgroundTaskInvalid) { return; }
		UIApplication.SharedApplication.EndBackgroundTask(_task);
		_task = UIApplication.BackgroundTaskInvalid;
	}
}
