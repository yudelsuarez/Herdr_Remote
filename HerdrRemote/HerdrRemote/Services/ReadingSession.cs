using System.Diagnostics;
using System.Globalization;
using HerdrRemote.Pages;
using Languages;

namespace HerdrRemote.Services;

// Owns finite reading (Read and voice samples) or streaming reading (Read from here). The page appends terminal sentences;
// streaming reading waits silently when none are available. This session retains no terminal, page, connection or view.
internal sealed partial class ReadingSession
{
	private CancellationTokenSource? _read;
	private CancellationTokenSource? _segment;
	private Task _run = Task.CompletedTask;
	private TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private ITtsProvider? _provider;
	private SpeechSettings? _settings;
	private bool _interrupted;
	private List<(string Text, int Paragraph)> _sentences = [];
	private bool _playing, _paused, _listening;
	// The page appends streaming sentences until End or PlaceLost; _more wakes the waiting read loop.
	private bool _streaming, _ended, _lost;
	private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _sentence, _target;
	private long _generation;
	private long _readyGeneration;
	private bool _errorShown;
	private int _rateSentence = -1;
	private float _deviceSentenceRate = 1;
	public float RequestedRate { get; private set; } = 1;
	public float AppliedRate { get; private set; } = 1;
	public bool RatePending => RequestedRate != AppliedRate;
	public string? RateNotice => _provider?.Id == "device" && RatePending ? i18n.Translate("Applies from the next sentence.") : null;

	public bool IsReading => _read is not null;
	public bool IsPreview { get; private set; }
	public bool CanContinue => !IsPreview && !IsReading && _interrupted && _settings is not null && _sentence < _sentences.Count;
	public bool CanPause => IsReading && _provider?.CanPause == true;
	public bool Paused => _paused;
	public string Phase => !IsReading ? "idle" : _paused ? "paused" : _listening ? "listening" : _playing ? "playing" : "waiting";
	public string SourceName { get; private set; } = "";
	public object? ReaderSource { get; private set; }
	public bool ReaderSourceReady => ReaderSource is not null && _readyGeneration == _generation;
	public string? PaneId { get; private set; }
	public string? Machine { get; private set; }
	public long Generation => _generation;
	public int Sentence => _sentence;
	public int SentenceCount => _sentences.Count;
	public bool CanPrevious => IsReading && _sentence > 0;
	public bool CanNext => IsReading && _sentence + 1 < _sentences.Count;
	public string? Error { get; private set; }
	public event Action? Changed;
	public event Action? ReaderSourceInvalidated;
	// Current sentence sequence number and phase: waiting, playing, paused or listening.
	public event Action<int, string>? StateChanged;

	public bool Matches(string machine, string paneId) => IsReading && Machine == machine && PaneId == paneId;
	// Remains true while an interrupted read awaits continuation, so the pane keeps its reading marks.
	public bool Owns(string machine, string paneId) => (IsReading || CanContinue) && Machine == machine && PaneId == paneId;
	public string? TakeError()
	{
		if (_errorShown || Error is null) { return null; }
		_errorShown = true;
		return Error;
	}

	// Starts finite reading by splitting paragraphs into sentences.
	public Task ReadAsync(IReadOnlyList<string> blocks, string? machine = null, string? paneId = null, string? sourceName = null)
		=> StartAsync(blocks, null, false, false, machine, paneId, TtsProviders.Current, null, 0, RememberedRate(TtsProviders.Current.Id), sourceName: sourceName);

	// Shares pre-split sentences between the Reader tab and speech; its identity token retains no view.
	public Task ReadReaderAsync(List<(string Text, int Paragraph)> sentences, object source)
		=> StartAsync(null, sentences, false, false, null, null, TtsProviders.Current, null, 0, RememberedRate(TtsProviders.Current.Id), readerSource: source);

	// Starts Read from here with initial sentences; the page supplies more through Append. End, PlaceLost or Stop ends reading.
	public Task StartStreamAsync(IReadOnlyList<string> texts, string machine, string paneId, string sourceName)
		=> StartAsync(null, [.. texts.Select((text, index) => (text, index))], true, false, machine, paneId, TtsProviders.Current, null, 0,
			RememberedRate(TtsProviders.Current.Id), sourceName: sourceName);

	public Task PreviewAsync(ITtsProvider provider, SpeechSettings settings, string language)
		=> StartAsync([TtsProviders.Samples[language]], null, false, false, null, null, provider, settings, 0, RememberedRate(provider.Id), preview: true);

