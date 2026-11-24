namespace HerdrRemote.Pages;

// Bottom tabs set labels in ApplyLanguage so a language change can update them.
public partial class TabsFooter : ContentView
{
	public TabsFooter()
	{
		InitializeComponent();
		ApplyLanguage();
		Active = "Reader";
	}

	// The lit tab: "Herdr", "Reader" or "Voices". Each page sets its own.
	public string Active
	{
		get => field;
		set
		{
			field = value;
			Light(TabHerdr, IconHerdr, LabelHerdr, value == "Herdr");
			Light(TabReader, IconReader, LabelReader, value == "Reader");
			Light(TabVoices, IconVoices, LabelVoices, value == "Voices");
		}
	} = string.Empty;

	private static void Light(VerticalStackLayout tab, Label icon, Label text, bool on)
	{
		// The tab item is what a screen reader reaches (its icon and text are hidden by their styles): name plus selected state.
		SemanticProperties.SetDescription(tab, ChipRow.Spoken(text.Text, on));
		icon.Style = App.Res<Style>(on ? "Tab.IconOn" : "Tab.Icon");
		text.Style = App.Res<Style>(on ? "Tab.TextOn" : "Tab.Text");
	}

	public void ApplyLanguage()
	{
		LabelHerdr.Text = Languages.i18n.Translate("Herdr");
		LabelReader.Text = Languages.i18n.Translate("Reader");
		LabelVoices.Text = Languages.i18n.Translate("Settings");
		Active = Active;
	}

	private async void TabHerdr_Tapped(object? sender, TappedEventArgs e) => await GoAsync("Herdr");

	private async void TabReader_Tapped(object? sender, TappedEventArgs e) => await GoAsync("Reader");

	private async void TabVoices_Tapped(object? sender, TappedEventArgs e) => await GoAsync("Voices");

	// The lit tab of a pushed page goes back to its root.
	private async Task GoAsync(string tab)
	{
		if (Active == tab && (App.CurrentShell?.Navigation.NavigationStack.Count ?? 0) <= 1) { return; }
		try { await App.GoToAsync($"//{tab}"); }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TabsFooter] go {tab} unexpected: {ex}"); }
	}
}
