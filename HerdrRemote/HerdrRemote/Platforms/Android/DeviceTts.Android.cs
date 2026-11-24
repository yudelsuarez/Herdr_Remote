using Android.Speech.Tts;
using AndroidTts = Android.Speech.Tts.TextToSpeech;

namespace HerdrRemote.Services;

// Track completions per utterance: MAUI's global completion count lets a stopped sentence's late callback end the next one.
// Gate protects only managed state; copy references under it, then release it before Java calls or task completion.
// This prevents nesting Gate with Android's mStartLock.
internal sealed partial class DeviceTts
{
	private static readonly Lock Gate = new();
	private static Engine? _engine;
	private static readonly Dictionary<string, Speech> Pending = [];
	private static Speech? _current;

	// Complete on the last chunk's onDone, any chunk's onStop/onError, or immediately after cancellation's stop() returns.
	private static async partial Task SpeakAsync(string text, Locale? locale, float rate, Action playing, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		Engine engine = await ReadyAsync(ct);
		ct.ThrowIfCancellationRequested();
		ApplyVoice(engine.Tts, locale, rate);
		List<string> chunks = Chunks(text);
		if (chunks.Count == 0) { return; }
		Speech speech = new(Guid.NewGuid().ToString("N"), chunks.Count - 1);
		try
		{
			lock (Gate)
			{
				Pending[speech.Id] = speech;
				_current = speech;
			}
			for (int i = 0; i < chunks.Count; i++)
			{
				OperationResult result = engine.Tts.Speak(chunks[i], i == 0 ? QueueMode.Flush : QueueMode.Add, null, $"{speech.Id}.{i}");
				if (result != OperationResult.Success)
				{
					InvalidOperationException error = new($"TextToSpeech speak {result}");
					Drop(error);
					throw error;
				}
				if (i == 0) { playing(); }
			}
			using (ct.Register(() => Cancel(speech, engine)))
			{
				await speech.Done.Task;
			}
		}
		finally
		{
			lock (Gate)
			{
				Pending.Remove(speech.Id);
				if (ReferenceEquals(_current, speech)) { _current = null; }
			}
		}
	}

	// Reuse the initialized engine; discard and recreate it if initialization failed.
	private static async Task<Engine> ReadyAsync(CancellationToken ct)
	{
		Engine? engine;
		bool faulted;
		lock (Gate)
		{
			engine = _engine;
			faulted = engine?.Ready.Task.IsFaulted == true;
		}
		if (engine is null || faulted)
		{
			if (faulted) { Drop(null); }
			Engine created = new();
			Engine? existing;
			lock (Gate)
			{
				existing = _engine;
				_engine ??= created;
			}
			if (existing is not null) { created.Tts.Shutdown(); }
			engine = existing ?? created;
		}
		await engine.Ready.Task.WaitAsync(ct);
		return engine;
	}

	// Stop only the current speech on the current engine, then complete without waiting for a callback.
	// stop() returns after the service flushes this app's queue and stops its audio; discard the engine if it fails.
	private static void Cancel(Speech speech, Engine engine)
	{
		bool stop;
		lock (Gate)
		{
			stop = !speech.Done.Task.IsCompleted && ReferenceEquals(_current, speech) && ReferenceEquals(_engine, engine);
		}
		OperationResult result = stop ? engine.Tts.Stop() : OperationResult.Success;
		speech.Done.TrySetResult();
		if (result != OperationResult.Success) { Drop(null); }
	}

	// Complete all pending speech with the error, if any, and shut down the engine; the next read creates a new one.
	private static void Drop(Exception? error)
	{
		Engine? engine;
		List<Speech> speeches;
		lock (Gate)
		{
			engine = _engine;
			speeches = [.. Pending.Values];
			_engine = null;
			_current = null;
			Pending.Clear();
		}
		foreach (Speech speech in speeches)
		{
			if (error is null) { speech.Done.TrySetResult(); } else { speech.Done.TrySetException(error); }
		}
		engine?.Tts.Shutdown();
	}