	private static float RememberedRate(string provider) => App.Setting($"tts.{provider}.default_speed", "1") switch
	{ "0.8" => 0.8f, "1.25" => 1.25f, "1.5" => 1.5f, _ => 1f };

	public Task ContinueAsync() => CanContinue && _settings is { } settings
		? StartAsync(null, [.. _sentences], _streaming, true, Machine, PaneId, TtsProviders.All.First(p => p.Id == settings.ProviderId), settings, _sentence, RequestedRate, sourceName: SourceName)
		: Task.CompletedTask;

	// blocks supplies paragraphs to split for finite reading; held supplies pre-split sentences for streaming or continuation.
	// resume preserves the read's end and place-loss flags.
	private Task StartAsync(IReadOnlyList<string>? blocks, List<(string Text, int Paragraph)>? held, bool stream, bool resume, string? machine, string? paneId,
		ITtsProvider provider, SpeechSettings? settings, int start, float rate, bool preview = false, string? sourceName = null, object? readerSource = null)
	{
		bool replacesReader = !resume && ReaderSource is not null;
		_read?.Cancel();
		IsPreview = preview;
		SourceName = sourceName ?? i18n.Translate(preview ? "Voice sample" : "Reader");
		_interrupted = false;
		_streaming = stream;
		if (!resume) { _ended = _lost = false; ReaderSource = readerSource; }
		_listening = false;
		CancellationTokenSource reading = new();
		_read = reading;
		long generation = ++_generation;
		List<string> paragraphs = [.. (blocks ?? []).Where(b => b.Length > 0)];
		Task previous = _run;
		TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_run = done.Task;
		// The previous audio can still be releasing; don't let its Reader marks survive a handoff.
		if (replacesReader) { ReaderSourceInvalidated?.Invoke(); }
		_ = RunAsync(previous, done, reading, generation, provider, paragraphs, held, machine, paneId, settings, start, rate);
		return done.Task;
	}

