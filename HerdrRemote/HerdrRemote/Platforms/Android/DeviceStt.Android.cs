using System.Diagnostics;
using Android.Content;
using Android.Media;
using Bundle = Android.OS.Bundle;
using Handler = Android.OS.Handler;
using Looper = Android.OS.Looper;
using ParcelFileDescriptor = Android.OS.ParcelFileDescriptor;
using Android.Speech;

namespace HerdrRemote.Services;

// API 33+ on-device recognition never uses the vendor's network recognizer. Feed 16 kHz mono PCM
// through an EXTRA_AUDIO_SOURCE pipe; each pause yields a segment. Some on-device services ignore
// segmented extras when using their own microphone and transcribe only the first utterance.
// Restart recognizer-ended sessions with a new pipe while the microphone keeps running. The shared
// session enforces the 60 s silence rule and 10 min limit on both platforms.
// Reuse one recognizer for the support check and all StartListening calls: each instance binds the same
// service, so destroying one while another starts can cause ServerDisconnected before listening.
internal sealed partial class DeviceStt
{
	// The recognizer that answered the support check, handed to the run that follows it (main thread only).
	private SpeechRecognizer? _checked;
	private string? _checkedLanguage;

	private static Intent RecognizerIntent(string language, IReadOnlyList<string>? terms = null)
	{
		Intent intent = new(Android.Speech.RecognizerIntent.ActionRecognizeSpeech);
		intent.PutExtra(Android.Speech.RecognizerIntent.ExtraLanguageModel, Android.Speech.RecognizerIntent.LanguageModelFreeForm);
		intent.PutExtra(Android.Speech.RecognizerIntent.ExtraLanguage, language);
		intent.PutExtra(Android.Speech.RecognizerIntent.ExtraPartialResults, true);
		intent.PutExtra(Android.Speech.RecognizerIntent.ExtraPreferOffline, true);
		if (terms is { Count: > 0 }) { intent.PutStringArrayListExtra(Android.Speech.RecognizerIntent.ExtraBiasingStrings, [.. terms]); }
		return intent;
	}

