using System.Globalization;
using HerdrRemote.Remote;
using HerdrRemote.Services;
using Languages;

namespace HerdrRemote.Pages;

// Herdr page with a connection action and herdr-app-style AgentList: spaces, then agents.
// App.Herdr state drives the header tone and connection card; HerdrAgents events from herdr-core populate the list.
public partial class Herdr : ContentPage
{
	private readonly BusyRunner _run;

	// The first appearing starts the first connect; the card shows "Connecting…" meanwhile.
	private bool _started;

	// The spaces and agents, both parts in the page's scroll.
	private readonly AgentList List = new();

	// Between OnAppearing and OnDisappearing: off screen the list builds nothing, and the next appearing draws the current model.
	private bool _onScreen;

	// Refresh status durations every 30 seconds while visible and foregrounded, matching herdr-app.
	// Only rows whose text changes are updated.
	private readonly IDispatcherTimer _refresh;

	public Herdr()
	{
		InitializeComponent();
		_refresh = Dispatcher.CreateTimer();
		_refresh.Interval = TimeSpan.FromSeconds(30);
		_refresh.Tick += (_, _) => List.Show();
		Tabs.Active = "Herdr";
		Header.ActionGlyph = "";
		_run = new BusyRunner(IsBusyBox);
		App.ApplyKeyboardSafeArea(this);
		Body.Add(List.Spaces);
		Body.Add(List.Agents);
		List.Opened += List_Opened;
		List.Searched += () => _ = App.GoToAsync("SearchAll");
		// The list's "+", "⋯" and holds: a terminal started from here opens on the Pane screen.
		List.Asked += ask => _ = HerdrRemote.Pages.Arrange.AskAsync(new ArrangeHost(address => App.OpenPaneAsync(address)), ask);
		// The tab root lives as long as the app, so its handlers never need removing.
		App.Herdr.StateChanged += Session_StateChanged;
		App.Herdr.Agents.Changed += Agents_Changed;
	}

	private void Agents_Changed()
	{
		if (_onScreen) { List.Show(); }
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		_onScreen = true;
		if (Window is { } window) { window.Stopped += Window_Stopped; window.Resumed += Window_Resumed; }
		if (App.Herdr.Agents.Foreground) { _refresh.Start(); }
		SetLanguage();
		if (!_started)
		{
			_started = true;
			if (RemoteSettings.IsConfigured) { ConnectionCard.StartConnect(); }
		}
		try { await HostKeyPrompt.ShowPendingAsync(); }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Herdr] pending prompt unexpected: {ex}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
		}
	}

	// Off screen nothing turns, and the next appearing draws the rows again (AgentList.Stop).
	protected override void OnDisappearing()
	{
		Header.DetachReading();
		base.OnDisappearing();
		_onScreen = false;
		if (Window is { } window) { window.Stopped -= Window_Stopped; window.Resumed -= Window_Resumed; }
		_refresh.Stop();
		List.Stop();
		Card.Still();
	}

	// The app going to the background stops the 30 s refresh (nothing is drawn there: HerdrAgents raises no Changed, and a state
	// change shows only the card); the return starts it again, shows the list as it is now (equal rows draw nothing) and lets the
	// card's glyph turn again if its state still asks. App.CreateWindow's handlers run first, so Agents.Foreground is already the new
	// value here.
	private void Window_Stopped(object? sender, EventArgs e) => _refresh.Stop();

	private void Window_Resumed(object? sender, EventArgs e)
	{
		_refresh.Start();
		ShowConnection();
		List.Show();
	}

	private void SetLanguage()
	{
		Header.TitleText = i18n.Translate("Herdr");
		ShowConnection();
		List.Show();
		Tabs.ApplyLanguage();
	}

	// App-lifetime handler for automatic host-key prompts. Open them only over Herdr or Connection;
	// defer them on other pages. Do not use _run: a row action may already hold it.
	private async void Session_StateChanged()
	{
		ShowConnection();
		if (_onScreen && App.Herdr.Agents.Foreground) { List.Show(); }
		if (App.Herdr.State is not (SessionState.HostKeyUnknown or SessionState.HostKeyChanged)) { return; }
		try { await HostKeyPrompt.OfferAsync(App.CurrentShell?.CurrentPage is Herdr or Connection); }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Herdr] state popup unexpected: {ex}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
		}
	}

	// Refresh the header's connection status and dot, user and Mac subtitle, and connection card.
	private void ShowConnection()
	{
		SessionState state = App.Herdr.State;
		bool configured = RemoteSettings.IsConfigured;
		Header.ActionDescription = string.Format(CultureInfo.CurrentCulture, i18n.Translate("Connection: {0}"), ConnectionCard.StateWord(state, configured));
		Header.ActionTone = ConnectionCard.Tone(state, configured);
		Header.SubtitleText = configured ? $"{RemoteSettings.User}@{RemoteSettings.Host}" : "";
		Card.Show(state, configured, _onScreen && App.Herdr.Agents.Foreground);
	}

	// Never ignored, Connecting included: the page shows the connection whatever its state.
	private void Header_ActionTapped(object? sender, EventArgs e) => _ = App.GoToAsync("Connection");

	// Navigate and connect outside _run; only short popups use it, so a connection cannot hold the busy guard.
	private async void Card_ActionTapped(object? sender, string key)
	{
		switch (key)
		{
			case "edit" or "setup":
				_ = App.GoToAsync("Connection");
				break;
			case "password":
				_ = App.GoToAsync("Connection?focus=password");
				break;
			case "retry" or "connect":
				ConnectionCard.StartConnect();
				break;
			case "trust":
				await _run.RunAsync("Herdr", async () =>
				{
					if (await HostKeyPrompt.TrustAsync()) { ConnectionCard.StartConnect(); }
				});
				break;
			case "details":
				await _run.RunAsync("Herdr", () => App.Herdr.State == SessionState.HostKeyChanged ? HostKeyPrompt.ChangedAsync() : BusyRunner.Show("Error", App.Herdr.Detail ?? ""));
				break;
		}
	}

	// A pane or agent row of the list opens the Pane screen.
	private async void List_Opened(string address) => await _run.RunAsync("Herdr", () => App.OpenPaneAsync(address), showError: false);
}
