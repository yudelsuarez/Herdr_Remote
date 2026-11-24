using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using HerdrRemote;
using HerdrRemote.Pages;
using HerdrRemote.Services;
using Languages;

namespace Popups;

// Draft, in-use and audition state have separate identities; only Use writes preferences.
public partial class VoiceSheet : ContentView
{
	internal sealed class VoiceRow(ChoiceOption option, string? providerGlyph = null) : INotifyPropertyChanged
	{
		public ChoiceOption Option { get; } = option;
		public string? ProviderGlyph { get; } = providerGlyph;
		public bool IsVoice => ProviderGlyph is null;
		public string Name => Option.Text;
		public string Detail => IsInUse ? InUse : TtsProviders.ReportedLanguage(Option.Language) ?? "";
		public string StateDescription => string.Join(" · ", new[] { InUse, IsMarked ? i18n.Translate("Marked") : "" }.Where(text => text.Length > 0));
		public string ListenDescription => string.Format(i18n.Translate("Listen to {0}"), Name);
		public bool IsMarked { get; private set; }
		public bool IsInUse { get; set; }
		public bool IsPreviewing { get; private set; }
		public string Phase { get; private set; } = "";
		public string Mark => IsMarked ? "\uE91C" : "";
		public string InUse => IsInUse ? i18n.Translate("In use") : "";
		public string ListenGlyph => IsPreviewing ? "\uE92E" : "\uE950";
		public string ActionDescription => IsPreviewing ? i18n.Translate("Stop sample") : ListenDescription;
		public event PropertyChangedEventHandler? PropertyChanged;
		public void Marked(bool value)
		{
			if (IsMarked == value) { return; }
			IsMarked = value;
			PropertyChanged?.Invoke(this, new(nameof(IsMarked)));
			PropertyChanged?.Invoke(this, new(nameof(Mark)));
			PropertyChanged?.Invoke(this, new(nameof(StateDescription)));
		}
		public void Preview(string phase)
		{
			if (Phase == phase) { return; }
			Phase = phase;
			IsPreviewing = phase.Length > 0;
			foreach (string name in new[] { nameof(Phase), nameof(IsPreviewing), nameof(ListenGlyph), nameof(ActionDescription) })
			{ PropertyChanged?.Invoke(this, new(name)); }
		}
	}

	private sealed record SetupDraft(ITtsProvider Provider, ChoiceOption? Voice, string? Manual, bool Own);

	private readonly Action<SetupDraft?> _close;
	private readonly bool _canSetUp;
	private readonly ReadingSession _session = App.Reading;
	private readonly ObservableCollection<VoiceRow> _visible = [];
	private readonly List<VoiceRow> _rows = [];
	private ITtsProvider _provider;
	private SetupDraft? _returnDraft;
	private ChoiceList? _voices;
	private VoiceRow? _marked, _preview;
	private CancellationTokenSource? _catalog, _sample;
	private Window? _window;
	private BottomSheet? _host;
	private int _load, _listen;
	private long? _previewGeneration;
	private bool _providers, _closed, _committed, _filtering, _own;
	private string _language = "";

