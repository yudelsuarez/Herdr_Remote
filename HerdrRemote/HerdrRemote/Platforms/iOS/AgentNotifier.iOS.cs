using Foundation;
using UserNotifications;

namespace HerdrRemote.Services;

// Local iOS notifications with alert and sound permission; no APNs.
public static partial class AgentNotifier
{
	// The UserInfo keys that carry the pane id and its Mac from the notification to the delegate.
	internal const string PaneKey = "herdr.pane";
	internal const string MachineKey = "herdr.machine";
	internal const string NeedsKey = "herdr.needs";

	static partial void ShowSystem(string machine, string paneId, bool needs, string title, string body)
	{
		if (App.Herdr.Agents.Foreground) { return; }
		var content = new UNMutableNotificationContent
		{
			Title = title,
			Body = body,
			Sound = UNNotificationSound.Default,
			UserInfo = NSDictionary.FromObjectsAndKeys(new NSObject[] { new NSString(paneId), new NSString(machine), NSNumber.FromBoolean(needs) },
				new NSObject[] { new NSString(PaneKey), new NSString(MachineKey), new NSString(NeedsKey) }),
		};
		// The identifier is the pane of its Mac: a new notice of the same pane replaces the last one.
		var request = UNNotificationRequest.FromIdentifier(Key(machine, paneId), content, null);
		UNUserNotificationCenter.Current.AddNotificationRequest(request, error =>
		{
			if (error is not null) { System.Diagnostics.Debug.WriteLine($"[AgentNotifier] post failed: {error.LocalizedDescription}"); }
		});
	}

	static partial void RemoveSystemNotice(string machine, string paneId)
	{
		string[] ids = [Key(machine, paneId)];
		UNUserNotificationCenter.Current.RemoveDeliveredNotifications(ids);
		UNUserNotificationCenter.Current.RemovePendingNotificationRequests(ids);
	}

	public static partial async Task<List<(string Pane, bool Needs)>> DeliveredAsync(string machine)
	{
		List<(string Pane, bool Needs)> found = [];
		foreach (UNNotification notice in await UNUserNotificationCenter.Current.GetDeliveredNotificationsAsync())
		{
			NSDictionary info = notice.Request.Content.UserInfo;
			if (info[MachineKey]?.ToString() != machine || info[PaneKey]?.ToString() is not { Length: > 0 } pane) { continue; }
			found.Add((pane, info[NeedsKey] is NSNumber needs && needs.BoolValue));
		}
		return found;
	}

	public static partial async Task RequestPermissionAsync()
	{
		try { await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound); }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AgentNotifier] authorization request failed: {ex.Message}"); }
	}
}
