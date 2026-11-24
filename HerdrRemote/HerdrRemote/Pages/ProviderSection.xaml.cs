using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Provider settings from provider.Rows(), with an API key field for cloud providers.
// Never put a saved key back into the Entry; the placeholder only reports whether a key exists.
public partial class ProviderSection : ContentView
{
	// Set by Bind in the page constructor, before the section is shown.
	private ITtsProvider _provider = null!;
	private BusyRunner _run = null!;
	private IsBusyBox _busyBox = null!;
	private CancellationTokenSource? _catalog;

	public ProviderSection()
	{
		InitializeComponent();
		SecretEye.Attach(En_Key, Eye_Key);
		Unloaded += (_, _) => _catalog?.Cancel();
	}

	// The page's runner (one double-tap guard for the whole page) and its busy box.
	internal void Bind(ITtsProvider provider, BusyRunner run, IsBusyBox busyBox)
	{
		_provider = provider;
		_run = run;
		_busyBox = busyBox;
		KeyGroup.IsVisible = provider.NeedsKey;
	}

	internal void Cancel() => _catalog?.Cancel();

	private void ShowRows()
	{
		List<InfoRow> rows = [.. _provider.Rows()];
		int voice = rows.FindIndex(row => row.Key == "voice"), model = rows.FindIndex(row => row.Key == "model");
		if (voice >= 0 && model > voice)
		{
			InfoRow row = rows[voice];
			rows.RemoveAt(voice);
			rows.Insert(model, row);
		}
		Rows.SetItems(rows);
		Btn_RefreshModels.BindingContext = rows.FirstOrDefault(row => row.Key == "model");
		Btn_RefreshModels.IsVisible = Btn_RefreshModels.BindingContext is InfoRow;
		Btn_RefreshModels.Text = i18n.Translate("Refresh") + " · " + i18n.Translate("Model");
	}

	// Texts, rows and the key placeholder (OnAppearing: a language change or a stored key shows on return).
	public async Task RefreshAsync()
	{
		Lbl_Title.Text = i18n.Translate("Access");
		Lbl_KeyLabel.Text = i18n.Translate("API key");
		Btn_SaveKey.Text = i18n.Translate("Save key");
		ShowRows();
		if (!_provider.NeedsKey) { return; }
		try
		{
			// Only tested for emptiness: the value is not shown, kept or logged.
			bool saved = !string.IsNullOrEmpty(await App.GetApiKeyAsync(_provider.Id));
			En_Key.Placeholder = i18n.Translate(saved ? "Key saved" : "Paste your API key");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Voices] key state {_provider.Id} unexpected: {ex.GetType().Name}");
			En_Key.Placeholder = i18n.Translate("Key status unavailable");
		}
	}

	private async void Btn_SaveKey_Clicked(object? sender, EventArgs e)
	{
		await _run.RunAsync("Voices", async () =>
		{
			string key = (En_Key.Text ?? "").Trim();
			if (key.Length == 0)
			{
				await BusyRunner.Show("Voices", i18n.Translate("Paste the API key first."));
				return;
			}
			await App.SaveApiKeyAsync(_provider.Id, key);
			_catalog?.Cancel();
			TtsProviders.InvalidateCatalog(_provider.Id);
			En_Key.Text = "";
			En_Key.Placeholder = i18n.Translate("Key saved");
		}, Btn_SaveKey);
	}

	private async void Rows_Tapped(object? sender, EventArgs e)
	{
		if (sender is not BindableObject { BindingContext: InfoRow { Key.Length: > 0 } row }) { return; }
		await _run.RunAsync("Voices", async () =>
		{
			if (row.Key == "voice")
			{
				await VoiceSheet.ShowAsync(_provider);
				ShowRows();
				return;
			}
			using CancellationTokenSource request = new();
			_catalog?.Cancel();
			_catalog = request;
			// The box is hidden before any popup.
			_busyBox.IsVisible = true;
			ChoiceList list;
			try
			{
				list = await TtsProviders.OptionsAsync(_provider, row.Key, request.Token, refresh: ReferenceEquals(sender, Btn_RefreshModels));
			}
			catch (OperationCanceledException) { return; }
			finally
			{
				// A throw reaches BusyRunner's error popup: the box goes first.
				_busyBox.IsVisible = false;
				if (_catalog == request) { _catalog = null; }
			}
			if (list.Error is not null)
			{
				await BusyRunner.Show("Voices", list.Error);
				return;
			}
			if (list.RefreshError is not null)
			{
				await BusyRunner.Show(list.Title, (list.Note ?? i18n.Translate("Saved list · Could not refresh")) + " · " + list.RefreshError);
			}
			if (list.ReadOnly)
			{
				await BusyRunner.Show(list.Title, string.Join(" · ", new[] { list.Note, string.Join(", ", list.Options.Select(o => o.Text)) }.Where(s => !string.IsNullOrEmpty(s))));
				return;
			}
			if (list.Options.Count > 0)
			{
				// ListSheet already returns a stable key, unlike ListPicker's label-only result.
				List<ListSheet.Filtered> choices = [.. list.Options.Select((option, index) => new ListSheet.Filtered(
					new ListSheet.Item(index.ToString(System.Globalization.CultureInfo.InvariantCulture), option.Text, option.Detail ?? "", "", 0,
						option.Id == list.SelectedId ? "\uE91C" : "", false, ListSheet.RowSubHeight), option.Text + " " + option.Detail))];
				if (list.Field is not null) { choices.Add(new(new ListSheet.Item("-1", list.Hint ?? list.Title, "", "", 0, "", false, ListSheet.RowSubHeight), "")); }
				string? picked = await ListSheet.PickFilteredAsync(list.Title, sheet => sheet.ShowFilter(list.Title, list.Title, choices,
					(shown, total) => $"{shown}/{total}", i18n.Translate("No voices found.")));
				if (picked is null) { return; }
				int index = int.Parse(picked, System.Globalization.CultureInfo.InvariantCulture);
				if (index >= 0)
				{
					ChoiceOption option = list.Options[index];
					_provider.Save(row.Key, option.Id, option.Text);
					ShowRows();
					return;
				}
			}
			if (list.Field is not null)
			{
				// A text row: FormSheet with the field, its hint and Save; Check keeps the sheet open with its error.
				FormSheet.Form form = new(list.Title, i18n.Translate("Save"), text =>
				{
					string typed = text.Trim();
					return Task.FromResult(list.Check?.Invoke(typed) is { } error ? new FormSheet.Outcome(error) : new FormSheet.Outcome(null, typed));
				}, Hint: list.Hint, Text: list.Field);
				string? saved = await FormSheet.PopAsync(form);
				if (saved is null) { return; }
				_provider.Save(row.Key, saved, saved);
				ShowRows();
				return;
			}
			await BusyRunner.Show("Voices", i18n.Translate(row.Key == "model" ? "No models returned." : "No voices found."));
		});
	}
}