	private VoiceSheet(Action<SetupDraft?> close, ITtsProvider? provider, SetupDraft? draft)
	{
		_close = close;
		_canSetUp = provider is null;
		_provider = provider ?? draft?.Provider ?? TtsProviders.Current;
		_returnDraft = draft;
		_own = draft?.Own ?? false;
		InitializeComponent();
#if IOS || ANDROID
		// As with UnifiedEntry, the field border owns the decoration, not the native control.
		foreach (Picker picker in new[] { Pk_Language, Pk_Sample })
		{
			picker.HandlerChanged += (_, _) =>
			{
#if IOS
				if (picker.Handler?.PlatformView is UIKit.UITextField field)
				{
					field.BorderStyle = UIKit.UITextBorderStyle.None;
					field.BackgroundColor = UIKit.UIColor.Clear;
					field.TextAlignment = UIKit.UITextAlignment.Right;
				}
#elif ANDROID
				if (picker.Handler?.PlatformView is Android.Widget.EditText field)
				{
					field.Background = null;
					field.BackgroundTintList = null;
					field.SetIncludeFontPadding(false);
					field.SetPadding(0, 0, 0, 0);
					field.SetPaddingRelative(0, 0, 0, 0);
					field.Gravity = Android.Views.GravityFlags.End | Android.Views.GravityFlags.CenterVertical;
				}
#endif
			};
		}
#endif
		Reading.CanInteract = () => !_closed && Parent is not null && _host is { IsClosing: false, Parent: not null };
		Reading.BeforeOpenSource = async page =>
		{
			Done_Clicked(this, EventArgs.Empty);
			await PopupsHandle.CloseAllAsync(page);
		};
		ParentChanged += (_, _) =>
		{
			if (_host is not null) { return; }
			Element? host = Parent;
			while (host is not null and not BottomSheet) { host = host.Parent; }
			if (host is not BottomSheet sheet) { return; }
			_host = sheet;
			sheet.Closing += Host_Closing;
			if (sheet.IsClosing) { CloseWork(); }
		};
		Items.ItemsSource = _visible;
		Btn_Done.Text = i18n.Translate("Close");
		Lbl_Title.Text = i18n.Translate("Voice");
		Btn_Use.Text = i18n.Translate("Use this voice");
		Btn_More.Text = i18n.Translate("Load more");
		Btn_SetUp.Text = i18n.Translate("Set up");
		SemanticProperties.SetDescription(Btn_Refresh, i18n.Translate("Refresh"));
		Lbl_Provider.Text = i18n.Translate("Provider");
		Lbl_Sample.Text = i18n.Translate("Sample language");
		Lbl_Filter.Text = i18n.Translate("Filter by language");
		Lbl_Voices.Text = i18n.Translate("Voices");
		Pk_Sample.ItemsSource = new[] { i18n.Translate("English"), i18n.Translate("Spanish") };
		Pk_Sample.SelectedIndex = i18n.defaultLanguageCode.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
		SemanticProperties.SetDescription(En_Search, i18n.Translate("Search voices"));
		SemanticProperties.SetDescription(Btn_Clear, i18n.Translate("Clear search"));
		SemanticProperties.SetDescription(Pk_Language, i18n.Translate("Language"));
		SemanticProperties.SetDescription(Pk_Sample, i18n.Translate("Sample language"));
		Loaded += Sheet_Loaded;
		Unloaded += Sheet_Unloaded;
	}

	internal static async Task ShowAsync(ITtsProvider? provider = null)
	{
		if (App.CurrentShell is not AppShell shell
			|| shell.CurrentPage is not ContentPage { Content: Layout layout, Window: { } window } origin) { return; }
		bool AtOrigin() => ReferenceEquals(App.CurrentShell, shell) && ReferenceEquals(shell.CurrentPage, origin)
			&& ReferenceEquals(origin.Window, window) && ReferenceEquals(origin.Content, layout);

		SetupDraft? draft = null;
		while (AtOrigin())
		{
			draft = await PopupsHandle.PopBottomSheetAsync<SetupDraft>(close => new VoiceSheet(close, provider, draft),
				CanInsert: (page, root) => ReferenceEquals(page, origin) && ReferenceEquals(root, layout) && AtOrigin());
			// The result arrives only after the previous sheet is off the page.
			if (draft is null || !AtOrigin() || !await OpenSetupAsync(draft.Provider)) { return; }
		}

		async Task<bool> OpenSetupAsync(ITtsProvider selected)
		{
			TaskCompletionSource<bool> returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Page? setup = null;
			void Navigated(object? sender, ShellNavigatedEventArgs e)
			{
				if (setup is null) { setup = shell.CurrentPage as Provider; }
				else if (!ReferenceEquals(shell.CurrentPage, setup)) { returned.TrySetResult(AtOrigin()); }
			}
			void Destroying(object? sender, EventArgs e) => returned.TrySetResult(false);
			window.Destroying += Destroying;
			try
			{
				await shell.NavigateAsync(async () =>
				{
					shell.Navigated += Navigated;
					await shell.GoToAsync("Provider", new ShellNavigationQueryParameters { ["provider"] = selected.Id });
					setup ??= shell.CurrentPage as Provider;
				}, canStart: AtOrigin);
				return setup is not null && await returned.Task;
			}
			catch (OperationCanceledException) { return false; }
			finally
			{
				shell.Navigated -= Navigated;
				window.Destroying -= Destroying;
			}
		}
	}

