using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// OpenAI speech: documented built-in voices; speech-compatible models intersected with the account's model list.
internal sealed class OpenAiTts : ITtsProvider
{
	private static readonly List<string> Voices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "onyx", "nova", "sage", "shimmer", "verse", "marin", "cedar"];
	private static readonly List<string> Models = ["tts-1", "tts-1-hd", "gpt-4o-mini-tts", "gpt-4o-mini-tts-2025-12-15"];

	public string Id => "openai";
	public string Name => "OpenAI";
	public bool NeedsKey => true;
	public bool CanPause => true;

	private static string Voice => App.Setting("tts.openai.voice", "alloy");
	private static string Model => App.Setting("tts.openai.model", "gpt-4o-mini-tts");

	public string VoiceSummary() => $"{Voice} · {Model}";

	public IReadOnlyList<Pages.InfoRow> Rows() =>
	[
		new("", i18n.Translate("Voice"), Voice, false, Chevron: true, Key: "voice"),
		new("", i18n.Translate("Model"), Model, true, Chevron: true, Key: "model"),
	];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		ct.ThrowIfCancellationRequested();
		if (rowKey != "model")
		{
			IEnumerable<string> voices = Model is "tts-1" or "tts-1-hd" ? Voices.Where(v => v is "alloy" or "echo" or "fable" or "onyx" or "nova" or "shimmer") : Voices;
			return new(i18n.Translate("Voice"), [.. voices.Select(v => new ChoiceOption(v, v))], Voice, null, Note: i18n.Translate("Availability not checked"));
		}
		string? key = await App.GetApiKeyAsync(Id).WaitAsync(ct);
		string title = i18n.Translate("Model");
		if (string.IsNullOrEmpty(key)) { return new(title, [], Model, TtsFailure.Message(TtsFailureKind.Authentication, true)); }
		var reply = await App.Tts.OpenAiModelsAsync(key, ct);
		if (TtsFailure.ListError(reply.Status, reply.Error) is { } error) { return new(title, [], Model, error); }
		if (reply.Value?.Data is not { } models || models.Any(m => m is null || string.IsNullOrEmpty(m.Id))) { return new(title, [], Model, TtsFailure.Message(TtsFailureKind.Catalog)); }
		return new(title, [.. models.Where(m => Models.Contains(m.Id!)).Select(m => new ChoiceOption(m.Id!, m.Id!,
			Shutdown: m.ShutdownDate ?? (m.Id is "tts-1" or "tts-1-hd" ? "2027-01-06" : null), SpeechSupported: true))], Model, null);
	}

	// Voice IDs are also their display names, so only the ID is stored.
	public void Save(string rowKey, string value, string text) =>
		App.SaveSetting(rowKey == "model" ? "tts.openai.model" : "tts.openai.voice", value);

	public Task<string?> ReadAsync(string text, Action playing, CancellationToken ct) => Prepare(text, ct).PlayAsync(playing, ct);

	public SpeechSettings Capture() => new(Id, Model, Voice, inputLimit: 4096, limitInBytes: false, verifiedLimit: true);

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) => new CloudSpeech(OpenAsync(text, settings, ct), nameof(OpenAiTts), ct);

	// Returns the speech stream, or a displayable error if it cannot be opened.
	private async Task<(SpeechReply? Reply, string? Error)> OpenAsync(string text, SpeechSettings settings, CancellationToken ct)
	{
		string? key = await settings.CredentialAsync(ct);
		if (string.IsNullOrEmpty(key)) { return (null, string.Format(i18n.Translate("Save your {0} API key in Settings first."), Name)); }
		return CloudSpeech.Opened(await App.Tts.OpenAiSpeechAsync(key, settings.Model, settings.Voice, text, ct));
	}
}