	private async Task RunAsync(Task previous, TaskCompletionSource done, CancellationTokenSource reading, long generation, ITtsProvider provider,
		List<string> paragraphs, List<(string Text, int Paragraph)>? held, string? machine, string? paneId, SpeechSettings? settings, int startSentence, float rate)
	{
		CancellationToken ct = reading.Token;
		bool began = false;
		bool media = false;
		try
		{
			settings ??= provider.Capture();
			await previous;
			ct.ThrowIfCancellationRequested();
			_provider = provider;
			_settings = settings;
			_sentences = held ?? (IsPreview ? paragraphs.Select((text, index) => (text, index)).ToList() : PaneText.Sentences(paragraphs));
			Machine = machine;
			PaneId = paneId;
			_sentence = _target = startSentence;
			_readyGeneration = generation;
			_paused = _playing = _listening = false;
			RequestedRate = AppliedRate = _deviceSentenceRate = rate;
			_rateSentence = -1;
			if (!AudioPlayer.SetRate(rate)) { throw new SpeechStreamException(TtsFailureKind.PlaybackSpeed); }
			_resumed.TrySetResult();
			Error = null;
			_errorShown = false;
			// Notify the pane that reading has started and publish the first sentence's state.
			Report();
			Task ready = Task.CompletedTask;
			media = true;
			MediaStarting(generation, ref ready);
			await ready.WaitAsync(ct);
			ct.ThrowIfCancellationRequested();
			AudioPlayer.BeginRead();
			began = true;
			bool spoken = false;
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				if (_target >= _sentences.Count)
				{
					if (!await WaitMoreAsync(ct)) { break; }
					continue;
				}
				using CancellationTokenSource segment = CancellationTokenSource.CreateLinkedTokenSource(ct);
				_segment = segment;
				Task<(IPreparedSpeech Speech, int Sentence)?>? ahead = null;
				IPreparedSpeech? currentSpeech = null;
				try
				{
					await WaitResumedAsync(segment.Token);
					using Pieces pieces = new(this, provider, settings, _target, segment.Token);
					(IPreparedSpeech Speech, int Sentence)? current = pieces.Next();
					while (current is { } piece)
					{
						currentSpeech = piece.Speech;
						segment.Token.ThrowIfCancellationRequested();
						await WaitResumedAsync(segment.Token);
						_sentence = piece.Sentence;
						_target = _sentence;
						if (_rateSentence != _sentence) { _deviceSentenceRate = RequestedRate; _rateSentence = _sentence; }
						if (piece.Speech is DeviceSpeech device) { device.Rate = _deviceSentenceRate; }
						_playing = false;
						Report();
						ahead = AheadAsync(pieces, piece.Speech, segment.Token);
						spoken = true;
						string? error = await piece.Speech.PlayAsync(() =>
						{
							if (generation != _generation || segment.IsCancellationRequested) { return; }
							_playing = true;
							AppliedRate = provider.Id == "device" ? _deviceSentenceRate : RequestedRate;
							if (_paused) { AudioPlayer.Pause(); }
							Report();
						}, segment.Token);
						segment.Token.ThrowIfCancellationRequested();
						if (error is not null)
						{
							if (error == TtsFailure.Message(TtsFailureKind.PlaybackSpeed)) { RequestedRate = AppliedRate; }
							Error = error;
							_interrupted = !IsPreview && error == TtsFailure.Message(TtsFailureKind.Interrupted);
							return;
						}
						await WaitResumedAsync(segment.Token);
						current = await ahead;
					}
					// All held sentences have played; newly appended sentences start the next segment.
					_target = pieces.NextIndex;
				}
				catch (OperationCanceledException) when (!ct.IsCancellationRequested && segment.IsCancellationRequested) { }
				finally
				{
					segment.Cancel();
					if (ahead is not null)
					{
						try { if (await ahead is { } pending) { await pending.Speech.Released; } }
						// An abandoned lookahead cannot replace the current failure or notify a newer reading.
						catch (Exception) when (segment.IsCancellationRequested) { }
					}
					if (currentSpeech is not null) { await currentSpeech.Released; }
					if (_segment == segment) { _segment = null; }
				}
			}
			// End quietly when the place is lost; a popup would interrupt the user's terminal context.
			if (!spoken && !(_lost && generation == _generation)) { Error = i18n.Translate("No readable text in this selection."); }
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
		catch (SpeechStreamException ex)
		{
			if (generation == _generation) { Error = TtsFailure.Message(ex.Kind); _interrupted = !IsPreview && ex.Kind == TtsFailureKind.Interrupted; }
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[ReadingSession] {ex.GetType().Name}");
			if (generation == _generation) { Error = TtsFailure.Message(TtsFailureKind.Unknown); }
		}
		finally
		{
			if (began) { AudioPlayer.EndRead(); }
			if (media) { MediaEnded(generation); }
			if (_read == reading)
			{
				_read = null;
				_provider = null;
				if (!_interrupted) { _settings = null; }
				_paused = _playing = _listening = false;
				Changed?.Invoke();
			}
			reading.Dispose();
			done.TrySetResult();
		}
	}

	private async Task<(IPreparedSpeech Speech, int Sentence)?> AheadAsync(Pieces pieces, IPreparedSpeech current, CancellationToken ct)
	{
		await current.Fetched.WaitAsync(ct);
		await WaitResumedAsync(ct);
		ct.ThrowIfCancellationRequested();
		return pieces.Next();
	}

	// Enumerates speech for held sentences from the requested index, observing additions during streaming.
	// Next is null after all held sentences are returned; NextIndex then points to the first sentence not yet held.
	private sealed class Pieces(ReadingSession session, ITtsProvider provider, SpeechSettings settings, int first, CancellationToken ct) : IDisposable
	{
		private IEnumerator<IPreparedSpeech>? _inner;
		private int _sentence = first - 1;
		public int NextIndex { get; private set; } = first;

		public (IPreparedSpeech Speech, int Sentence)? Next()
		{
			while (true)
			{
				if (_inner is not null && _inner.MoveNext()) { return (_inner.Current, _sentence); }
				_inner?.Dispose();
				_inner = null;
				if (NextIndex >= session._sentences.Count) { return null; }
				_sentence = NextIndex++;
				_inner = TtsProviders.Prepare(provider, settings, session._sentences[_sentence].Text, ct).GetEnumerator();
			}
		}

		public void Dispose() => _inner?.Dispose();
	}

	// Releases audio while streaming reading waits for more text. Returns true when a sentence arrives,
	// or false when the page ends reading, the place is lost, or the read is not streaming.
	private async Task<bool> WaitMoreAsync(CancellationToken ct)
	{
		while (_target >= _sentences.Count)
		{
			if (!_streaming || _ended || _lost) { return false; }
			_more = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_listening = true;
			_playing = false;
			AudioPlayer.Idle();
			Report();
			try { await _more.Task.WaitAsync(ct); }
			finally { _listening = false; }
		}
		return true;
	}

