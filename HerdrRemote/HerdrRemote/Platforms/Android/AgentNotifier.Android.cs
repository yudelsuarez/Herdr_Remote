using Android.App;
using Android.Content;
using Android.Content.PM;

namespace HerdrRemote.Services;

// Local Android notifications with a notification channel and an immutable, updatable tap intent; no FCM.
public static partial class AgentNotifier
{
	// The extras that carry the pane id and its Mac from the notification to MainActivity.
	internal const string PaneExtra = "herdr.pane";
	internal const string MachineExtra = "herdr.machine";
	internal const string NeedsExtra = "herdr.needs";

	private const string ChannelId = "herdr.agents";
	private static bool _channelCreated;

	static partial void ShowSystem(string machine, string paneId, bool needs, string title, string body)
	{
		if (App.Herdr.Agents.Foreground) { return; }
		Context context = Platform.AppContext;
		if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) { return; }
		if (!_channelCreated)
		{
			manager.CreateNotificationChannel(new NotificationChannel(ChannelId, Languages.i18n.Translate("Agents"), NotificationImportance.Default));
			_channelCreated = true;
		}
		var open = new Intent(context, typeof(MainActivity));
		open.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
		open.PutExtra(PaneExtra, paneId);
		open.PutExtra(MachineExtra, machine);
		// Stable across processes, so a notice of the same pane replaces the last one after an app restart too.
		int id = StableId(Key(machine, paneId));
		var builder = new Notification.Builder(context, ChannelId);
		builder.SetContentTitle(title);
		builder.SetContentText(body);
		builder.SetSmallIcon(Resource.Mipmap.appicon);
		builder.SetContentIntent(PendingIntent.GetActivity(context, id, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
		builder.SetAutoCancel(true);
		var extras = new Android.OS.Bundle();
		extras.PutString(PaneExtra, paneId);
		extras.PutString(MachineExtra, machine);
		extras.PutBoolean(NeedsExtra, needs);
		builder.AddExtras(extras);
		manager.Notify(id, builder.Build());
	}

	static partial void RemoveSystemNotice(string machine, string paneId)
	{
		if (Platform.AppContext.GetSystemService(Context.NotificationService) is NotificationManager manager) { manager.Cancel(StableId(Key(machine, paneId))); }
	}

	public static partial Task<List<(string Pane, bool Needs)>> DeliveredAsync(string machine)
	{
		List<(string Pane, bool Needs)> found = [];
		if (Platform.AppContext.GetSystemService(Context.NotificationService) is NotificationManager manager)
		{
			foreach (Android.Service.Notification.StatusBarNotification notice in manager.GetActiveNotifications() ?? [])
			{
				Android.OS.Bundle? extras = notice.Notification?.Extras;
				if (extras?.GetString(MachineExtra) != machine || extras.GetString(PaneExtra) is not { Length: > 0 } pane) { continue; }
				found.Add((pane, extras.GetBoolean(NeedsExtra)));
			}
		}
		return Task.FromResult(found);
	}

	public static partial Task RequestPermissionAsync() => MainThread.InvokeOnMainThreadAsync(() =>
	{
		if (Platform.CurrentActivity is { } activity && activity.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
		{
			activity.RequestPermissions([Android.Manifest.Permission.PostNotifications], 0);
		}
	});
}
