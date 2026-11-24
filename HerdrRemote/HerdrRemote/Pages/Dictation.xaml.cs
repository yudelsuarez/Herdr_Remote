using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Dictation engine ("device" or cloud), language, and model settings use stt.provider, stt.lang,
// and stt.{id}.model. They do not change the reading voice (tts.provider).
public partial class Dictation : ContentPage
{
	private readonly BusyRunner _run;
	private int _load;

	public Dictation()
	{
		InitializeComponent();
		_run = new(IsBusyBox);
		App.ApplyKeyboardSafeArea(this);
	}

	// "Device · Automatic": the line of the Settings row.
	internal static string Summary() => $"{SttProviders.Current.Name} · {LanguageName(SttProviders.LanguageSetting)}";

	private static string LanguageName(string setting) => setting switch
	{
		"es-US" => i18n.Translate("Spanish (United States)"),
		"en-US" => i18n.Translate("English (United States)"),
		_ => i18n.Translate("Automatic"),
	};

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		Header.TitleText = i18n.Translate("Dictation");
		Lbl_Note.Text = i18n.Translate("Dictation with the phone never leaves it. A provider uploads the recording when you stop.");
		await ShowAsync();
	}

	protected override void OnDisappearing()
	{
		_load++;
		Header.DetachReading();
		base.OnDisappearing();
	}

	private async Task ShowAsync()
	{
		int load = ++_load;
		Dictionary<string, bool?> keys = await TtsProviders.SavedKeysAsync();
		if (load != _load) { return; }
		ISpeechToText current = SttProviders.Current;
		List<InfoRow> rows =
		[
			new(current.Id == "device" ? "\uE972" : TtsProviders.Glyph(current.Id), i18n.Translate("Dictation provider"), current.Name, false, KeyLine(current, keys), Chevron: true, Key: "provider"),
			new("", i18n.Translate("Dictation language"), LanguageName(SttProviders.LanguageSetting), true, Chevron: true, Key: "lang"),
		];
		if (current.Id == "openai") { rows.Add(new("", i18n.Translate("Model"), SttProviders.Model("openai"), true, Chevron: true, Key: "model")); }
		Rows.SetItems(rows);
	}

	private static string KeyLine(ISpeechToText provider, IReadOnlyDictionary<string, bool?> keys) => !provider.NeedsKey ? i18n.Translate("No key needed")
		: keys.GetValueOrDefault(provider.Id) switch { true => i18n.Translate("Key saved"), false => i18n.Translate("No key saved"), _ => i18n.Translate("Key status unavailable") };

	private async void Rows_Tapped(object? sender, TappedEventArgs e)
	{
		if (sender is not BindableObject { BindingContext: InfoRow row }) { return; }
		await _run.RunAsync("Dictation", async () =>
		{
			switch (row.Key)
			{
				case "provider": await PickProviderAsync(); break;
				case "lang": await PickLanguageAsync(); break;
				case "model": await PickModelAsync(); break;
			}
			await ShowAsync();
		}, showError: false);
	}

	private static async Task PickProviderAsync()
	{
		Dictionary<string, bool?> keys = await TtsProviders.SavedKeysAsync();
		List<string> items = [.. SttProviders.All.Select(p => p.NeedsKey ? $"{p.Name} · {KeyLine(p, keys)}" : p.Name)];
		string? picked = await PopupsHandle.PopListPickerAsync(items, i18n.Translate("Dictation provider"), items[SttProviders.All.ToList().IndexOf(SttProviders.Current)]);
		int index = picked is null ? -1 : items.IndexOf(picked);
		if (index < 0) { return; }
		ISpeechToText provider = SttProviders.All[index];
		App.SaveSetting("stt.provider", provider.Id);
		if (provider.NeedsKey && keys.GetValueOrDefault(provider.Id) != true)
		{
			string providers = i18n.Translate("Providers and keys");
			string? answer = await PopupsHandle.PopDisplayAsync(i18n.Translate("Dictation"), SttFailure.MissingKey(provider.Name), [providers, i18n.Translate("Cancel")]);
			if (answer == providers) { await App.GoToAsync("Providers"); }
		}
	}

	private static async Task PickLanguageAsync()
	{
		string[] settings = ["auto", "es-US", "en-US"];
		List<string> items = [.. settings.Select(LanguageName)];
		string? picked = await PopupsHandle.PopListPickerAsync(items, i18n.Translate("Dictation language"), LanguageName(SttProviders.LanguageSetting));
		int index = picked is null ? -1 : items.IndexOf(picked);
		if (index >= 0) { App.SaveSetting("stt.lang", settings[index]); }
	}

	private static async Task PickModelAsync()
	{
		string[] models = ["gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1"];
		string? picked = await PopupsHandle.PopListPickerAsync([.. models], i18n.Translate("Model"), SttProviders.Model("openai"));
		if (picked is not null) { App.SaveSetting("stt.openai.model", picked); }
	}

	private void Back_Clicked(object? sender, EventArgs e) => _ = App.GoToAsync("..");
}
