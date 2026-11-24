using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// Fish voice models and synthesis engine IDs are separate; an empty reference ID explicitly uses the default voice.
internal sealed class FishTts : ITtsProvider
{
	private static readonly List<string> Models = ["s2.1-pro", "s2.1-pro-free", "s2-pro", "drama-3", "s1"];

	public string Id => "fish";
	public string Name => "Fish Audio";
	public bool NeedsKey => true;
	public bool CanPause => true;

	private static string Voice => App.Setting("tts.fish.voice", "");
	private static string VoiceName => App.Setting("tts.fish.voice_name", "");
	private static string Model => App.Setting("tts.fish.model", "s2.1-pro-free");

	private static string VoiceText => Voice.Length == 0 ? i18n.Translate("Default voice") : VoiceName.Length > 0 ? VoiceName : Voice;

	public string VoiceSummary() => $"{VoiceText} · {Model}";

	public IReadOnlyList<Pages.InfoRow> Rows() =>
	[
		new("", i18n.Translate("Voice"), VoiceText, false, Chevron: true, Key: "voice"),
		new("", i18n.Translate("Model"), Model, true, Chevron: true, Key: "model"),
	];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		ct.ThrowIfCancellationRequested();
		if (rowKey == "model") { return new(i18n.Translate("Model"), [.. Models.Select(m => new ChoiceOption(m, m, Shutdown: m == "s1" ? "2026-12-31" : null))], Model, null, Note: i18n.Translate("Documented models")); }
		string title = i18n.Translate("Voice");
		int page = 1;
		if (continuation is not null && (!int.TryParse(continuation, out page) || page < 1)) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		string? key = await App.GetApiKeyAsync(Id).WaitAsync(ct);
		if (string.IsNullOrEmpty(key)) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Authentication, true)); }
		var reply = await App.Tts.FishVoicesAsync(key, ct, page, own, search, language);
		if (TtsFailure.ListError(reply.Status, reply.Error) is { } error) { return new(title, [], Voice, error); }
		if (reply.Value is not { Items: { } list } data || list.Any(v => v is null || string.IsNullOrEmpty(v.Id))) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		int offset = checked(page * 50);
		bool more = data.HasMore ?? offset < data.Total;
		bool bounded = data.MaxOffset is { } max && offset > max || data.AccessibleUpperBound is { } upper && offset >= upper;
		List<ChoiceOption> options = continuation is null ? [new("", i18n.Translate("Default voice"))] : [];
		options.AddRange(list.Select(v => new ChoiceOption(v.Id, v.Title ?? v.Id, Language: TtsProviders.ReportedLanguage(v.Language),
			Languages: [.. (v.Languages ?? []).Select(TtsProviders.ReportedLanguage).OfType<string>().Distinct()])));
		if (more && list.Count == 0) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		return new(title, options, Voice, null, Continuation: more && !bounded ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
			Partial: more || data.WindowLimited || bounded, Note: i18n.Translate(own ? "Your voices" : "Public voices"));
	}

	public void Save(string rowKey, string value, string text)
	{
		if (rowKey == "model") { App.SaveSetting("tts.fish.model", value); return; }
		App.SaveSetting("tts.fish.voice", value);
		App.SaveSetting("tts.fish.voice_name", text);
	}

	public Task<string?> ReadAsync(string text, Action playing, CancellationToken ct) => Prepare(text, ct).PlayAsync(playing, ct);

	public SpeechSettings Capture() => new(Id, Model, Voice, VoiceName);

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) => new CloudSpeech(OpenAsync(text, settings, ct), nameof(FishTts), ct);

	// Returns the speech stream, or a displayable error if it cannot be opened.
	private async Task<(SpeechReply? Reply, string? Error)> OpenAsync(string text, SpeechSettings settings, CancellationToken ct)
	{
		string? key = await settings.CredentialAsync(ct);
		if (string.IsNullOrEmpty(key)) { return (null, string.Format(i18n.Translate("Save your {0} API key in Settings first."), Name)); }
		return CloudSpeech.Opened(await App.Tts.FishSpeechAsync(key, settings.Model, settings.Voice.Length > 0 ? settings.Voice : null, text, ct));
	}
}
