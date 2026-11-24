using HerdrRemote.Services;
using Languages;

namespace HerdrRemote.Pages;

// Browsing a provider never changes the saved reading provider.
public partial class Providers : ContentPage
{
	private readonly BusyRunner _run;
	private int _load;

	public Providers()
	{
		InitializeComponent();
		_run = new(IsBusyBox);
		App.ApplyKeyboardSafeArea(this);
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		int load = ++_load;
		Header.TitleText = i18n.Translate("Providers and keys");
		Dictionary<string, bool?> keys = await TtsProviders.SavedKeysAsync();
		if (load != _load) { return; }
		Lbl_Keys.Text = TtsProviders.KeySummary(keys);
		Rows.SetItems(TtsProviders.All.Select((provider, index) => new InfoRow(TtsProviders.Glyph(provider.Id),
			provider.Name, provider == TtsProviders.Current ? i18n.Translate("Selected") : "", index > 0,
			provider.VoiceSummary() + " · " + i18n.Translate(!provider.NeedsKey ? "No key needed" : keys[provider.Id] switch
			{ true => "Key saved", false => "No key saved", _ => "Key status unavailable" }), Chevron: true, Key: provider.Id)).ToList());
	}

	protected override void OnDisappearing()
	{
		_load++;
		Header.DetachReading();
		base.OnDisappearing();
	}

	private async void Rows_Tapped(object? sender, TappedEventArgs e)
	{
		if (sender is not BindableObject { BindingContext: InfoRow row }) { return; }
		await _run.RunAsync("Providers", () => App.GoToAsync("Provider", new Dictionary<string, object> { ["provider"] = row.Key }), showError: false);
	}

	private void Back_Clicked(object? sender, EventArgs e) => _ = App.GoToAsync("..");
}
