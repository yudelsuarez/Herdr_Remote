using System.Globalization;
using System.Runtime.CompilerServices;
using HerdrRemote.Remote;
using Languages;

namespace HerdrRemote.Pages;

// Shared mapping from session state to card tone, glyph, title, body, buttons, and short status text
// for headers, Settings, and the link strip. Herdr hides the card when connected. On Connection, the form's
// Save and connect replaces setup, edit, retry, and connect buttons. ActionTapped leaves handling to the host.
public partial class ConnectionCard : ContentView
{
	// One button: the key raised through ActionTapped and its caption (a catalog key).
	private sealed record CardButton(string Key, string Caption);

	// What the card shows for one state. Tone: Late, Warn, Info or Ok. Title and Body are catalog keys; an empty Body hides the line.
	private sealed record Look(string Tone, string Glyph, string Title, string Body, CardButton[] Buttons);

	// The glyph that turns while a connect runs (Connecting, Reconnecting).
	private const string TurningGlyph = "";

	public ConnectionCard()
	{
		InitializeComponent();
	}

	// False on Herdr (list mode); Connection.xaml sets it.
	public bool PageMode { get; set; }

	// The sender is the card, the argument the tapped button's key.
	public event EventHandler<string>? ActionTapped;

	// Internal, not public: SessionState is internal to the app.
	internal static string StateWord(SessionState state, bool configured) => i18n.Translate(!configured || state == SessionState.NotConfigured ? "Not set up" : state switch
	{
		SessionState.Connected => "Connected",
		SessionState.Reconnecting => "Reconnecting…",
		SessionState.Unreachable => "Mac not reachable",
		SessionState.AuthFailed => "Key not accepted",
		SessionState.PasswordRefused => "Password not accepted",
		SessionState.HostKeyUnknown => "Not trusted yet",
		SessionState.HostKeyChanged => "Identity changed",
		SessionState.HerdrNotFound => "herdr not found",
		SessionState.HerdrNotRunning => "herdr not running",
		SessionState.Failed => "Connection failed",
		SessionState.DisconnectedByOwner => "Disconnected",
		// Connecting, and Disconnected while configured: the instant before a connect publishes Connecting.
		_ => "Connecting…",
	});

	internal static HeaderTone Tone(SessionState state, bool configured) => !configured || state == SessionState.NotConfigured ? HeaderTone.None : state switch
	{
		SessionState.Connected => HeaderTone.Ok,
		SessionState.Connecting or SessionState.Disconnected or SessionState.Reconnecting or SessionState.HostKeyUnknown => HeaderTone.Warn,
		SessionState.DisconnectedByOwner => HeaderTone.None,
		_ => HeaderTone.Late,
	};

	// The buttons shown last (key and caption): the same ones are not built again.
	private (string Key, string Caption)[]? _shown;

	// The glyphs turning now, each with its spinner (the card's, Pane's link strip).
	private static readonly ConditionalWeakTable<Label, Spinner> Spinners = new();