	internal static string Summary()
	{
		ITtsProvider provider = TtsProviders.Current;
		string voice = provider.VoiceSummary();
		int cut = voice.IndexOf(" · ", StringComparison.Ordinal);
		if (cut >= 0) { voice = voice[..cut]; }
		cut = voice.IndexOf(" - ", StringComparison.Ordinal);
		if (cut >= 0) { voice = voice[..cut]; }
		return $"{provider.Name} · {voice}";
	}

	private async void Sheet_Loaded(object? sender, EventArgs e)
	{
		if (_closed) { return; }
		Reading.Attach();
		_window = Window;
		if (_window is { } window) { window.Stopped += Window_Stopped; }
		_session.Changed += Session_Changed;
		await LoadVoicesAsync();
	}

	private void Host_Closing(object? sender, EventArgs e) => CloseWork();

	private void CloseWork()
	{
		if (_closed) { return; }
		_closed = true;
		_load++;
		_catalog?.Cancel();
		StopSample();
		Reading.Detach();
	}

	private void Sheet_Unloaded(object? sender, EventArgs e)
	{
		CloseWork();
		Reading.Detach();
		if (_host is { } host) { host.Closing -= Host_Closing; }
		_session.Changed -= Session_Changed;
		if (_window is { } window) { window.Stopped -= Window_Stopped; }
		_window = null;
	}

	private void Window_Stopped(object? sender, EventArgs e) => StopSample();

	private void Sheet_SizeChanged(object? sender, EventArgs e)
	{
		if (_closed || Height <= 0) { return; }
		double chrome = Math.Max(0, SheetHead.Height)
			+ (ReadingSlot.IsVisible && Reading.IsVisible ? Math.Max(0, Reading.Height) : 0)
			+ (ProviderCard.IsVisible ? Math.Max(0, ProviderCard.Height) : 0)
			+ (SearchRow.IsVisible ? Math.Max(0, SearchRow.Height) : 0)
			+ (LanguageRow.IsVisible ? Math.Max(0, LanguageRow.Height) : 0)
			+ (ManualRow.IsVisible ? Math.Max(0, ManualRow.Height) : 0);
		double room = Math.Max(48, Height - chrome - 64);
		Notes.MaximumHeightRequest = Math.Max(48, room - Btn_Use.MinimumHeightRequest - FooterScroll.RowSpacing);
	}

