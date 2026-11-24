using System.Diagnostics;
using AVFoundation;
using Foundation;
using Speech;

namespace HerdrRemote.Services;

// RequiresOnDeviceRecognition keeps audio on the device. Restart recognizer-ended tasks on the same
// audio tap until the shared session stops dictation; it enforces the 60 s silence rule and 10 min limit
// on both platforms.
internal sealed partial class DeviceStt
{
	private static NSLocale Locale(string language) => new(language.Replace('-', '_'));

	private partial async Task<string?> CheckPlatformAsync(string language, CancellationToken ct)
	{
		SFSpeechRecognizerAuthorizationStatus status = SFSpeechRecognizer.AuthorizationStatus;
		if (status == SFSpeechRecognizerAuthorizationStatus.NotDetermined)
		{
			TaskCompletionSource<SFSpeechRecognizerAuthorizationStatus> asked = new();
			SFSpeechRecognizer.RequestAuthorization(s => asked.TrySetResult(s));
			status = await asked.Task.WaitAsync(ct);
		}
		if (status != SFSpeechRecognizerAuthorizationStatus.Authorized) { return SttFailure.SpeechDenied(); }
		bool supported = false;
		foreach (NSLocale locale in SFSpeechRecognizer.SupportedLocales) { supported |= string.Equals(locale.Identifier?.Replace('_', '-'), language, StringComparison.OrdinalIgnoreCase); }
		SFSpeechRecognizer? recognizer = supported ? new SFSpeechRecognizer(Locale(language)) : null;
		Debug.WriteLine($"[Dictation] {language}: supported locale={supported} onDevice={recognizer?.SupportsOnDeviceRecognition}");
		return recognizer is { SupportsOnDeviceRecognition: true } ? null : SttFailure.NoOnDevice(language);
	}

	private partial IDictation StartPlatform(string language, IReadOnlyList<string> terms) => new Run(language, terms);

	private sealed class Run : IDictation
	{
		private readonly object _gate = new();
		private readonly SFSpeechRecognizer _recognizer;
		private readonly string[] _terms;
		private readonly MicEngine _mic = new();
		private readonly TaskCompletionSource<SttFailureKind?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private SFSpeechAudioBufferRecognitionRequest? _request;
		private SFSpeechRecognitionTask? _task;
		private string _live = "";
		private double _keptStart = -1;
		private bool _stopping, _done;
		private long _lastStartAt;
		private int _quickFailures;
		private TaskCompletionSource? _taskEnded;

		public Run(string language, IReadOnlyList<string> terms)
		{
			_recognizer = new SFSpeechRecognizer(Locale(language));
			_terms = [.. terms];
			_mic.Interrupted += Interrupted;
			if (!_mic.Start((pcm, level) =>
			{
				_request?.Append(pcm);
				if (level > 0) { Level?.Invoke(level); }
			}))
			{
				_done = true;
				_ended.TrySetResult(SttFailureKind.Audio);
				return;
			}
			try { BeginTask(); }
			catch
			{
				_mic.Stop();
				throw;
			}
		}

		public event Action<string>? Partial;
		public event Action<string>? Committed;
		public event Action<float>? Level;

		public Task<SttFailureKind?> Ended => _ended.Task;

		private void BeginTask()
		{
			lock (_gate)
			{
				if (_done || _stopping) { return; }
				_lastStartAt = Environment.TickCount64;
				SFSpeechAudioBufferRecognitionRequest request = new()
				{
					ShouldReportPartialResults = true, RequiresOnDeviceRecognition = true, AddsPunctuation = true, ContextualStrings = _terms,
				};
				_request = request;
				_keptStart = -1;
				_taskEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
				TaskCompletionSource ended = _taskEnded;
				_task = _recognizer.GetRecognitionTask(request, (result, error) => Result(request, ended, result, error));
			}
		}

		private void Result(SFSpeechAudioBufferRecognitionRequest request, TaskCompletionSource ended, SFSpeechRecognitionResult? result, NSError? error)
		{
			bool restart = false;
			string? commit = null;
			string? partial = null;
			SttFailureKind? failure = null;
			bool finished = false;
			lock (_gate)
			{
				if (!ReferenceEquals(request, _request)) { return; }
				if (result is not null)
				{
					string text = result.BestTranscription.FormattedString;
					// The end of one utterance inside a running task: the recognizer marks it with metadata (not Final) and starts the next
					// utterance's transcription from empty, so what it heard is kept now or the next words replace it. The task's Final result
					// repeats the last utterance; the same utterance (same audio start) is not kept twice.
					SFTranscriptionSegment[] segments = result.BestTranscription.Segments;
					double start = segments.Length > 0 ? segments[0].Timestamp : -1;
					bool kept = start >= 0 && start == _keptStart;
					if (result.Final) { commit = kept ? null : text; _live = ""; finished = true; }
					else if (result.SpeechRecognitionMetadata is not null) { commit = kept ? null : text; _live = ""; _keptStart = start; }
					else if (text != _live) { _live = text; partial = text; }
				}
				if (error is not null)
				{
					finished = true;
					if (_live.Length > 0) { commit = _live; _live = ""; }
				}
				if (!finished) { goto publish; }
				_request = null;
				if (_stopping || _done) { goto publish; }
				// No speech heard, or the recognizer's own limit: a new request on the same tap. A task that dies at once, again and again,
				// is a failure (the model is not there).
				if (Environment.TickCount64 - _lastStartAt < 1500 && commit is null) { _quickFailures++; } else { _quickFailures = 0; }
				if (_quickFailures >= 4) { failure = SttFailureKind.Unknown; }
				else { restart = true; }
			}
		publish:
			if (partial is not null) { Partial?.Invoke(partial); }
			if (commit is { Length: > 0 }) { Committed?.Invoke(commit); }
			if (finished) { ended.TrySetResult(); }
			if (failure is { } f) { Finish(f); }
			else if (restart) { BeginTask(); }
		}

		// The system took the microphone (a call, Siri): what was heard is kept and the run ends as stopped by the system.
		private void Interrupted()
		{
			string? left = null;
			lock (_gate)
			{
				if (_done) { return; }
				_stopping = true;
				if (_live.Length > 0) { left = _live; _live = ""; }
			}
			if (left is not null) { Committed?.Invoke(left); }
			Finish(SttFailureKind.Audio);
		}

		public async Task StopAsync()
		{
			TaskCompletionSource? ended;
			lock (_gate)
			{
				if (_done) { return; }
				_stopping = true;
				ended = _taskEnded;
				_request?.EndAudio();
			}
			if (ended is not null) { await Task.WhenAny(ended.Task, Task.Delay(3000)); }
			string? left = null;
			lock (_gate)
			{
				if (_live.Length > 0) { left = _live; _live = ""; }
			}
			if (left is not null) { Committed?.Invoke(left); }
			Finish(null);
		}

		public void Cancel()
		{
			lock (_gate)
			{
				if (_done) { return; }
				_stopping = true;
				_live = "";
			}
			Finish(null);
		}

		private void Finish(SttFailureKind? failure)
		{
			SFSpeechRecognitionTask? task;
			lock (_gate)
			{
				if (_done && _ended.Task.IsCompleted) { return; }
				_done = true;
				task = _task;
				_task = null;
				_request = null;
			}
			try { task?.Cancel(); } catch (Exception ex) { Debug.WriteLine($"[Dictation] task cancel: {ex.GetType().Name}"); }
			_mic.Stop();
			_ended.TrySetResult(failure);
		}
	}
}
