using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// Loads ElevenLabs voices and speech models through the account's API.
internal sealed class ElevenLabsTts : ITtsProvider
{
	public string Id => "elevenlabs";
	public string Name => "ElevenLabs";
	public bool NeedsKey => true;
	public bool CanPause => true;

	private static string Voice => App.Setting("tts.elevenlabs.voice", "");
	private static string VoiceName => App.Setting("tts.elevenlabs.voice_name", "");
	private static string Model => App.Setting("tts.elevenlabs.model", "eleven_multilingual_v2");

	private static string VoiceText => VoiceName.Length > 0 ? VoiceName : i18n.Translate("No voice chosen");

	public string VoiceSummary() => $"{VoiceText} · {Model}";

	public IReadOnlyList<Pages.InfoRow> Rows() =>
	[
		new("", i18n.Translate("Voice"), VoiceText, false, Chevron: true, Key: "voice"),
		new("", i18n.Translate("Model"), Model, true, Chevron: true, Key: "model"),
	];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		string title = rowKey == "model" ? i18n.Translate("Model") : i18n.Translate("Voice");
		string? key = await App.GetApiKeyAsync(Id).WaitAsync(ct);
		if (string.IsNullOrEmpty(key)) { return new(title, [], null, TtsFailure.Message(TtsFailureKind.Authentication, true)); }
		if (rowKey == "model")
		{
			var models = await App.Tts.ElevenLabsModelsAsync(key, ct);
			if (TtsFailure.ListError(models.Status, models.Error) is { } modelsError) { return new(title, [], Model, modelsError); }
			if (models.Value is not { } rows || rows.Any(m => m is null || string.IsNullOrEmpty(m.ModelId))) { return new(title, [], Model, TtsFailure.Message(TtsFailureKind.Catalog)); }
			return new(title, [.. rows.Where(m => m.CanDoTextToSpeech).Select(m => new ChoiceOption(m.ModelId, m.Name ?? m.ModelId, m.Description,
				MaximumTextLength: m.MaximumTextLengthPerRequest is > 0 ? m.MaximumTextLengthPerRequest : null, SpeechSupported: true,
				Languages: [.. (m.Languages ?? []).Where(l => l is not null).Select(l => TtsProviders.ReportedLanguage(l.Id)).OfType<string>().Distinct()]))], Model, null);
		}
		var voices = await App.Tts.ElevenLabsVoicesAsync(key, ct, continuation, search, language);
		if (TtsFailure.ListError(voices.Status, voices.Error) is { } voicesError) { return new(title, [], Voice, voicesError); }
		if (voices.Value?.Voices is not { } list || list.Any(v => v is null || string.IsNullOrEmpty(v.VoiceId))
			|| voices.Value.HasMore && string.IsNullOrEmpty(voices.Value.NextPageToken)) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		return new(title, [.. list.Select(v => new ChoiceOption(v.VoiceId, v.Name ?? v.VoiceId, v.Category,
			TtsProviders.ReportedLanguage(v.Language) ?? TtsProviders.ReportedLanguage(v.Labels?.GetValueOrDefault("language")),
			Languages: [.. (v.VerifiedLanguages ?? []).Where(l => l is not null).Select(l => TtsProviders.ReportedLanguage(l.Locale) ?? TtsProviders.ReportedLanguage(l.Language)).OfType<string>().Distinct()]))],
			Voice, null, Continuation: voices.Value.HasMore ? voices.Value.NextPageToken : null, Partial: voices.Value.HasMore);
	}

	public void Save(string rowKey, string value, string text)
	{
		if (rowKey == "model") { App.SaveSetting("tts.elevenlabs.model", value); return; }
		App.SaveSetting("tts.elevenlabs.voice", value);
		App.SaveSetting("tts.elevenlabs.voice_name", text);
	}

	public Task<string?> ReadAsync(string text, Action playing, CancellationToken ct) => Prepare(text, ct).PlayAsync(playing, ct);

	public SpeechSettings Capture()
	{
		string model = Model;
		int? limit = TtsProviders.ModelInputLimit(Id, model);
		return new(Id, model, Voice, VoiceName, inputLimit: limit ?? 1024, limitInBytes: limit is null, verifiedLimit: limit is not null);
	}

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) => new CloudSpeech(OpenAsync(text, settings, ct), nameof(ElevenLabsTts), ct);

	// Returns the speech stream, or a displayable error if it cannot be opened.
	private async Task<(SpeechReply? Reply, string? Error)> OpenAsync(string text, SpeechSettings settings, CancellationToken ct)
	{
		string? key = await settings.CredentialAsync(ct);
		if (string.IsNullOrEmpty(key)) { return (null, string.Format(i18n.Translate("Save your {0} API key in Settings first."), Name)); }
		if (settings.Voice.Length == 0) { return (null, string.Format(i18n.Translate("Choose a {0} voice in Settings first."), Name)); }
		return CloudSpeech.Opened(await App.Tts.ElevenLabsSpeechAsync(key, settings.Voice, settings.Model, text, ct));
	}
}
