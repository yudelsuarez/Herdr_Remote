using Languages;

namespace HerdrRemote.Services;

// Connects to an HTTPS OpenAI-compatible server with an optional key. Discovers Supertonic's loaded model and styles;
// other servers use model discovery or manual IDs. Requests speech as WAV.
internal sealed class SelfHostedTts : ITtsProvider
{
	// Stored language values and catalog keys for their labels; "auto" omits lang from the request.
	private static readonly List<string> Langs = ["auto", "es", "en"];
	private static readonly List<string> LangNames = ["Automatic", "Spanish", "English"];

	public string Id => "selfhosted";
	public string Name => i18n.Translate("Your server");
	// Shows the key field, but sends a key only if one is saved.
	public bool NeedsKey => true;
	public bool CanPause => true;

	private static string BaseUrl => App.Setting("tts.selfhosted.base_url", "");
	private static string Model => App.Setting("tts.selfhosted.model", "supertonic-3");
	private static string Voice => App.Setting("tts.selfhosted.voice", "M3");
	private static string Lang => App.Setting("tts.selfhosted.lang", "auto");

	private static string LangText => i18n.Translate(LangNames[Math.Max(0, Langs.IndexOf(Lang))]);

	public string VoiceSummary() => $"{Voice} · {Model}";

	public IReadOnlyList<Pages.InfoRow> Rows() =>
	[
		new("", i18n.Translate("Base URL"), BaseUrl, false, Chevron: true, Key: "base_url"),
		new("", i18n.Translate("Model"), Model, true, Chevron: true, Key: "model"),
		new("", i18n.Translate("Voice"), Voice, true, Chevron: true, Key: "voice"),
		new("", i18n.Translate("Language"), LangText, true, Chevron: true, Key: "lang"),
	];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		ct.ThrowIfCancellationRequested();
		if (rowKey == "base_url") { return new(i18n.Translate("Base URL"), [], null, null, BaseUrl, i18n.Translate("e.g. https://tts.example.com"), CheckBaseUrl); }
		if (rowKey == "lang") { return new(i18n.Translate("Language"), [.. Langs.Select((l, i) => new ChoiceOption(l, i18n.Translate(LangNames[i])))], Lang, null); }
		string title = i18n.Translate(rowKey == "model" ? "Model" : "Voice");
		string selected = rowKey == "model" ? Model : Voice;
		string endpoint = BaseUrl;
		if (CheckBaseUrl(endpoint) is { } invalid) { return new(title, [], selected, invalid); }
		string? key = await App.GetApiKeyAsync(Id).WaitAsync(ct);
		var health = await App.Tts.ServerHealthAsync(endpoint, key, ct);
		if (health.Status is >= 200 and <= 299)
		{
			if (health.Error is not null || string.IsNullOrWhiteSpace(health.Value?.Model)) { return new(title, [], selected, TtsFailure.Message(TtsFailureKind.Catalog)); }
			if (rowKey == "model") { return new(title, [new(health.Value.Model, health.Value.Model)], selected, null, Note: i18n.Translate("Model loaded on server"), ReadOnly: true); }
			var styles = await App.Tts.ServerStylesAsync(endpoint, key, ct);
			if (styles.Status is >= 200 and <= 299)
			{
				if (styles.Error is not null || styles.Value?.Styles is not { } rows || rows.Any(s => s is null || string.IsNullOrEmpty(s.Name))) { return new(title, [], selected, TtsFailure.Message(TtsFailureKind.Catalog)); }
				return new(title, [.. rows.Select(s => new ChoiceOption(s.Name!, s.Name!, s.Kind))], selected, null,
					Field: selected, Hint: i18n.Translate("Enter voice ID"), Note: i18n.Translate("Catalog from server"));
			}
			if (styles.Status is not (404 or 405)) { return new(title, [], selected, TtsFailure.ListError(styles.Status, styles.Error)); }
		}
		else if (health.Status is not (404 or 405)) { return new(title, [], selected, TtsFailure.ListError(health.Status, health.Error)); }
		var models = await App.Tts.OpenAiModelsAsync(key, ct, endpoint);
		if (models.Status is 404 or 405) { return new(title, [], selected, null, Field: selected, Note: i18n.Translate("Availability not checked")); }
		if (TtsFailure.ListError(models.Status, models.Error) is { } error) { return new(title, [], selected, error); }
		if (models.Value?.Data is not { } list || list.Any(m => m is null || string.IsNullOrEmpty(m.Id))) { return new(title, [], selected, TtsFailure.Message(TtsFailureKind.Catalog)); }
		return new(title, rowKey == "model" ? [.. list.Where(m => (m.Capabilities?.TextToSpeech ?? m.Capabilities?.Speech) != false)
			.Select(m => new ChoiceOption(m.Id!, m.Id!, SpeechSupported: m.Capabilities?.TextToSpeech ?? m.Capabilities?.Speech, Shutdown: m.ShutdownDate))] : [],
			selected, null, Field: selected, Hint: i18n.Translate(rowKey == "model" ? "Enter model ID" : "Enter voice ID"), Note: i18n.Translate("Availability not checked"));
	}

	// rowKey is the setting suffix; value is the stored value.
	public void Save(string rowKey, string value, string text)
	{
		App.SaveSetting($"tts.selfhosted.{rowKey}", value);
		if (rowKey == "base_url") { TtsProviders.InvalidateCatalog(Id); }
	}

	public Task<string?> ReadAsync(string text, Action playing, CancellationToken ct) => Prepare(text, ct).PlayAsync(playing, ct);

	public SpeechSettings Capture() => new(Id, Model, Voice, endpoint: BaseUrl, language: Lang == "auto" ? null : Lang);

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) => new CloudSpeech(OpenAsync(text, settings, ct), nameof(SelfHostedTts), ct);

	// Requires HTTPS because neither platform allows cleartext connections.
	private static string? CheckBaseUrl(string text) =>
		Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
			&& uri.Query.Length == 0 && uri.Fragment.Length == 0 ? null : i18n.Translate("Use an https:// address.");

	// Returns the speech stream, or a displayable error if it cannot be opened.
	private async Task<(SpeechReply? Reply, string? Error)> OpenAsync(string text, SpeechSettings settings, CancellationToken ct)
	{
		string baseUrl = settings.Endpoint;
		if (baseUrl.Length == 0) { return (null, i18n.Translate("Set your server's base URL in Settings first.")); }
		string? key = await settings.CredentialAsync(ct);
		return CloudSpeech.Opened(await App.Tts.SelfHostedSpeechAsync(baseUrl, key, settings.Model, settings.Voice, 1, settings.Language, text, ct));
	}
}
