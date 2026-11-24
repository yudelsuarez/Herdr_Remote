using HerdrRemote.Services;
using Languages;

namespace HerdrRemote.Pages;

public partial class Provider : ContentPage, IQueryAttributable
{
	private ITtsProvider? _provider;
	private readonly BusyRunner _run;

	public Provider()
	{
		InitializeComponent();
		_run = new(IsBusyBox);
		App.ApplyKeyboardSafeArea(this);
		App.KeepFocusedFieldVisible(Scroll);
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		_provider = query.TryGetValue("provider", out object? id) ? TtsProviders.All.FirstOrDefault(p => p.Id == id as string) : null;
		if (_provider is null) { return; }
		Header.TitleText = _provider.Name;
		Lbl_Mark.Text = TtsProviders.Glyph(_provider.Id);
		Section.Bind(_provider, _run, IsBusyBox);
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		if (_provider is not null) { await Section.RefreshAsync(); }
	}

	protected override void OnDisappearing()
	{
		Section.Cancel();
		Header.DetachReading();
		base.OnDisappearing();
	}

	private void Back_Clicked(object? sender, EventArgs e) => _ = App.GoToAsync("..");
}
