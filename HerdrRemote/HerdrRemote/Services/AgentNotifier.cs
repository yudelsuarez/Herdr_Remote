namespace HerdrRemote.Services;

// Shows agent alerts in-app while the app is open, or as local notifications with the system's default sound in the background.
// There are no push or spoken notifications. Platform implementations are in Platforms/<os>/AgentNotifier.<os>.cs.
public static partial class AgentNotifier
{
	// Shows one notification per machine and pane; tapping it opens the pane through App.OpenPaneFromNotification.
	// machine is HerdrSession.MachineKey. A new notification for the same pane replaces the previous one.
	// needs distinguishes "needs you" from "is done" and is stored in the system notification for retrieval after an app restart.
	public static void Show(string machine, string paneId, bool needs, string title, string body) => MainThread.BeginInvokeOnMainThread(() =>
	{
		if (machine != Remote.HerdrSession.MachineKey) { return; }
		if (App.Herdr.Agents.Foreground)
		{
			if (App.CurrentShell?.CurrentPage is Pages.Pane { LeaseActive: true } pane && pane.Address == paneId) { return; }
			Popups.AgentToast.Show(machine, paneId, title, body);
		}
		else
		{
			DismissInApp();
			ShowSystem(machine, paneId, needs, title, body);
		}
	});

	static partial void ShowSystem(string machine, string paneId, bool needs, string title, string body);

	// Returns this machine's delivered notifications, including those from earlier app runs, with each pane and its "needs you" flag.
	public static partial Task<List<(string Pane, bool Needs)>> DeliveredAsync(string machine);

	// Removes the pane's notification from the system list and lock screen, if present.
	public static void RemoveSystem(string machine, string paneId) => MainThread.BeginInvokeOnMainThread(() => RemoveSystemNotice(machine, paneId));

	static partial void RemoveSystemNotice(string machine, string paneId);

	internal static void DismissInApp() => Popups.AgentToast.DismissInApp();
	internal static void DismissForPane(string machine, string paneId) => Popups.AgentToast.DismissForPane(machine, paneId);

	// Requests notification permission on the first connection; the system prompts the user once.
	public static partial Task RequestPermissionAsync();

	// Both platforms identify a notification by its machine and pane.
	internal static string Key(string machine, string paneId) => $"{machine}/{paneId}";

	// Uses 32-bit FNV-1a over UTF-8 bytes for Android's notification ID and request code.
	// Unlike string.GetHashCode, this value stays stable across processes.
	internal static int StableId(string key)
	{
		unchecked
		{
			uint hash = 2166136261;
			foreach (byte b in System.Text.Encoding.UTF8.GetBytes(key))
			{
				hash ^= b;
				hash *= 16777619;
			}
			return (int)hash;
		}
	}
}
