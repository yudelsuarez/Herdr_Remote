using System.Globalization;

namespace HerdrRemote.Remote;

// SSH settings are stored through App.Setting and App.SaveSetting; only App accesses storage.
// The pinned host key is a public fingerprint, not a secret, so it belongs in Preferences.
internal static class RemoteSettings
{
	private const string HostKey = "herdr.host";
	private const string PortKey = "herdr.port";
	private const string UserKey = "herdr.user";
	private const string PinKey = "herdr.host_key";
	private const string AgentSortKey = "herdr.agent_sort";

	public static string Host => App.Setting(HostKey, "");

	public static int Port => int.Parse(App.Setting(PortKey, "22"), CultureInfo.InvariantCulture);

	public static string User => App.Setting(UserKey, "");

	// "{key type} SHA256:{fingerprint}", as printed by `ssh-keygen -l`; empty until the user trusts a Mac.
	public static string PinnedHostKey => App.Setting(PinKey, "");

	public static bool IsConfigured => Host.Length > 0 && User.Length > 0;

	// A changed host or port requires host-key trust again. Return true if the saved pin was cleared.
	public static bool Save(string host, int port, string user)
	{
		bool another = host != Host || port != Port;
		if (another) { SavePin(""); }
		App.SaveSetting(HostKey, host);
		App.SaveSetting(PortKey, port.ToString(CultureInfo.InvariantCulture));
		App.SaveSetting(UserKey, user);
		return another;
	}

	public static void SavePin(string pin) => App.SaveSetting(PinKey, pin);

	// Per-pane terminal font size after a pinch; null uses the layout default.
	public static double? FontSize(string paneId) =>
		App.Setting(FontKey(paneId), "") is { Length: > 0 } saved ? double.Parse(saved, CultureInfo.InvariantCulture) : null;

	public static void SaveFontSize(string paneId, double size) => App.SaveSetting(FontKey(paneId), size.ToString(CultureInfo.InvariantCulture));

	private static string FontKey(string paneId) => $"herdr.font.{paneId}";

	// Floating D-pad position: top-left coordinates as fractions (0..1) of its available movement area; null until dragged.
	public static (double X, double Y)? DPadAt =>
		App.Setting(DPadKey, "") is { Length: > 0 } saved && saved.Split(',') is [string x, string y]
			? (double.Parse(x, CultureInfo.InvariantCulture), double.Parse(y, CultureInfo.InvariantCulture)) : null;

	public static void SaveDPadAt(double x, double y) =>
		App.SaveSetting(DPadKey, $"{x.ToString(CultureInfo.InvariantCulture)},{y.ToString(CultureInfo.InvariantCulture)}");

	private const string DPadKey = "herdr.dpad.at";

	// Agent list order: "priority" sorts by urgency; "grouped" follows herdr's order.
	public static string AgentSort
	{
		get => App.Setting(AgentSortKey, "priority");
		set => App.SaveSetting(AgentSortKey, value);
	}
}
