using System.Globalization;
using System.Text;
using HerdrRemote.Remote;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Connection settings pushed from Herdr or Settings. The card follows session state; Save stores the Mac
// address, port, and user, then reconnects. Send passwords only to pinned hosts: before trust, retain the
// password until public-key authentication after Trust fails with AuthFailed. Copy immediately copies this
// phone's authorized_keys entry; Forget immediately clears the pinned host key.
public partial class Connection : ContentPage, IQueryAttributable
{
	private readonly BusyRunner _run;

	// This phone's authorized_keys line; the key pair is made on the first open (SshKeyStore).
	private string _publicLine = "";

	// UTF-8 password retained when Save has no pinned host key, for authentication after Trust.
	// Zero and discard it on declined trust, Connected, the post-trust result, HostKeyChanged, page exit,
	// another Save, or completion of password authentication.
	private byte[]? _held;

	// First published state after Connecting for post-trust authentication while a password is held.
	// SetState posts asynchronously, so the connect can return before its state arrives. Page exit cancels this wait.
	private TaskCompletionSource<SessionState>? _afterTrust;

	// Return to the list on Connected only when this page initiated Save and connect.
	private bool _saved;

	// The query focus=password (the card's "Add key" and "Type it again" on Herdr): the password field takes the focus on appearing.
	private bool _focusPassword;

	public Connection()
	{
		InitializeComponent();
		_run = new BusyRunner(IsBusyBox);
		Header.TitleText = i18n.Translate("Connection");
		Lbl_Host.Text = i18n.Translate("Machine address");
		Lbl_Port.Text = i18n.Translate("Port");
		Lbl_User.Text = i18n.Translate("User");
		Span_Password.Text = i18n.Translate("Machine password");
		Span_PasswordNote.Text = " · " + i18n.Translate("used once to add this phone's key");
		Btn_Save.Text = i18n.Translate("Save and connect");
		En_Host.Text = RemoteSettings.Host;
		En_Port.Text = RemoteSettings.Port.ToString(CultureInfo.InvariantCulture);
		En_User.Text = RemoteSettings.User;
		SecretEye.Attach(En_Password, Eye_Password);
		App.ApplyKeyboardSafeArea(this);
		App.KeepFocusedFieldVisible(Scroll);
		_ = _run.RunAsync("Connection", async () => _publicLine = await SshKeyStore.PublicLineAsync());
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query) => _focusPassword = query.TryGetValue("focus", out object? value) && value is "password";