	private partial async Task<string?> CheckPlatformAsync(string language, CancellationToken ct)
	{
		Context context = Platform.AppContext;
		if (!SpeechRecognizer.IsOnDeviceRecognitionAvailable(context)) { return SttFailure.NoOnDevice(language); }
		TaskCompletionSource<bool> answer = new();
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			_checked ??= SpeechRecognizer.CreateOnDeviceSpeechRecognizer(context);
			_checkedLanguage = null;
			_checked.CheckRecognitionSupport(RecognizerIntent(language), context.MainExecutor!, new SupportCallback(language, answer));
		});
		bool installed = await answer.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			if (installed) { _checkedLanguage = language; }
			else { _checked?.Destroy(); _checked = null; }
		});
		return installed ? null : SttFailure.NoOnDevice(language);
	}

	private sealed class SupportCallback(string language, TaskCompletionSource<bool> answer) : Java.Lang.Object, IRecognitionSupportCallback
	{
		public void OnSupportResult(RecognitionSupport recognitionSupport) =>
			answer.TrySetResult(recognitionSupport.InstalledOnDeviceLanguages.Any(tag => string.Equals(tag, language, StringComparison.OrdinalIgnoreCase)));

		public void OnError(int error)
		{
			Debug.WriteLine($"[Dictation] recognition support check failed: {error}");
			answer.TrySetResult(false);
		}
	}

	// The checked recognizer goes to the run (it owns and destroys it); without one for this language the run makes its own.
	private partial IDictation StartPlatform(string language, IReadOnlyList<string> terms)
	{
		SpeechRecognizer? recognizer = _checkedLanguage == language ? _checked : null;
		if (recognizer is null) { _checked?.Destroy(); }
		_checked = null;
		_checkedLanguage = null;
		return new Run(language, terms, recognizer);
	}

	private sealed class Run : IDictation
	{
		private readonly string _language;
		private readonly IReadOnlyList<string> _terms;
		private readonly TaskCompletionSource<SttFailureKind?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private SpeechRecognizer? _recognizer;
		private TaskCompletionSource? _sessionEnded;
		private string _live = "";
		private string _lastCommitted = "";
		private bool _stopping, _done, _listening;
		private long _lastStartAt, _levelAt;
		private int _quickFailures;

		public Run(string language, IReadOnlyList<string> terms, SpeechRecognizer? recognizer)
		{
			_language = language;
			_terms = terms;
			_recognizer = recognizer;
			MainThread.BeginInvokeOnMainThread(Begin);
		}

		public event Action<string>? Partial;
		public event Action<string>? Committed;
		public event Action<float>? Level;

		public Task<SttFailureKind?> Ended => _ended.Task;

		private void Begin()
		{
			if (_done || _stopping) { return; }
			try
			{
				if (_recognizer is null || !_listening)
				{
					_recognizer ??= SpeechRecognizer.CreateOnDeviceSpeechRecognizer(Platform.AppContext);
					_recognizer.SetRecognitionListener(new Listener(this));
					_listening = true;
				}
				if (_mic is null && !StartMic()) { Finish(SttFailureKind.Audio); return; }
				SpeechRecognizer recognizer = _recognizer;
				Intent intent = RecognizerIntent(_language, _terms);
				intent.PutExtra(Android.Speech.RecognizerIntent.ExtraAudioSource, OpenPipe());
				intent.PutExtra(Android.Speech.RecognizerIntent.ExtraAudioSourceEncoding, (int)Encoding.Pcm16bit);
				intent.PutExtra(Android.Speech.RecognizerIntent.ExtraAudioSourceChannelCount, 1);
				intent.PutExtra(Android.Speech.RecognizerIntent.ExtraAudioSourceSamplingRate, Rate);
				intent.PutExtra(Android.Speech.RecognizerIntent.ExtraSegmentedSession, Android.Speech.RecognizerIntent.ExtraAudioSource);
				_sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
				_lastStartAt = Environment.TickCount64;
				recognizer.StartListening(intent);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Dictation] recognizer start failed: {ex.GetType().Name}");
				Finish(SttFailureKind.Audio);
			}
		}

		private sealed class Listener(Run run) : Java.Lang.Object, IRecognitionListener
		{
			private bool Stale => run._done;
			public void OnReadyForSpeech(Bundle? @params) { }
			public void OnBeginningOfSpeech() { }
			public void OnBufferReceived(byte[]? buffer) { }
			public void OnEvent(int eventType, Bundle? @params) { }
			public void OnEndOfSpeech() { }

			public void OnRmsChanged(float rmsdB)
			{
				long now = Environment.TickCount64;
				if (Stale || now - run._levelAt < 100) { return; }
				run._levelAt = now;
				run.Level?.Invoke(Math.Clamp((rmsdB + 2) / 12f, 0, 1));
			}

			public void OnPartialResults(Bundle? partialResults)
			{
				if (!Stale && Best(partialResults) is { Length: > 0 } text && text != run._live) { run._live = text; run.Partial?.Invoke(text); }
			}

			public void OnSegmentResults(Bundle segmentResults)
			{
				if (!Stale) { run.Commit(Best(segmentResults)); }
			}

			public void OnEndOfSegmentedSession()
			{
				if (!Stale) { run.SessionEnded(); }
			}

			public void OnResults(Bundle? results)
			{
				if (Stale) { return; }
				string? text = Best(results);
				// A segmented session already delivered this text through OnSegmentResults.
				if (text is { Length: > 0 } && text != run._lastCommitted) { run.Commit(text); }
				else { run.Commit(null); }
				run.SessionEnded();
			}

			public void OnError(SpeechRecognizerError error)
			{
				if (Stale) { return; }
				run.Commit(null);
				switch (error)
				{
					case SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout or SpeechRecognizerError.RecognizerBusy or SpeechRecognizerError.Client:
						run.SessionEnded(error != SpeechRecognizerError.NoMatch && error != SpeechRecognizerError.SpeechTimeout);
						break;
					case SpeechRecognizerError.Audio or SpeechRecognizerError.InsufficientPermissions:
						run.Finish(SttFailureKind.Audio);
						break;
					default:
						Debug.WriteLine($"[Dictation] recognizer error: {error}");
						run.Finish(SttFailureKind.Unknown);
						break;
				}
			}

			private static string? Best(Bundle? bundle) => bundle?.GetStringArrayList(SpeechRecognizer.ResultsRecognition)?.FirstOrDefault();
		}

		// A finished segment (or the live text left when a session ends).
		private void Commit(string? text)
		{
			string segment = string.IsNullOrWhiteSpace(text) ? _live : text;
			_live = "";
			if (string.IsNullOrWhiteSpace(segment)) { return; }
			_lastCommitted = segment;
			Committed?.Invoke(segment);
		}

		// The recognizer's session is over: stopped by the session, or ended by the recognizer itself and started again.
		private void SessionEnded(bool backoff = false)
		{
			_sessionEnded?.TrySetResult();
			if (_stopping || _done) { return; }
			if (Environment.TickCount64 - _lastStartAt < 1000) { _quickFailures++; } else { _quickFailures = 0; }
			if (_quickFailures >= 5) { Finish(SttFailureKind.Unknown); return; }
			if (backoff || _quickFailures > 0) { new Handler(Looper.MainLooper!).PostDelayed(Begin, 300); }
			else { Begin(); }
		}

		public async Task StopAsync()
		{
			if (_done) { return; }
			_stopping = true;
			TaskCompletionSource? ended = _sessionEnded;
			await MainThread.InvokeOnMainThreadAsync(() => { CloseSink(); try { _recognizer?.StopListening(); } catch (Exception ex) { Debug.WriteLine($"[Dictation] stop: {ex.GetType().Name}"); } });
			if (ended is not null) { await Task.WhenAny(ended.Task, Task.Delay(3000)); }
			await MainThread.InvokeOnMainThreadAsync(() => { Commit(null); Finish(null); });
		}

		public void Cancel()
		{
			_stopping = true;
			_live = "";
			MainThread.BeginInvokeOnMainThread(() => Finish(null));
		}

		private const int Rate = 16000;
		private readonly object _sinkGate = new();
		private AudioRecord? _mic;
		private Java.IO.OutputStream? _sink;

		// The run's microphone: one reader thread copies PCM into the current session's pipe. A read that fails while the run is
		// listening means the system took the microphone (a call): what was heard is kept and the run ends as stopped by the system.
		private bool StartMic()
		{
			int minimum = AudioRecord.GetMinBufferSize(Rate, ChannelIn.Mono, Encoding.Pcm16bit);
			if (minimum <= 0) { return false; }
			AudioRecord mic = new(AudioSource.VoiceRecognition, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minimum, Rate));
			if (mic.State != State.Initialized) { mic.Release(); return false; }
			try { mic.StartRecording(); }
			catch (Exception ex)
			{
				Debug.WriteLine($"[Dictation] AudioRecord start failed: {ex.GetType().Name}");
				mic.Release();
				return false;
			}
			_mic = mic;
			new Thread(() =>
			{
				byte[] buffer = new byte[Rate / 5];
				while (!_done)
				{
					int read = mic.Read(buffer, 0, buffer.Length);
					if (read <= 0)
					{
						if (!_done && !_stopping) { MainThread.BeginInvokeOnMainThread(() => { Commit(null); Finish(SttFailureKind.Audio); }); }
						return;
					}
					lock (_sinkGate)
					{
						// The recognizer closed its end (its session ended): the bytes until the next pipe are dropped.
						try { _sink?.Write(buffer, 0, read); } catch (Java.IO.IOException) { _sink = null; }
					}
				}
			}) { IsBackground = true, Name = "dictation-mic" }.Start();
			return true;
		}

		// A new pipe for a session: the recognizer reads its end, the microphone thread writes the other.
		private ParcelFileDescriptor OpenPipe()
		{
			ParcelFileDescriptor[] pipe = ParcelFileDescriptor.CreatePipe()!;
			lock (_sinkGate)
			{
				CloseSinkLocked();
				_sink = new ParcelFileDescriptor.AutoCloseOutputStream(pipe[1]);
			}
			return pipe[0];
		}

		private void CloseSink() { lock (_sinkGate) { CloseSinkLocked(); } }

		private void CloseSinkLocked()
		{
			try { _sink?.Close(); } catch (Java.IO.IOException) { }
			_sink = null;
		}

		private void Finish(SttFailureKind? failure)
		{
			if (_done) { return; }
			_done = true;
			CloseSink();
			if (_mic is { } mic)
			{
				_mic = null;
				try { mic.Stop(); } catch (Exception ex) { Debug.WriteLine($"[Dictation] AudioRecord stop failed: {ex.GetType().Name}"); }
				mic.Release();
			}
			try { _recognizer?.Destroy(); } catch (Exception ex) { Debug.WriteLine($"[Dictation] destroy: {ex.GetType().Name}"); }
			_recognizer = null;
			_ended.TrySetResult(failure);
		}
	}
}
