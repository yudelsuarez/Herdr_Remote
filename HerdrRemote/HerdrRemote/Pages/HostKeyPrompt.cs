using System.Globalization;
using HerdrRemote.Remote;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Shared host-key prompts open automatically once per offered key until the pin is cleared by Forget
// or a saved address/port change. Only Herdr and Connection can host them: a sheet attached to a departing
// page would disappear with it. On other pages, defer the state in _pending until a valid host appears.
internal static class HostKeyPrompt
{
	// A host-key state arrived while neither Herdr nor Connection was the current page.
	private static bool _pending;

	// Key dismissed without Trust, or acknowledged as changed. Do not automatically prompt again for it
	// until Forget or an address/port change clears the pin.
	private static string? _dismissedKey;

	// Clearing the pin also clears the dismissed-key state.
	public static void Forget() => _dismissedKey = null;

	// A host-key sheet is shown: a second request would be answered null at once (PopupsHandle), and that null is no dismissal.
	private static bool _open;

	// Notify once when the Trust sheet closes: true after pinning, false otherwise.
	// Connection authenticates with its key on true and zeroes the held password on false.
	public static event Action<bool>? TrustAnswered;

	// The Mac offered a host key that is not pinned yet: Trust pins it and returns true. It does not connect: the caller does.
	public static async Task<bool> TrustAsync()
	{
		if (_open || App.Herdr.OfferedHostKey is not { } offered) { return false; }
		string trust = i18n.Translate("Trust");
		string sentence = string.Format(CultureInfo.CurrentCulture, i18n.Translate("First time connecting to {0}. Make sure it's your machine."), RemoteSettings.Host);
		_open = true;
		try
		{
			if (await SheetAsync(i18n.Translate("Trust this machine?"), sentence, offered, [trust, i18n.Translate("Cancel")]) != trust)
			{
				_dismissedKey = offered;
				TrustAnswered?.Invoke(false);
				return false;
			}
		}
		finally { _open = false; }
		RemoteSettings.SavePin(offered);
		TrustAnswered?.Invoke(true);
		return true;
	}

	// The Mac offered a key other than the pinned one, so the session refused it; a new key is accepted by forgetting the saved one (Connection page).
	public static async Task ChangedAsync()
	{
		if (_open || App.Herdr.OfferedHostKey is not { } offered) { return; }
		string ok = i18n.Translate("OK");
		_open = true;
		try
		{
			if (await SheetAsync(i18n.Translate("Host key"), i18n.Translate("This machine's identity changed, so this phone did not connect. If you did not reinstall the machine, do not continue. To accept the new key, choose Forget host key in connection settings."), offered, [ok]) == ok)
			{
				_dismissedKey = offered;
			}
		}
		finally { _open = false; }
	}

	// Handle HostKeyUnknown or HostKeyChanged: defer outside Herdr and Connection, otherwise prompt unless
	// a sheet is open or this key was already answered. After Trust, start one public-key connection here
	// when over Herdr; Connection starts its own through TrustAnswered.
	public static async Task OfferAsync(bool onPromptPage)
	{
		if (!onPromptPage)
		{
			_pending = true;
			return;
		}
		_pending = false;
		if (_open || App.Herdr.OfferedHostKey is not { } offered || offered == _dismissedKey) { return; }
		switch (App.Herdr.State)
		{
			case SessionState.HostKeyUnknown:
				if (await TrustAsync() && App.CurrentShell?.CurrentPage is Herdr) { ConnectionCard.StartConnect(); }
				break;
			case SessionState.HostKeyChanged:
				await ChangedAsync();
				break;
		}
	}

	// Herdr's and Connection's appearing: a sheet that waited opens now, only while its state still stands. A plain return to the page
	// with nothing pending opens nothing.
	public static Task ShowPendingAsync() => _pending ? OfferAsync(true) : Task.CompletedTask;

	// The host-key check on a bottom sheet: one plain sentence, the fingerprint in one muted monospace line, shortened
	// ("SHA256:" + first 6 + "…" + last 4), and Details, which shows the full key and the command that prints the Mac's own to compare.
	// Buttons[0] is the primary; the result is the tapped caption, null when the dimmed page is tapped.
	private static Task<string?> SheetAsync(string title, string sentence, string offered, string[] buttons)
		=> PopupsHandle.PopBottomSheetAsync<string?>(close =>
		{
			// offered is "{type} SHA256:{base64}" (HerdrSession.OnHostKeyReceived); macOS's standard host-key files go by the key type.
			string body = offered[(offered.IndexOf(':') + 1)..];
			string file = offered switch
			{
				_ when offered.StartsWith("ssh-ed25519 ", StringComparison.Ordinal) => "/etc/ssh/ssh_host_ed25519_key.pub",
				_ when offered.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) => "/etc/ssh/ssh_host_ecdsa_key.pub",
				_ => "/etc/ssh/ssh_host_rsa_key.pub",
			};
			Label fingerprint = new() { Style = App.Res<Style>("Hdr.Sub"), LineBreakMode = LineBreakMode.MiddleTruncation, VerticalOptions = LayoutOptions.Center, Text = $"SHA256:{body[..6]}…{body[^4..]}" };
			Button details = new() { Style = App.Res<Style>("Btn.Text"), Text = i18n.Translate("Details") };
			details.Clicked += (_, _) =>
			{
				fingerprint.LineBreakMode = LineBreakMode.WordWrap;
				fingerprint.MaxLines = -1;   // Hdr.Sub is one line; the details are several
				fingerprint.Text = $"{offered}\n" + string.Format(CultureInfo.CurrentCulture, i18n.Translate("Compare it with: ssh-keygen -lf {0}"), file);
				details.IsVisible = false;
			};
			Grid key = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
			key.Add(fingerprint);
			key.Add(details, 1);
			VerticalStackLayout stack = new() { Padding = new Thickness(24, 12, 24, 20), Spacing = 12 };
			stack.Add(new Label { Style = App.Res<Style>("Sht.Title"), Text = title });
			stack.Add(new Label { Style = App.Res<Style>("Dlg.Body"), Text = sentence });
			stack.Add(key);
			for (int i = 0; i < buttons.Length; i++)
			{
				string caption = buttons[i];
				Button button = new() { Style = App.Res<Style>(i == 0 ? "Btn.Primary" : "Btn.Text"), Text = caption };
				button.Clicked += (_, _) => close(caption);
				stack.Add(button);
			}
			return new ContentView { Content = stack };
		});
}