	// Follow MAUI's voice selection, using the default voice's locale instead of obsolete language getters
	// and Locale.of instead of Locale constructors on Android 36+.
	private static void ApplyVoice(AndroidTts tts, Locale? locale, float rate)
	{
		using Android.Media.AudioAttributes attributes = new Android.Media.AudioAttributes.Builder()
			.SetUsage(Android.Media.AudioUsageKind.Media)?.SetContentType(Android.Media.AudioContentType.Speech)?.Build()
			?? throw new SpeechStreamException(TtsFailureKind.Audio);
		if (tts.SetAudioAttributes(attributes) != OperationResult.Success) { throw new SpeechStreamException(TtsFailureKind.Audio); }
		tts.SetPitch(1f);
		if (tts.SetSpeechRate(rate) != OperationResult.Success) { throw new SpeechStreamException(TtsFailureKind.PlaybackSpeed); }
		if (locale?.Language is { } language)
		{
			bool country = !string.IsNullOrWhiteSpace(locale.Country);
			tts.SetLanguage(OperatingSystem.IsAndroidVersionAtLeast(36)
				? (country ? Java.Util.Locale.Of(language, locale.Country) : Java.Util.Locale.Of(language))
				: (country ? new Java.Util.Locale(language, locale.Country) : new Java.Util.Locale(language)));
		}
		else if (tts.DefaultVoice?.Locale is { } fallback)
		{
			tts.SetLanguage(fallback);
		}
		if (!string.IsNullOrEmpty(locale?.Id) && tts.Voices?.FirstOrDefault(v => v.Name == locale.Id) is { } voice)
		{
			tts.SetVoice(voice);
		}
	}

	// Split within the engine's limit, preferring the last whitespace or punctuation; otherwise avoid splitting a surrogate pair.
	// Preserve every character when splitting, then omit blank chunks.
	private static List<string> Chunks(string text)
	{
		int max = AndroidTts.MaxSpeechInputLength;
		List<string> chunks = [];
		int start = 0;
		while (text.Length - start > max)
		{
			int cut = -1;
			for (int i = start + max - 1; i >= start && cut < 0; i--)
			{
				if (char.IsWhiteSpace(text[i]) || char.IsPunctuation(text[i])) { cut = i + 1; }
			}
			if (cut < 0) { cut = char.IsHighSurrogate(text[start + max - 1]) ? start + max - 1 : start + max; }
			chunks.Add(text[start..cut]);
			start = cut;
		}
		chunks.Add(text[start..]);
		return [.. chunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk))];
	}

	// Resolve "{guid}.{chunk}" to pending speech and its current status; ignore late callbacks for completed speech.
	private static (Speech Speech, int Chunk, bool Current)? Find(string? utteranceId)
	{
		int dot = utteranceId?.LastIndexOf('.') ?? -1;
		if (utteranceId is null || dot < 0 || !int.TryParse(utteranceId.AsSpan(dot + 1), out int chunk)) { return null; }
		lock (Gate)
		{
			return Pending.TryGetValue(utteranceId[..dot], out Speech? speech) ? (speech, chunk, ReferenceEquals(_current, speech)) : null;
		}
	}

	private sealed class Speech(string id, int last)
	{
		public string Id { get; } = id;
		public int Last { get; } = last;
		public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	// One Java peer handles initialization and utterance callbacks, which run on binder threads.
	private sealed class Engine : UtteranceProgressListener, AndroidTts.IOnInitListener
	{
		public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public AndroidTts Tts { get; }

		public Engine()
		{
			Tts = new AndroidTts(Android.App.Application.Context, this);
			Tts.SetOnUtteranceProgressListener(this);
		}

		// Called once per engine inside Android's mStartLock: complete only Ready; do not acquire locks or call Java.
		public void OnInit(OperationResult status)
		{
			if (status == OperationResult.Success) { Ready.TrySetResult(); }
			else { Ready.TrySetException(new InvalidOperationException($"TextToSpeech init {status}")); }
		}

		public override void OnStart(string? utteranceId) { }

		public override void OnDone(string? utteranceId)
		{
			if (Find(utteranceId) is { } found && found.Chunk == found.Speech.Last) { found.Speech.Done.TrySetResult(); }
		}

		// A stop the app did not cancel itself: a flush by a newer speech, or the engine's own stop.
		public override void OnStop(string? utteranceId, bool interrupted)
		{
			if (Find(utteranceId) is { } found) { found.Speech.Done.TrySetResult(); }
		}

		public override void OnError(string? utteranceId, TextToSpeechError errorCode) => Failed(utteranceId, errorCode);

		[Obsolete("Android calls OnError(string, TextToSpeechError); this abstract member only has to exist.")]
		public override void OnError(string? utteranceId) => Failed(utteranceId, TextToSpeechError.Synthesis);

		// The speech's other chunks are stopped when it is the newest one sent, then it ends with the error.
		private void Failed(string? utteranceId, TextToSpeechError code)
		{
			if (Find(utteranceId) is not { } found) { return; }
			if (found.Current) { Tts.Stop(); }
			found.Speech.Done.TrySetException(new InvalidOperationException($"TextToSpeech error {code}"));
		}
	}
}