	// Appends sentences starting at sequence number seq; skips duplicates and ignores gaps.
	public void Append(int seq, IReadOnlyList<string> texts)
	{
		if (!_streaming || _ended || _lost || !(IsReading || _interrupted) || seq < 0 || seq > _sentences.Count) { return; }
		bool added = false;
		for (int i = _sentences.Count - seq; i < texts.Count; i++)
		{
			_sentences.Add((texts[i], _sentences.Count));
			added = true;
		}
		if (!added) { return; }
		_more.TrySetResult();
		Changed?.Invoke();
	}

	// Ends reading after the last held sentence; no more sentences are expected.
	public void End()
	{
		if (!_streaming || _ended) { return; }
		_ended = true;
		_more.TrySetResult();
	}

	// Finishes held sentences and ends quietly after the page loses its terminal position.
	public void PlaceLost()
	{
		if (!_streaming || _lost) { return; }
		_lost = true;
		_more.TrySetResult();
	}

	public void Stop()
	{
		_generation++;
		_interrupted = false;
		_settings = null;
		Error = null;
		_read?.Cancel();
		if (ReaderSource is not null) { ReaderSourceInvalidated?.Invoke(); }
		if (_read is null) { Changed?.Invoke(); }
	}
	public void Previous() { if (CanPrevious) { Jump(_sentence - 1); } }
	public void Next() { if (CanNext) { Jump(_sentence + 1); } }
	private void Jump(int target)
	{
		_target = _sentence = target;
		_rateSentence = -1;
		_playing = false;
		_segment?.Cancel();
		_more.TrySetResult();
		Report();
	}
	public void TogglePause()
	{
		if (!CanPause) { return; }
		if (_paused)
		{
			bool allowed = true;
			MediaResuming(_generation, ref allowed);
			if (!allowed) { Error = TtsFailure.Message(TtsFailureKind.Interrupted); _errorShown = false; Report(); return; }
		}
		_paused = !_paused;
		if (_paused)
		{
			_resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
			if (_provider?.Id == "device") { _segment?.Cancel(); }
			else { AudioPlayer.Pause(); }
		}
		else { _resumed.TrySetResult(); AudioPlayer.Resume(); }
		Report();
	}
	public void CycleRate()
	{
		if (_read is not { IsCancellationRequested: false } || _provider is not { } provider || _readyGeneration != _generation || IsPreview) { return; }
		float next = RequestedRate switch { 0.8f => 1f, 1f => 1.25f, 1.25f => 1.5f, _ => 0.8f };
		bool device = provider.Id == "device";
		if (device || AudioPlayer.SetRate(next))
		{
			RequestedRate = next;
			if (!device && _playing) { AppliedRate = next; }
			App.SaveSetting($"tts.{provider.Id}.default_speed", next.ToString("0.##", CultureInfo.InvariantCulture));
		}
		else { Error = TtsFailure.Message(TtsFailureKind.PlaybackSpeed); _errorShown = false; }
		Report();
	}
	private Task WaitResumedAsync(CancellationToken ct) => _paused ? _resumed.Task.WaitAsync(ct) : Task.CompletedTask;
	private void Report()
	{
		MediaChanged(_generation);
		if (_sentence < _sentences.Count) { StateChanged?.Invoke(_sentence, Phase); }
		Changed?.Invoke();
	}

	// Native callbacks carry the read they were created for, never authority over its replacement.
	internal bool MediaCommand(long generation, string command)
	{
		if (generation != _generation || _read is not { IsCancellationRequested: false }) { return false; }
		switch (command)
		{
			case "stop": Stop(); return true;
			case "interrupt":
				_generation++;
				_interrupted = !IsPreview;
				Error = TtsFailure.Message(TtsFailureKind.Interrupted);
				_errorShown = false;
				_read.Cancel();
				if (ReaderSource is not null) { ReaderSourceInvalidated?.Invoke(); }
				return true;
			case "pause" when CanPause: if (!_paused) { TogglePause(); } return true;
			case "play" when CanPause: if (_paused) { TogglePause(); } return true;
			default: return false;
		}
	}

	partial void MediaStarting(long generation, ref Task ready);
	partial void MediaChanged(long generation);
	partial void MediaResuming(long generation, ref bool allowed);
	partial void MediaEnded(long generation);
}
