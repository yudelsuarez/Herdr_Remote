using Languages;

namespace HerdrRemote.Services;

// A device or cloud dictation engine. Shares provider IDs and key storage (App.GetApiKeyAsync) with ITtsProvider.
// CheckAsync returns already translated messages.
internal interface ISpeechToText
{
	string Id { get; }
	string Name { get; }
	bool NeedsKey { get; }

	// Device recognizers stream partial results; cloud engines transcribe after recording stops.
	bool Streams { get; }

	// Returns null when ready, otherwise a translated message about a missing key, model or permission.
	Task<string?> CheckAsync(string language, CancellationToken ct);

	IDictation Start(string language, IReadOnlyList<string> terms);
}

// One dictation run. Events can arrive on any thread; DictationSession dispatches them to the UI thread.
internal interface IDictation
{
	// The complete text of the current live segment.
	event Action<string> Partial;

	// A completed segment ready to append to the draft.
	event Action<string> Committed;

	// Input level from 0 to 1, reported at most 10 times per second.
	event Action<float> Level;

	// Resolves to null on a clean end, including StopAsync or Cancel.
	Task<SttFailureKind?> Ended { get; }

	// Commits the final device segment or starts the cloud upload.
	Task StopAsync();

	// Discards the run and releases the microphone.
	void Cancel();
}

// A cloud dictation run that retains its recording after a failed upload for retry.
internal interface IRetryable : IDictation
{
	bool HasRecording { get; }
	Task<SttFailureKind?> RetryAsync();
	void Discard();
}

internal enum SttFailureKind { Authentication, Credits, RateLimit, Transport, Audio, NothingHeard, Unknown }

internal static class SttFailure
{
	public static string Message(SttFailureKind kind) => i18n.Translate(kind switch
	{
		SttFailureKind.Authentication => "Check this provider's API key.",
		SttFailureKind.Credits => "This provider has no credits available.",
		SttFailureKind.RateLimit => "Too many requests. Try again later.",
		SttFailureKind.Transport => "Could not connect to the provider. Your recording is still here.",
		SttFailureKind.Audio => "The microphone stopped working. Try again.",
		SttFailureKind.NothingHeard => "Nothing was understood. Try again.",
		_ => "The provider could not transcribe the audio. Try again.",
	});

	public static SttFailureKind Classify(int status, string? code) => TtsFailure.Classify(status, code) switch
	{
		TtsFailureKind.Authentication or TtsFailureKind.Permission => SttFailureKind.Authentication,
		TtsFailureKind.Credits => SttFailureKind.Credits,
		TtsFailureKind.RateLimit => SttFailureKind.RateLimit,
		TtsFailureKind.Transport => SttFailureKind.Transport,
		_ => SttFailureKind.Unknown,
	};

	public static string MissingKey(string providerName) => string.Format(i18n.Translate("Save your {0} API key in Settings first."), providerName);
	public static string SpeechDenied() => i18n.Translate("Remote Shell needs speech recognition to dictate with the phone. Allow it in the phone's settings.");
	public static string MicDenied() => i18n.Translate("Remote Shell needs the microphone to dictate. Allow it in the phone's settings.");
	public static string NoOnDevice(string language) => string.Format(i18n.Translate("This phone cannot recognize {0} offline. Change the language or choose a provider in Settings."), LanguageName(language));

	public static string LanguageName(string language) => language.StartsWith("es", StringComparison.OrdinalIgnoreCase)
		? i18n.Translate("Spanish (United States)") : i18n.Translate("English (United States)");
}

internal static class SttProviders
{
	public static readonly ISpeechToText Device = new DeviceStt();
	public static readonly ISpeechToText OpenAi = new CloudStt("openai", "OpenAI");
	public static readonly ISpeechToText ElevenLabs = new CloudStt("elevenlabs", "ElevenLabs");
	public static readonly ISpeechToText Azure = new CloudStt("azure", "Azure");
	public static readonly ISpeechToText Inworld = new CloudStt("inworld", "Inworld");

	public static readonly IReadOnlyList<ISpeechToText> All = [Device, OpenAi, ElevenLabs, Azure, Inworld];

	public static ISpeechToText Current => All.FirstOrDefault(p => p.Id == App.Setting("stt.provider", "device")) ?? Device;

	// "auto" follows the app language; explicit choices are "es-US" and "en-US".
	public static string LanguageSetting => App.Setting("stt.lang", "auto") is "es-US" or "en-US" ? App.Setting("stt.lang", "auto") : "auto";

	public static string Language => LanguageSetting != "auto" ? LanguageSetting
		: i18n.defaultLanguageCode.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es-US" : "en-US";

	public static string Model(string id) => App.Setting($"stt.{id}.model", id == "openai" ? "gpt-4o-transcribe" : "");
}

// Shared developer terms and tool names for recognition bias in Spanish or English, where supported by the engine.
// The same list applies to every agent.
internal static class DictationTerms
{
	public static readonly IReadOnlyList<string> All =
	[
		"space", "workspace", "worktree", "branch", "commit", "path", "pane", "merge", "rebase", "pull request", "push", "stash", "diff",
		"terminal", "prompt", "token", "repo", "build", "deploy", "debug", "refactor", "script", "shell", "sudo", "git", "npm", "docker",
		"Claude Code", "Codex", "pi", "OpenCode", "Herdr",
	];
}
