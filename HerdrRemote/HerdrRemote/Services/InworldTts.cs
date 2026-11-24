using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// Loads Inworld voices through the Voice API and offers two speech models. The key is Inworld's displayed Basic credential.
internal sealed class InworldTts : ITtsProvider
{
	private static readonly List<string> Models = ["inworld-tts-2", "inworld-tts-2-flash"];

	public string Id => "inworld";
	public string Name => "Inworld";
	public bool NeedsKey => true;
	public bool CanPause => true;

	private static string Voice => App.Setting("tts.inworld.voice", "Ashley");
	private static string VoiceName => App.Setting("tts.inworld.voice_name", "Ashley");
	private static string Model => App.Setting("tts.inworld.model", "inworld-tts-2");

	public string VoiceSummary() => $"{VoiceName} · {Model}";

	public IReadOnlyList<Pages.InfoRow> Rows() =>
	[
		new("", i18n.Translate("Voice"), VoiceName, false, Chevron: true, Key: "voice"),
		new("", i18n.Translate("Model"), Model, true, Chevron: true, Key: "model"),
	];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		ct.ThrowIfCancellationRequested();
		if (rowKey == "model") { return new(i18n.Translate("Model"), [.. Models.Select(m => new ChoiceOption(m, m))], Model, null, Note: i18n.Translate("Documented models")); }
		string title = i18n.Translate("Voice");
		string? key = await App.GetApiKeyAsync(Id).WaitAsync(ct);
		if (string.IsNullOrEmpty(key)) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Authentication, true)); }
		var reply = await App.Tts.InworldVoicesAsync(key, ct, continuation);
		if (TtsFailure.ListError(reply.Status, reply.Error) is { } error) { return new(title, [], Voice, error); }
		if (reply.Value?.Voices is not { } list || list.Any(v => v is null || string.IsNullOrEmpty(v.VoiceId))) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		return new(title, [.. list.Select(v => new ChoiceOption(v.VoiceId, v.DisplayName ?? v.VoiceId, v.VoiceLanguage, v.VoiceLanguage, v.PromptLanguages))], Voice, null,
			Continuation: string.IsNullOrEmpty(reply.Value.NextPageToken) ? null : reply.Value.NextPageToken, Partial: !string.IsNullOrEmpty(reply.Value.NextPageToken));
	}

	public void Save(string rowKey, string value, string text)
	{
		if (rowKey == "model") { App.SaveSetting("tts.inworld.model", value); return; }
		App.SaveSetting("tts.inworld.voice", value);
		App.SaveSetting("tts.inworld.voice_name", text);
	}

	public Task<string?> ReadAsync(string text, Action playing, CancellationToken ct) => Prepare(text, ct).PlayAsync(playing, ct);

	public SpeechSettings Capture() => new(Id, Model, Voice, VoiceName);

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) => new CloudSpeech(OpenAsync(text, settings, ct), nameof(InworldTts), ct);

	// Returns the speech stream, or a displayable error if it cannot be opened.
	private async Task<(SpeechReply? Reply, string? Error)> OpenAsync(string text, SpeechSettings settings, CancellationToken ct)
	{
		string? key = await settings.CredentialAsync(ct);
		if (string.IsNullOrEmpty(key)) { return (null, string.Format(i18n.Translate("Save your {0} API key in Settings first."), Name)); }
		return CloudSpeech.Opened(await App.Tts.InworldSpeechAsync(key, settings.Voice, settings.Model, text, ct));
	}
}