	// Animate only when the host is visible and the app is in the foreground.
	internal void Show(SessionState state, bool configured, bool onScreen)
	{
		bool setUp = configured && state != SessionState.NotConfigured;
		IsVisible = PageMode || !setUp || state != SessionState.Connected;
		CardButton retry = new("retry", "Try again");
		CardButton edit = new("edit", "Edit connection");
		CardButton disconnect = new("disconnect", "Disconnect");
		Look look = !setUp
			? new("Info", "", "Connect to your Mac", "Turn on Remote Login on the Mac, then enter its address and your user.", [new("setup", "Set up")])
			: state switch
			{
				SessionState.Connected => new("Ok", "", "Connected to {0}", "", [disconnect]),
				SessionState.Reconnecting => new("Warn", TurningGlyph, "Connection lost", "Trying again by itself. Your agents keep running on the Mac.", [disconnect]),
				SessionState.Unreachable => new("Late", "", "Mac not reachable", "Check that the Mac is on and awake and on the same network as this phone.", [retry, edit]),
				SessionState.AuthFailed => new("Late", "", "The Mac didn't accept this phone's key", "Type the Mac password once to add it.", [new("password", "Add key")]),
				SessionState.PasswordRefused => new("Late", "", "Password not accepted", "Type it again. The Mac must allow password sign-in.", [new("password", "Type it again")]),
				SessionState.HostKeyUnknown => new("Warn", "", "Trust this Mac?", "This phone hasn't connected to this Mac before.", [new("trust", "Review")]),
				SessionState.HostKeyChanged => new("Late", "", "This Mac's identity changed", "If you did not reinstall the Mac, do not continue.", [new("details", "Details"), edit]),
				SessionState.HerdrNotFound => new("Late", "", "herdr not found on the Mac", "Install herdr on the Mac, then try again.", [retry]),
				SessionState.HerdrNotRunning => new("Late", "", "herdr is not running", "Open herdr on the Mac, then try again.", [retry]),
				SessionState.Failed => new("Late", "", "Connection failed", "Something went wrong while connecting.", [retry, new("details", "Details")]),
				SessionState.DisconnectedByOwner => new("Info", "", "Disconnected", "You disconnected from this Mac.", [new("connect", "Connect")]),
				// Connecting, and Disconnected while configured: the instant before a connect publishes Connecting.
				_ => new("Info", TurningGlyph, "Connecting to your Mac…", "", []),
			};

		(string card, string ink) = look.Tone switch
		{
			"Warn" => ("Card.Work", "Work"),
			"Info" => ("Card.Acc", "Acc"),
			"Ok" => ("Card.Ok", "Ok"),
			_ => ("Card.Need", "Need"),
		};
		Card.Style = App.Res<Style>(card);
		Lbl_Glyph.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>(ink), App.Res<Color>(ink + "Dark"));
		Lbl_Glyph.Text = look.Glyph;
		// Only the Connected title has a {0} (the user and Mac); Format leaves the others as they are.
		Lbl_Title.Text = string.Format(CultureInfo.CurrentCulture, i18n.Translate(look.Title), $"{RemoteSettings.User}@{RemoteSettings.Host}");
		Lbl_Body.Text = look.Body.Length == 0 ? "" : i18n.Translate(look.Body);
		Lbl_Body.IsVisible = look.Body.Length > 0;

		// List mode has no Disconnect (Connected hides the card there); page mode leaves Set up, Edit connection, Try again and Connect
		// to the form's "Save and connect".
		CardButton[] shown = [.. look.Buttons.Where(button => PageMode ? button.Key is not ("setup" or "edit" or "retry" or "connect") : button.Key != "disconnect")];
		(string Key, string Caption)[] captions = [.. shown.Select(button => (button.Key, i18n.Translate(button.Caption)))];
		if (_shown is null || !captions.SequenceEqual(_shown))
		{
			_shown = captions;
			Buttons.Children.Clear();
			foreach ((string key, string caption) in captions)
			{
				Button button = new() { Style = App.Res<Style>("Btn.Outline"), Text = caption };
				button.Clicked += (_, _) => ActionTapped?.Invoke(this, key);
				Buttons.Children.Add(button);
			}
		}
		Buttons.IsVisible = shown.Length > 0;
		// Place one button beside the text and two below it, so their captions cannot squeeze the title.
		bool one = shown.Length == 1;
		Buttons.Style = App.Res<Style>(one ? "Card.Buttons" : "Card.ButtonsUnder");
		Grid.SetRow(Buttons, one ? 0 : 2);
		Grid.SetColumn(Buttons, one ? 2 : 1);
		Grid.SetRowSpan(Buttons, one ? 2 : 1);
		Grid.SetColumnSpan(Buttons, one ? 1 : 2);
		Buttons.VerticalOptions = one ? LayoutOptions.Center : LayoutOptions.Start;

		// Stopped when the card hides, the state moves on or the host is off screen or in the background.
		Turn(Lbl_Glyph, onScreen && IsVisible && look.Glyph == TurningGlyph);
	}

	// The host left the screen: the glyph stops (its next Show turns it again if the state still asks).
	internal void Still() => Turn(Lbl_Glyph, false);

	// The same turn as the agents' spinner (AgentList.RowView), shared with Pane's link strip, on the compositor (Spinner): on starts it
	// once, off stops it upright.
	internal static void Turn(Label glyph, bool on)
	{
		if (on) { Spinners.GetValue(glyph, label => new Spinner(label)).Start(); }
		else if (Spinners.TryGetValue(glyph, out Spinner? spinner)) { spinner.Stop(); }
	}

	// Start connecting outside the page's BusyRunner, which would stay busy for seconds.
	// StateChanged publishes results; Disconnect or restart cancels silently.
	public static async void StartConnect()
	{
		try { await App.Herdr.ConnectAsync(CancellationToken.None); }
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[ConnectionCard] connect unexpected: {ex}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
		}
	}
}
