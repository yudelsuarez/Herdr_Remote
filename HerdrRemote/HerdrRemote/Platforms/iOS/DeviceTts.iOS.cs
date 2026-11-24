namespace HerdrRemote.Services;

// MAUI's AVSpeechSynthesizer completes each utterance independently.
internal sealed partial class DeviceTts
{
	private static partial Task SpeakAsync(string text, Locale? locale, float rate, Action playing, CancellationToken ct)
	{
		Task speech = TextToSpeech.Default.SpeakAsync(text, new SpeechOptions { Locale = locale, Rate = rate }, ct);
		playing();
		return speech;
	}
}