	// Subscribe only while visible; show any host-key prompt deferred until this page appears.
	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		App.Herdr.StateChanged += Session_StateChanged;
		HostKeyPrompt.TrustAnswered += Prompt_TrustAnswered;
		ShowState();
		if (_focusPassword)
		{
			_focusPassword = false;
			En_Password.Focus();
		}
		try { await HostKeyPrompt.ShowPendingAsync(); }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Connection] pending prompt unexpected: {ex}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
		}
	}

	// On navigation or backgrounding, stop the status animation, clear typed and retained passwords,
	// and prevent a later Connected event from navigating away.
	protected override void OnDisappearing()
	{
		Header.DetachReading();
		base.OnDisappearing();
		App.Herdr.StateChanged -= Session_StateChanged;
		HostKeyPrompt.TrustAnswered -= Prompt_TrustAnswered;
		Card.Still();
		En_Password.Text = "";
		ZeroHeld();
		_saved = false;
	}

	private void ZeroHeld()
	{
		if (_held is not null) { Array.Clear(_held); }
		_held = null;
		_afterTrust?.TrySetCanceled();
		_afterTrust = null;
	}

	// The header's Mac name, the card, the password hint (its second sentence only while a host key is pinned) and the rows; Host key
	// reads "Changed" in red while the Mac offers another key than the pinned one, "SHA256:" and the fingerprint's first three
	// characters while trusted, and Forget is offered while one is pinned.
	private void ShowState()
	{
		SessionState state = App.Herdr.State;
		Header.Subtitle(App.Herdr.Agents.Machine);
		// Called only while the page is shown.
		Card.Show(state, RemoteSettings.IsConfigured, true);
		string pin = RemoteSettings.PinnedHostKey;
		bool pinned = pin.Length > 0;
		bool changed = pinned && state == SessionState.HostKeyChanged;
		Lbl_PasswordHint.Text = i18n.Translate("The eye shows or hides the text.") + (pinned ? " " + i18n.Translate("Changing the address or port means trusting the machine again.") : "");
		const string Sha = "SHA256:";
		int fingerprint = pin.IndexOf(Sha, StringComparison.Ordinal) + Sha.Length;
		string trusted = fingerprint >= Sha.Length ? Sha + pin.Substring(fingerprint, Math.Min(3, pin.Length - fingerprint)) + "…" : i18n.Translate("Trusted");
		Rows.SetItems(
		[
			new("\xE923", i18n.Translate("This phone's key"), i18n.Translate("Copy"), false, Chevron: true, Key: "copy"),
			new("\xE953", i18n.Translate("Host key"), changed ? i18n.Translate("Changed") : pinned ? trusted : i18n.Translate("Not trusted yet"), true, Chevron: pinned, Key: pinned ? "forget" : "", ValueTone: changed ? ValueTone.Need : ValueTone.None, MonoValue: pinned && !changed),
		]);
	}

	private void Session_StateChanged()
	{
		SessionState state = App.Herdr.State;
		ShowState();
		if (state != SessionState.Connecting)
		{
			_afterTrust?.TrySetResult(state);
			_afterTrust = null;
		}
		switch (state)
		{
			case SessionState.Connected:
				ZeroHeld();
				// Only after this page's own Save, and only while it is still the page on screen.
				if (_saved && App.CurrentShell?.CurrentPage == this)
				{
					_saved = false;
					_ = App.GoToAsync("..");
				}
				return;
			case SessionState.HostKeyChanged:
				ZeroHeld();
				return;
			case SessionState.PasswordRefused:
				En_Password.Text = "";
				En_Password.Focus();
				return;
		}
	}

	// Declined trust discards the held password. Accepted trust starts public-key authentication through _run
	// with Save disabled. HostKeyPrompt connects only over Herdr, so authentication runs once here.
	// On AuthFailed, send the held password once to the now-pinned host without another tap.
	private async void Prompt_TrustAnswered(bool trusted)
	{
		if (!trusted)
		{
			ZeroHeld();
			return;
		}
		await _run.RunAsync("Connection", async () =>
		{
			TaskCompletionSource<SessionState>? result = _afterTrust = _held is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
			try
			{
				await App.Herdr.ConnectAsync(CancellationToken.None);
				if (result is null || await result.Task != SessionState.AuthFailed || _held is not { } password) { return; }
				_held = null;
				// Sent only to a pinned host, checked at the moment of sending.
				if (RemoteSettings.PinnedHostKey.Length > 0) { await RestartAsync(password); } else { Array.Clear(password); }
			}
			// A Disconnect, the background or the page leaving ended it.
			catch (OperationCanceledException) { }
			finally { ZeroHeld(); }
		}, Btn_Save);
	}

	// Run Save's connection or password authentication. Cancellation from Disconnect or backgrounding is silent;
	// other exceptions reach RunAsync's error popup. Zero password bytes on both success and failure.
	private static async Task RestartAsync(byte[]? password)
	{
		try { await App.Herdr.RestartAsync(password); }
		catch (OperationCanceledException) { }
		finally { if (password is not null) { Array.Clear(password); } }
	}

	private void Back_Clicked(object? sender, EventArgs e) => _ = App.GoToAsync("..");

	// Keep the page open during Save; the live card shows progress and Connected returns to the list.
	private async void Btn_Save_Clicked(object? sender, EventArgs e)
	{
		await _run.RunAsync("Connection", async () =>
		{
			ZeroHeld();
			string host = (En_Host.Text ?? "").Trim();
			string user = (En_User.Text ?? "").Trim();
			if (host.Length == 0 || user.Length == 0)
			{
				await BusyRunner.Show("Connection", i18n.Translate("Enter the machine's address and user first."));
				return;
			}
			if (!int.TryParse((En_Port.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
			{
				await BusyRunner.Show("Connection", i18n.Translate("The port must be a number from 1 to 65535."));
				return;
			}
			AppShell? owner = Window?.Page as AppShell;
			string previousMachine = HerdrSession.MachineKey;
			if (RemoteSettings.Save(host, port, user)) { HostKeyPrompt.Forget(); }
			if (previousMachine != HerdrSession.MachineKey && owner is not null) { await owner.RetirePaneAsync(); }
			// Read after Save: another address or port has just cleared the pin. Unpinned, the password waits for Trust.
			byte[]? password = null;
			if (!string.IsNullOrEmpty(En_Password.Text))
			{
				byte[] typed = Encoding.UTF8.GetBytes(En_Password.Text);
				En_Password.Text = "";
				if (RemoteSettings.PinnedHostKey.Length > 0) { password = typed; } else { _held = typed; }
			}
			_saved = true;
			ShowState();
			await RestartAsync(password);
		}, Btn_Save);
	}

	private async void Card_ActionTapped(object? sender, string key)
	{
		switch (key)
		{
			case "password":
				En_Password.Focus();
				break;
			case "disconnect":
				App.Herdr.DisconnectByOwner();
				break;
			// Not through _run: the answer starts this page's key connect in _run (Prompt_TrustAnswered), which a held _run would skip.
			// A second tap while the sheet is shown opens nothing (HostKeyPrompt's own guard).
			case "trust":
				try { await HostKeyPrompt.TrustAsync(); }
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[Connection] trust unexpected: {ex}");
					await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
				}
				break;
			case "details":
				await _run.RunAsync("Connection", () => App.Herdr.State == SessionState.HostKeyChanged ? HostKeyPrompt.ChangedAsync() : BusyRunner.Show("Error", App.Herdr.Detail ?? ""));
				break;
		}
	}

	private async void Rows_Tapped(object? sender, TappedEventArgs e)
	{
		if (sender is not BindableObject { BindingContext: InfoRow { Key.Length: > 0 } row }) { return; }
		await _run.RunAsync("Connection", async () =>
		{
			switch (row.Key)
			{
				case "copy":
					if (_publicLine.Length == 0) { return; }
					await Clipboard.Default.SetTextAsync(_publicLine);
					await BusyRunner.Show("This phone's key", i18n.Translate("Key copied. Add it to the machine's authorized SSH keys."));
					break;
				case "forget":
					string forget = i18n.Translate("Forget host key");
					if (await PopupsHandle.PopDisplayAsync(forget, i18n.Translate("Forget this machine's host key? The next connection will ask you to trust it again."), [forget, i18n.Translate("Cancel")], Destructive: true) != forget) { return; }
					RemoteSettings.SavePin("");
					HostKeyPrompt.Forget();
					ShowState();
					break;
			}
		});
	}
}
