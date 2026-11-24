using Foundation;
using HerdrRemote.Services;
using UIKit;
using UserNotifications;

namespace HerdrRemote;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	[Export("applicationDidReceiveMemoryWarning:")]
	public void ReceiveMemoryWarning(UIApplication application)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (Microsoft.Maui.Controls.Application.Current is not { } app) { return; }
			foreach (Window window in app.Windows)
			{
				if (window.Page is AppShell owner) { owner.TrimRetainedPane(pressure: true); }
			}
		});
	}

	public override bool FinishedLaunching(UIApplication app, NSDictionary? options)
	{
		UNUserNotificationCenter.Current.Delegate = new NotificationDelegate();
		return base.FinishedLaunching(app, options);
	}

	// Foreground attention stays in-app; a background notification's tap still opens its pane.
	private sealed class NotificationDelegate : UNUserNotificationCenterDelegate
	{
		public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
			completionHandler((UNNotificationPresentationOptions)0);

		public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
		{
			NSDictionary info = response.Notification.Request.Content.UserInfo;
			if (info[AgentNotifier.PaneKey] is NSString paneId) { _ = App.OpenPaneFromNotification(paneId.ToString(), (info[AgentNotifier.MachineKey] as NSString)?.ToString()); }
			completionHandler();
		}
	}
}
