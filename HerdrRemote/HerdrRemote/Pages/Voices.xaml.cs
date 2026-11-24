using HerdrRemote.Remote;
using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Settings for the Mac connection, appearance, language, and reading voice; Providers manages API keys.
public partial class Voices : ContentPage
{
	private readonly BusyRunner _run;

	public Voices()
	{
		InitializeComponent();
		Tabs.Active = "Voices";
		_run = new BusyRunner(IsBusyBox);
		(App.AppThemeSetting switch { "Light" => Rb_ThemeLight, "Dark" => Rb_ThemeDark, _ => Rb_ThemeSystem }).IsChecked = true;
	}

	private void Appearance_CheckedChanged(object? sender, CheckedChangedEventArgs e)
	{
		if (e.Value && sender is RadioButton { Value: string theme }) { App.ApplyAppTheme(theme); }
	}

	// Offer every shipped language; apply the selection immediately and persist it.
	private async void LanguageRow_Tapped(object? sender, TappedEventArgs e)
	{
		await _run.RunAsync("Voices", async () =>
		{
			string? picked = await PopupsHandle.PopListPickerAsync([.. i18n.AvilableLanguage.Values.Select(l => l.Name)], i18n.Translate("Language"), i18n.AvilableLanguage[i18n.defaultLanguageCode].Name);
			if (picked is null) { return; }
			// First equal name wins on a duplicate.
			string code = i18n.AvilableLanguage.FirstOrDefault(l => l.Value.Name == picked).Key;
			if (code == i18n.defaultLanguageCode) { return; }
			App.SavePreferLanguage(code);
			SetLanguage();
		});
	}

	private async void ConnectionRow_Tapped(object? sender, TappedEventArgs e) => await _run.RunAsync("Connection", () => App.GoToAsync("Connection"), showError: false);
	private async void KeysRow_Tapped(object? sender, TappedEventArgs e) => await _run.RunAsync("Shortcuts", () => App.GoToAsync("Shortcuts"), showError: false);

	// Reader voice opens the voice sheet (as the Reader's pill does) and redraws the rows when it closes; Providers and keys opens its page.
	private async void VoiceRows_Tapped(object? sender, TappedEventArgs e)
	{
		if (sender is not BindableObject { BindingContext: InfoRow row }) { return; }
		if (row.Key == "dictation")
		{
			await _run.RunAsync("Dictation", () => App.GoToAsync("Dictation"), showError: false);
			return;
		}
		if (row.Key == "providers")
		{
			await _run.RunAsync("Providers", () => App.GoToAsync("Providers"), showError: false);
			return;
		}
		await _run.RunAsync("Voices", async () =>
		{
			await VoiceSheet.ShowAsync();
			await ShowVoiceAsync();
		});
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		App.Herdr.StateChanged += Session_StateChanged;
		SetLanguage();
	}

	protected override void OnDisappearing()
	{
		App.Herdr.StateChanged -= Session_StateChanged;
		Header.DetachReading();
		base.OnDisappearing();
	}

	// The state changes off the UI thread; only the Connection row follows it.
	private void Session_StateChanged() => MainThread.BeginInvokeOnMainThread(ShowConnection);

	private void ShowConnection()
	{
		string sub = RemoteSettings.IsConfigured ? $"{RemoteSettings.User}@{RemoteSettings.Host}" : "";
		ConnectionRow.SetItems([new("\uE930", i18n.Translate("Mac"), ConnectionCard.StateWord(App.Herdr.State, RemoteSettings.IsConfigured), false, sub, Chevron: true, Key: "connection", ValueTone: ConnectionCard.Tone(App.Herdr.State, RemoteSettings.IsConfigured) switch
		{
			HeaderTone.Ok => ValueTone.Ok,
			HeaderTone.Warn => ValueTone.Work,
			HeaderTone.Late => ValueTone.Need,
			_ => ValueTone.None,
		})]);
	}

	// Reader voice (the provider and voice of the current setting) and Providers and keys (how many providers have a key saved).
	private async Task ShowVoiceAsync()
	{
		string saved = TtsProviders.KeySummary(await TtsProviders.SavedKeysAsync());
		VoiceRows.SetItems(
		[
			new("\uE92D", i18n.Translate("Reader voice"), VoiceSheet.Summary(), false, Chevron: true, Key: "voice"),
			new("\uE972", i18n.Translate("Dictation"), Dictation.Summary(), true, Chevron: true, Key: "dictation"),
			new("\uE923", i18n.Translate("Providers and keys"), saved, true, Chevron: true, Key: "providers"),
		]);
	}

	private void SetLanguage()
	{
		Header.TitleText = i18n.Translate("Settings");
		Lbl_Connection.Text = i18n.Translate("Connection");
		ShowConnection();
		Lbl_Appearance.Text = i18n.Translate("Appearance");
		Rb_ThemeLight.Content = i18n.Translate("Light");
		Rb_ThemeSystem.Content = i18n.Translate("System");
		Rb_ThemeDark.Content = i18n.Translate("Dark");
		LanguageRow.SetItems([new("\uE924", i18n.Translate("Language"), i18n.AvilableLanguage[i18n.defaultLanguageCode].Name, false, Chevron: true, Key: "Language")]);
		Lbl_Keys.Text = i18n.Translate("Keys");
		KeysRow.SetItems([new("\uE92F", i18n.Translate("Shortcuts"), "", false, i18n.Translate("D-pad corners and key bar"), Chevron: true)]);
		Lbl_Voice.Text = i18n.Translate("Voice");
		_ = ShowVoiceAsync();
		Lbl_About.Text = i18n.Translate("About");
		AboutRows.SetItems(
		[
			new("", i18n.Translate("App"), "Remote Shell", false),
			new("", i18n.Translate("Version"), App.VersionText, true),
			new("", i18n.Translate("Copyright"), App.CopyrightText, true),
		]);
		Tabs.ApplyLanguage();
		IsBusyBox.BoxTitle = i18n.Translate("Loading");
		IsBusyBox.BoxBody = i18n.Translate("Please wait.");
	}
}