	private async Task LoadVoicesAsync(bool refresh = false, bool more = false)
	{
		if (_closed) { return; }
		int load = ++_load;
		_catalog?.Cancel();
		using CancellationTokenSource request = new();
		_catalog = request;
		ITtsProvider provider = _provider;
		Btn_Refresh.IsVisible = true;
		Btn_Refresh.IsEnabled = Btn_More.IsEnabled = false;
		Lbl_Note.Text = i18n.Translate("Loading");
		Lbl_ProviderName.Text = provider.Name;
		Lbl_Mark.Text = TtsProviders.Glyph(provider.Id);
		SetUpRow.IsVisible = _canSetUp && !_providers && provider.Id != "device";
		try
		{
			ChoiceList list = await TtsProviders.OptionsAsync(provider, "voice", request.Token, refresh: refresh,
				continuation: more ? _voices?.Continuation : null, own: _own);
			if (_closed || load != _load) { return; }
			_voices = list;
			if (list.Error is not null) { Lbl_Note.Text = list.Error; return; }
			SetupDraft? draft = _returnDraft;
			_returnDraft = null;
			ChoiceOption? marked = _marked?.Option ?? draft?.Voice;
			string? markedId = marked?.Id ?? list.SelectedId;
			if (!more)
			{
				_rows.Clear();
				_marked = null;
			}
			foreach (ChoiceOption option in list.Options)
			{
				if (_rows.Any(row => row.Option.Id == option.Id)) { continue; }
				VoiceRow row = new(option) { IsInUse = provider == TtsProviders.Current && option.Id == list.SelectedId };
				_rows.Add(row);
				if (option.Id == markedId) { Mark(row); }
			}
			if (_marked is null && list.Field is null && markedId is { Length: > 0 })
			{
				Mark(new(marked ?? new(markedId, App.Setting($"tts.{provider.Id}.voice_name", markedId))));
			}
			ManualRow.IsVisible = list.Field is not null;
			if (!more && list.Field is not null)
			{
				if (draft is not null) { En_Manual.TextChanged -= Manual_Changed; }
				En_Manual.Text = draft?.Manual ?? list.Field;
				if (draft is not null) { En_Manual.TextChanged += Manual_Changed; }
				En_Manual.Placeholder = list.Hint ?? i18n.Translate("Enter voice ID");
				if (draft is { Voice: null, Manual: "" }) { _marked?.Marked(false); _marked = null; }
				else if (_marked is null && draft?.Voice is { } voice) { Mark(new(voice)); }
				else if (_marked is null && list.Field.Length > 0) { Mark(new(new(list.Field, list.Field))); }
			}
			_filtering = true;
			List<string> languages = _rows.SelectMany(row => (row.Option.Languages ?? []).Prepend(row.Option.Language ?? ""))
				.Where(value => TtsProviders.ReportedLanguage(value) is not null).Distinct().Order().ToList();
			Pk_Language.ItemsSource = new[] { i18n.Translate("All") }.Concat(languages).ToList();
			if (!languages.Contains(_language)) { _language = ""; }
			Pk_Language.SelectedItem = _language.Length == 0 ? i18n.Translate("All") : _language;
			LanguageRow.IsVisible = provider.Id != "openai" && languages.Count > 0;
			SearchRow.IsVisible = provider.Id != "openai";
			En_Search.Placeholder = i18n.Translate(languages.Count > 0 ? "Search by name or language" : "Search by name");
			_filtering = false;
			Btn_Scope.IsVisible = provider.Id == "fish";
			Btn_Scope.Text = i18n.Translate(_own ? "Your voices" : "Public voices");
			Btn_More.IsVisible = list.Continuation is not null;
			Lbl_Partial.Text = list.Partial ? i18n.Translate("Showing part of the catalog") : "";
			if (list.Field is null && list.SelectedId is { Length: > 0 } selected && !_rows.Any(row => row.Option.Id == selected))
			{
				string savedName = App.Setting($"tts.{provider.Id}.voice_name", selected);
				Lbl_Partial.Text = i18n.Translate("Saved voice not in this list") + " · "
					+ (savedName == selected ? selected : $"{savedName} · {selected}");
			}
			Lbl_Note.Text = list.RefreshError ?? "";
			if (!more)
			{
				string? language = App.Setting($"tts.{provider.Id}.lang", "auto");
				if (language == "auto") { language = _marked?.Option.Language; }
				if (language?.StartsWith("es", StringComparison.OrdinalIgnoreCase) == true) { Pk_Sample.SelectedIndex = 1; }
				else if (language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true) { Pk_Sample.SelectedIndex = 0; }
			}
			ProjectRows();
			ShowMarked();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[VoiceSheet] catalog: {ex.GetType().Name}");
			if (!_closed && load == _load) { Lbl_Note.Text = TtsFailure.Message(TtsFailureKind.Unknown, true); }
		}
		finally
		{
			if (_catalog == request)
			{
				_catalog = null;
				if (!_closed) { Btn_Refresh.IsEnabled = Btn_More.IsEnabled = true; }
			}
			if (!_closed && load == _load)
			{
				Element? host = Parent;
				while (host is not null and not BottomSheet) { host = host.Parent; }
				(host as BottomSheet)?.Refit(En_Search.IsFocused);
			}
		}
	}

