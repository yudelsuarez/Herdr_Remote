using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// Lists device voices through MAUI TextToSpeech and uses platform speech engines. Pause cancels; resume repeats the sentence.
internal sealed partial class DeviceTts : ITtsProvider
{
	public string Id => "device";
	public string Name => i18n.Translate("Device voice");
	public bool NeedsKey => false;
	public bool CanPause => true;

	// voice is the Locale Id, or its Name if the Id is empty; an empty voice selects the system default.
	private static string Voice => App.Setting("tts.device.voice", "");
	private static string VoiceName => App.Setting("tts.device.voice_name", "");

	private static string VoiceText => Voice.Length == 0 ? i18n.Translate("System default") : VoiceName;

	public string VoiceSummary() => VoiceText;

	public IReadOnlyList<Pages.InfoRow> Rows() => [new("", i18n.Translate("Voice"), VoiceText, false, Chevron: true, Key: "voice")];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		List<ChoiceOption> options = [new("", i18n.Translate("System default"))];
		foreach (Locale l in (await TextToSpeech.Default.GetLocalesAsync().WaitAsync(ct)).OrderBy(l => l.Language).ThenBy(l => l.Name))
		{
			options.Add(new(l.Id.Length > 0 ? l.Id : l.Name, l.Id.Length > 0 ? $"{l.Name} · {l.Id}" : $"{l.Name} ({l.Language}-{l.Country})",
				Language: TtsProviders.ReportedLanguage(l.Language)));
		}
		return new ChoiceList(i18n.Translate("Voice"), [.. options.DistinctBy(o => o.Id)], Voice, null);
	}
	public void RefreshCatalog() { _localeRevision++; _resolved = null; }

	public void Save(string rowKey, string value, string text)
	{
		App.SaveSetting("tts.device.voice", value);
		App.SaveSetting("tts.device.voice_name", text);
	}

	// Falls back to the system default if the saved voice is no longer installed.
	// Device speech is not prefetched; synthesis starts when the prepared speech plays.
	public SpeechSettings Capture() => new(Id, "", Voice, VoiceName, deviceLocale: LocaleAsync(Voice));

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) =>
		new DeviceSpeech((playing, token, rate) => ReadAsync(text, settings, rate, playing, token));

	public async Task<string?> ReadAsync(string text, Action playing, CancellationToken ct)
		=> await ReadAsync(text, Capture(), 1, playing, ct);

	private static async Task<string?> ReadAsync(string text, SpeechSettings settings, float rate, Action playing, CancellationToken ct)
	{
		try
		{
			Locale? locale = await settings.DeviceLocale.WaitAsync(ct);
			ct.ThrowIfCancellationRequested();
			if (settings.Voice.Length > 0 && locale is null) { return TtsFailure.Message(TtsFailureKind.Unavailable); }
			AudioPlayer.PrepareSession();
			await SpeakAsync(text, locale, rate, playing, ct);
			return null;
		}
		catch (SpeechStreamException ex) { return TtsFailure.Message(ex.Kind); }
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Debug.WriteLine($"[DeviceTts] read failed: {ex.GetType().Name}");
			return i18n.Translate("The audio could not be played.");
		}
	}

	// Caches each saved voice's locale for this app run rather than listing voices for every sentence.
	// An empty voice uses the system default without a lookup; a missing saved voice resolves to null.
	private static (string Voice, Locale? Locale)? _resolved;
	private static int _localeRevision;

	private static async Task<Locale?> LocaleAsync(string saved)
	{
		if (saved.Length == 0) { return null; }
		if (_resolved is { } resolved && resolved.Voice == saved) { return resolved.Locale; }
		int revision = _localeRevision;
		Locale? locale = (await TextToSpeech.Default.GetLocalesAsync()).FirstOrDefault(l => l.Id == saved || (l.Id.Length == 0 && l.Name == saved));
		if (revision == _localeRevision) { _resolved = (saved, locale); }
		return locale;
	}

	// Completes when platform speech ends, or immediately after calling the engine's stop method on cancellation.
	private static partial Task SpeakAsync(string text, Locale? locale, float rate, Action playing, CancellationToken ct);
}
