using Languages;

namespace HerdrRemote.Services;

// Uses only on-device recognition, never the vendor's network recognizer. CheckAsync reports a missing language model.
// iOS uses SFSpeechRecognizer with RequiresOnDeviceRecognition; Android uses the on-device SpeechRecognizer.
// Platform implementations are in Platforms/<os>/DeviceStt.<os>.cs.
internal sealed partial class DeviceStt : ISpeechToText
{
	public string Id => "device";
	public string Name => i18n.Translate("Device");
	public bool NeedsKey => false;
	public bool Streams => true;

	public Task<string?> CheckAsync(string language, CancellationToken ct) => CheckPlatformAsync(language, ct);

	public IDictation Start(string language, IReadOnlyList<string> terms) => StartPlatform(language, terms);

	private partial Task<string?> CheckPlatformAsync(string language, CancellationToken ct);
	private partial IDictation StartPlatform(string language, IReadOnlyList<string> terms);
}