	private void ProjectRows()
	{
		if (_closed || _providers) { return; }
		string query = En_Search.Text ?? "";
		List<VoiceRow> wanted = _rows.Where(row =>
			(_language.Length == 0 || row.Option.Language == _language || row.Option.Languages?.Contains(_language) == true)
			&& (query.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(
				row.Name + " " + row.Option.Language + " " + string.Join(" ", row.Option.Languages ?? []), query,
				CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)).ToList();
		for (int i = 0; i < wanted.Count; i++)
		{
			if (i < _visible.Count && ReferenceEquals(_visible[i], wanted[i])) { continue; }
			int old = _visible.IndexOf(wanted[i]);
			if (old >= 0) { _visible.Move(old, i); } else { _visible.Insert(i, wanted[i]); }
		}
		while (_visible.Count > wanted.Count) { _visible.RemoveAt(_visible.Count - 1); }
		Items.MaximumHeightRequest = En_Search.IsFocused ? double.PositiveInfinity : Math.Min(Math.Max(1, wanted.Count), 5) * 64;
		if (wanted.Count == 0) { Lbl_Note.Text = i18n.Translate("No matching voices."); }
		else if (Lbl_Note.Text == i18n.Translate("No matching voices.")) { Lbl_Note.Text = _voices?.RefreshError ?? ""; }
	}

	private void Mark(VoiceRow row)
	{
		if (ReferenceEquals(row, _marked)) { return; }
		_marked?.Marked(false);
		_marked = row;
		row.Marked(true);
		ShowMarked();
	}

	private void ShowMarked()
	{
		if (_closed) { return; }
		Btn_Use.IsEnabled = _marked is not null && !_providers;
		Sheet_SizeChanged(this, EventArgs.Empty);
	}

	private async Task ListenAsync(VoiceRow row)
	{
		if (_closed) { return; }
		if (row.IsPreviewing) { StopSample(); return; }
		int listen = ++_listen;
		StopSample(invalidate: false);
		ITtsProvider provider = _provider;
		SpeechSettings saved = provider.Capture();
		if (_session.IsReading && !_session.IsPreview)
		{
			string confirm = i18n.Translate("Stop reading and listen");
			if (await PopupsHandle.PopDisplayAsync(confirm, row.Name, [confirm, i18n.Translate("Cancel")]) != confirm) { return; }
		}
		if (_closed || listen != _listen) { return; }
		if (_voices?.Check?.Invoke(row.Option.Id) is { } error) { Lbl_Note.Text = error; return; }
		using CancellationTokenSource request = new();
		_sample = request;
		try
		{
			Locale? locale = null;
			if (provider.Id == "device" && row.Option.Id.Length > 0)
			{
				locale = (await TextToSpeech.Default.GetLocalesAsync().WaitAsync(request.Token)).FirstOrDefault(value =>
					value.Id == row.Option.Id || value.Id.Length == 0 && value.Name == row.Option.Id);
			}
			if (_closed || listen != _listen || request.IsCancellationRequested) { return; }
			SpeechSettings draft = new(saved.ProviderId, saved.Model, row.Option.Id, row.Name, saved.Endpoint, saved.Region,
				saved.Language, saved.InputLimit, saved.LimitInBytes, saved.VerifiedLimit,
				Task.FromResult(locale), saved.CredentialAsync(CancellationToken.None));
			_preview = row;
			Task playing = _session.PreviewAsync(provider, draft, Pk_Sample.SelectedIndex == 1 ? "es" : "en");
			_previewGeneration = _session.Generation;
			Session_Changed();
			await playing;
			if (_closed || listen != _listen) { return; }
			if (_session.TakeError() is { } failure) { Lbl_Note.Text = failure; }
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[VoiceSheet] sample: {ex.GetType().Name}");
			if (!_closed && listen == _listen) { Lbl_Note.Text = TtsFailure.Message(TtsFailureKind.Unknown); }
		}
		finally
		{
			if (_sample == request)
			{
				_sample = null;
				_preview?.Preview("");
				_preview = null;
				_previewGeneration = null;
				if (!_closed)
				{
					if (Lbl_Note.Text == i18n.Translate("Generating sample…") || Lbl_Note.Text == i18n.Translate("Playing sample"))
					{ Lbl_Note.Text = _voices?.RefreshError ?? ""; }
					ShowMarked();
				}
			}
		}
	}

	private void StopSample(bool invalidate = true)
	{
		if (invalidate) { _listen++; }
		_sample?.Cancel();
		if (_previewGeneration == _session.Generation && _session.IsPreview) { _session.Stop(); }
		_previewGeneration = null;
		_preview?.Preview("");
		_preview = null;
		ShowMarked();
	}

	private void Session_Changed()
	{
		if (_closed || _preview is null || _previewGeneration != _session.Generation) { return; }
		_preview.Preview(_session.IsReading ? _session.Phase : "");
		if (_preview.IsPreviewing)
		{
			Lbl_Note.Text = i18n.Translate(_session.Phase == "waiting" ? "Generating sample…" : "Playing sample");
		}
		ShowMarked();
	}

	private void Item_SelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_closed) { return; }
		if (e.CurrentSelection.FirstOrDefault() is not VoiceRow row) { return; }
		Items.SelectedItem = null;
		if (_providers)
		{
			_provider = TtsProviders.All.First(provider => provider.Id == row.Option.Id);
			_returnDraft = null;
			_marked = null;
			_voices = null;
			_rows.Clear();
			_providers = false;
			_visible.Clear();
			En_Search.Text = "";
			_language = "";
			_own = false;
			ShowMarked();
			_ = LoadVoicesAsync();
			return;
		}
		Mark(row);
	}

	private void Provider_Tapped(object? sender, TappedEventArgs e)
	{
		if (_closed) { return; }
		StopSample();
		_load++;
		_catalog?.Cancel();
		_providers = true;
		_visible.Clear();
		ITtsProvider current = TtsProviders.Current;
		foreach (ITtsProvider provider in TtsProviders.All)
		{
			_visible.Add(new(new(provider.Id, provider.Name), TtsProviders.Glyph(provider.Id)) { IsInUse = provider == current });
		}
		Items.MaximumHeightRequest = 5 * 64;
		SearchRow.IsVisible = LanguageRow.IsVisible = ManualRow.IsVisible = false;
		SetUpRow.IsVisible = false;
		Btn_More.IsVisible = Btn_Scope.IsVisible = Btn_Refresh.IsVisible = false;
		ShowMarked();
	}

	private void SetUp_Clicked(object? sender, EventArgs e)
	{
		if (_closed || !SetUpRow.IsVisible || _host is not { IsClosing: false }) { return; }
		ChoiceOption? draft = _marked?.Option;
		if (draft?.Id == _voices?.SelectedId) { draft = null; }
		string? manual = ManualRow.IsVisible && En_Manual.Text != _voices?.Field ? En_Manual.Text ?? "" : null;
		CloseWork();
		_close(new(_provider, draft ?? _returnDraft?.Voice, manual ?? _returnDraft?.Manual, _own));
	}

	private void Search_FocusChanged(object? sender, FocusEventArgs e)
	{
		if (_closed) { return; }
		ReadingSlot.IsVisible = ProviderCard.IsVisible = FooterScroll.IsVisible = !e.IsFocused;
		ProjectRows();
		Sheet_SizeChanged(this, EventArgs.Empty);
		Element? host = Parent;
		while (host is not null and not BottomSheet) { host = host.Parent; }
		(host as BottomSheet)?.Refit(e.IsFocused);
	}

	private void Search_Changed(object? sender, TextChangedEventArgs e) => ProjectRows();
	private void Clear_Clicked(object? sender, EventArgs e) { if (!_closed) { En_Search.Text = ""; } }
	private void Language_Changed(object? sender, EventArgs e)
	{
		if (_closed || _filtering) { return; }
		_language = Pk_Language.SelectedIndex > 0 ? Pk_Language.SelectedItem as string ?? "" : "";
		ProjectRows();
	}
	private void Sample_Changed(object? sender, EventArgs e) => StopSample();
	private void Manual_Changed(object? sender, TextChangedEventArgs e)
	{
		if (_closed) { return; }
		string id = (En_Manual.Text ?? "").Trim();
		if (id.Length > 0) { Mark(_rows.FirstOrDefault(row => row.Option.Id == id) ?? new(new(id, id))); }
		else { _marked?.Marked(false); _marked = null; ShowMarked(); }
	}
	private async void Listen_Clicked(object? sender, EventArgs e)
	{
		if (sender is BindableObject { BindingContext: VoiceRow row } && !_providers) { await ListenAsync(row); }
	}
	private async void Refresh_Clicked(object? sender, EventArgs e) { if (_closed) { return; } StopSample(); _providers = false; await LoadVoicesAsync(refresh: true); }
	private async void More_Clicked(object? sender, EventArgs e) => await LoadVoicesAsync(more: true);
	private async void Scope_Clicked(object? sender, EventArgs e) { if (_closed) { return; } StopSample(); _own = !_own; await LoadVoicesAsync(refresh: true); }

	private void Use_Clicked(object? sender, EventArgs e)
	{
		if (_closed || _committed || _providers || _marked is not { } marked) { return; }
		if (_voices?.Check?.Invoke(marked.Option.Id) is { } error) { Lbl_Note.Text = error; return; }
		_committed = true;
		_provider.Save("voice", marked.Option.Id, marked.Name);
		App.SaveSetting("tts.provider", _provider.Id);
		Done_Clicked(sender, e);
	}

	private void Done_Clicked(object? sender, EventArgs e)
	{
		if (_closed) { return; }
		CloseWork();
		_close(null);
	}
}
