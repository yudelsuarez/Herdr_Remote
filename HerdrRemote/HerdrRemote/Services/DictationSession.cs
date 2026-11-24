using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

internal enum DictationState { Closed, Listening, Transcribing, Editing }

// Requests a pane message box without opening the draft.
internal sealed record DictationAlert(string Message, bool OpenSettings);

// App.Dictation owns the dictation flow: Closed -> Listening -> (Transcribing) -> Editing. It handles permissions,
// the 60-second silence timeout, the 10-minute limit and coordination with app reading. Drafts stay in memory, keyed by machine and pane.
// The pane view only renders this state. Results always go to the starting pane's draft, even if a cloud upload finishes after navigation.
internal sealed class DictationSession
{
	internal static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);
	internal static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(10);
	internal static readonly TimeSpan LimitWarning = TimeSpan.FromSeconds(30);
	// Input level that resets the silence timer; partial recognition results also reset it.
	private const float Sound = 0.35f;

	private enum RunState { Idle, Listening, Transcribing }

	private sealed class Run(ISpeechToText provider, IDictation dictation, string key, string before, string after)
	{
		public readonly ISpeechToText Provider = provider;
		public readonly IDictation Dictation = dictation;
		public readonly string Key = key;
		public string Before = before, After = after;
		public string Spoken = "", Live = "";
		public bool Heard, Closed;
		public SttFailureKind? Failure;
		// Marks a completed or cleared run whose text has been saved to the draft.
		public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public long StartedAt = Environment.TickCount64, ActiveAt = Environment.TickCount64;
	}

	private sealed class Retained(Run run, int at)
	{
		public readonly Run Run = run;
		public string Key => Run.Key;
		public int? At = at;
		public bool PlacementRequested;
	}

	private const string MovedBoundary = "The draft changed where this recording belongs. Place the caret and tap Retry.";

	private readonly Dictionary<string, string> _drafts = [];
	private readonly HashSet<string> _open = [];
	private Run? _run;
	private RunState _runState;
	private bool _foreground = true;
	private int _startGeneration;
	private string? _viewKey;
	// Retains failed cloud recordings and the draft locations where their retried results belong.
	private readonly List<Retained> _retry = [];

	public DictationSession() => AudioRecorder.DeleteStale();

	// Raised on the UI thread when draft state, text, status or timer changes.
	public event Action? Changed;
	public event Action<float>? Level;
	public event Action<DictationAlert>? Alert;

	public string Status { get; private set; } = "";
	public bool StatusIsError { get; private set; }
	public bool CanRetry => _retry.Any(retry => retry.Key == _viewKey);

	public DictationState State => _viewKey is null ? DictationState.Closed
		: _run is { } run && run.Key == _viewKey && _runState != RunState.Idle ? (_runState == RunState.Listening ? DictationState.Listening : DictationState.Transcribing)
		: _open.Contains(_viewKey) || !string.IsNullOrEmpty(_drafts.GetValueOrDefault(_viewKey)) ? DictationState.Editing : DictationState.Closed;

	public bool IsOpen => State != DictationState.Closed;

	public string ProviderName => SttProviders.Current.Name;

	// "ES" or "EN".
	public string LanguageChip => SttProviders.Language[..2].ToUpperInvariant();

	public TimeSpan Elapsed => _run is { } run && _runState == RunState.Listening ? TimeSpan.FromMilliseconds(Environment.TickCount64 - run.StartedAt) : TimeSpan.Zero;
	public bool NearLimit => Elapsed >= RunLimit - LimitWarning;

	// The visible pane's draft, including the current run's text while listening.
	public string Text => _viewKey is null ? "" : _run is { } run && run.Key == _viewKey && !run.Closed && _runState != RunState.Idle ? Compose(run) : _drafts.GetValueOrDefault(_viewKey, "");

	// The live segment's range within Text; the view displays it in a lighter color.
	public (int Start, int Length) LiveSpan
	{
		get
		{
			if (_run is not { } run || run.Key != _viewKey || run.Live.Length == 0 || _runState != RunState.Listening) { return (0, 0); }
			string head = Glue(Glue(run.Before, run.Spoken), run.Live);
			return (head.Length - run.Live.Length, run.Live.Length);
		}
	}

	// Selects the visible machine and pane, or null when none is visible; restores that pane's open draft.
	public void Attach(string? key)
	{
		if (_viewKey == key) { return; }
		_viewKey = key;
		Status = "";
		StatusIsError = false;
		Changed?.Invoke();
	}

	// The microphone key stops listening or starts dictation; a pending transcription explains why it cannot start.
	public Task MicAsync(int caret) => State switch
	{
		DictationState.Listening => StopAsync(),
		_ => StartAsync(caret),
	};

	public async Task StartAsync(int caret)
	{
		if (!_foreground || _viewKey is not { } key) { return; }
		if (_runState != RunState.Idle)
		{
			Alert?.Invoke(new DictationAlert(i18n.Translate("Dictation is still finishing. Try again in a moment."), false));
			return;
		}
		int generation = _startGeneration;
		bool CanStart() => _foreground && generation == _startGeneration && _viewKey == key && _runState == RunState.Idle;
		// Pause app reading before dictation; only its play control resumes it.
		if (App.Reading is { IsReading: true, Paused: false, CanPause: true } reading) { reading.TogglePause(); }
		PermissionStatus status;
		try
		{
			status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
			if (!CanStart()) { return; }
			if (status != PermissionStatus.Granted) { status = await Permissions.RequestAsync<Permissions.Microphone>(); }
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Dictation] microphone permission failed: {ex.GetType().Name}");
			if (CanStart()) { Alert?.Invoke(new DictationAlert(SttFailure.Message(SttFailureKind.Audio), false)); }
			return;
		}
		if (!CanStart()) { return; }
		if (status != PermissionStatus.Granted)
		{
			Alert?.Invoke(new DictationAlert(SttFailure.MicDenied(), true));
			return;
		}
		ISpeechToText provider = SttProviders.Current;
		string language = SttProviders.Language;
		string? problem;
		try { problem = await provider.CheckAsync(language, CancellationToken.None); }
		catch (Exception ex) { Debug.WriteLine($"[Dictation] check failed: {ex.GetType().Name}"); problem = SttFailure.NoOnDevice(language); }
		if (!CanStart()) { return; }
		if (problem is not null)
		{
			if (provider.Id == "device" && problem == SttFailure.SpeechDenied()) { Alert?.Invoke(new DictationAlert(problem, true)); return; }
			_open.Add(key);
			Show(problem, true);
			return;
		}
		string text = _drafts.GetValueOrDefault(key, "");
		int at = Math.Clamp(caret, 0, text.Length);
		IDictation dictation;
		try { dictation = provider.Start(language, DictationTerms.All); }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Dictation] start failed: {ex.GetType().Name}");
			_open.Add(key);
			Show(SttFailure.Message(SttFailureKind.Audio), true);
			return;
		}
		Run run = new(provider, dictation, key, text[..at], text[at..]);
		_run = run;
		_runState = RunState.Listening;
		_open.Add(key);
		Status = "";
		StatusIsError = false;
		dictation.Partial += live => MainThread.BeginInvokeOnMainThread(() =>
		{
			if (_run != run || _runState != RunState.Listening) { return; }
			run.Live = live;
			run.Heard = true;
			run.ActiveAt = Environment.TickCount64;
			Changed?.Invoke();
		});
		dictation.Committed += segment => MainThread.BeginInvokeOnMainThread(() => Commit(run, segment));
		dictation.Level += level => MainThread.BeginInvokeOnMainThread(() =>
		{
			if (_run != run || _runState != RunState.Listening) { return; }
			if (level >= Sound) { run.ActiveAt = Environment.TickCount64; }
			Level?.Invoke(level);
		});
		_ = dictation.Ended.ContinueWith(t => MainThread.BeginInvokeOnMainThread(() => Ended(run, t.IsCompletedSuccessfully ? t.Result : SttFailureKind.Unknown)));
		Changed?.Invoke();
		_ = TickAsync(run);
	}

	// Updates the timer once per second while listening and enforces the silence and duration limits.
	private async Task TickAsync(Run run)
	{
		while (true)
		{
			await Task.Delay(1000);
			if (_run != run || _runState != RunState.Listening) { return; }
			long now = Environment.TickCount64;
			if (now - run.StartedAt >= RunLimit.TotalMilliseconds || now - run.ActiveAt >= SilenceLimit.TotalMilliseconds)
			{
				_ = StopAsync();
				return;
			}
			Changed?.Invoke();
		}
	}

	private void Commit(Run run, string segment)
	{
		if (_run != run || run.Closed || string.IsNullOrWhiteSpace(segment)) { return; }
		run.Spoken = Glue(run.Spoken, segment.Trim());
		run.Live = "";
		run.Heard = true;
		Changed?.Invoke();
	}

	// Stops listening after a microphone-key press, draft tap, silence timeout or duration limit.
	// Commits the final device segment or starts the cloud upload.
	public async Task StopAsync()
	{
		if (_run is not { } run || _runState != RunState.Listening) { return; }
		_runState = run.Provider.Streams ? RunState.Listening : RunState.Transcribing;
		if (!run.Provider.Streams) { Show("", false); }
		Changed?.Invoke();
		try { await run.Dictation.StopAsync(); }
		catch (Exception ex) { Debug.WriteLine($"[Dictation] stop failed: {ex.GetType().Name}"); }
	}

	// Allows sending while listening: stops recognition and waits for the final words before the caller sends the draft.
	// Returns false if recognition fails or the visible pane changes; a failure leaves the draft and its notice intact.
	public async Task<bool> StopForSendAsync()
	{
		if (_run is not { } run || run.Key != _viewKey || run.Closed) { return true; }
		string key = run.Key;
		await StopAsync();
		await run.Done.Task;
		return run.Failure is null && _viewKey == key;
	}

	// Saves the completed run to its pane's draft and reports failures or an empty result in the status row.
	private void Ended(Run run, SttFailureKind? failure)
	{
		if (_run != run || run.Closed) { return; }
		Store(run);
		run.Closed = true;
		_run = null;
		_runState = RunState.Idle;
		bool retryable = failure is not null && run.Dictation is IRetryable { HasRecording: true };
		if (retryable) { _retry.Add(new Retained(run, run.Before.Length)); }
		string message = failure switch
		{
			null => run.Heard ? "" : SttFailure.Message(SttFailureKind.NothingHeard),
			SttFailureKind.Audio => i18n.Translate("Dictation stopped."),
			SttFailureKind.Unknown when run.Provider.Id == "device" => i18n.Translate("Dictation stopped."),
			{ } kind => SttFailure.Message(kind),
		};
		run.Failure = failure;
		if (run.Key == _viewKey) { Show(message, failure is not null || message.Length > 0); }
		else { Changed?.Invoke(); }
		run.Done.TrySetResult();
		Debug.WriteLine($"[Dictation] ended: failure={failure} heard={run.Heard}");
	}

	private void Store(Run run)
	{
		SetDraft(run.Key, Compose(run));
	}

	// Retries transcription of a retained cloud recording.
	public async Task RetryAsync(int? caret = null)
	{
		var retry = _retry.FirstOrDefault(item => item.Key == _viewKey);
		if (retry is null || _runState != RunState.Idle || retry.Run.Dictation is not IRetryable recording) { return; }
		if (retry.At is null)
		{
			if (!retry.PlacementRequested || caret is null)
			{
				retry.PlacementRequested = true;
				Show(i18n.Translate(MovedBoundary), true);
				return;
			}
			retry.At = Math.Clamp(caret.Value, 0, _drafts.GetValueOrDefault(retry.Key, "").Length);
		}
		Run takeover = new(retry.Run.Provider, recording, retry.Key, "", "") { Closed = true };
		_runState = RunState.Transcribing;
		_run = takeover;
		Show("", false);
		SttFailureKind? failure;
		string text = "";
		void Got(string segment) => text = segment;
		recording.Committed += Got;
		try { failure = await recording.RetryAsync(); }
		catch (Exception ex) { Debug.WriteLine($"[Dictation] retry failed: {ex.GetType().Name}"); failure = SttFailureKind.Unknown; }
		recording.Committed -= Got;
		if (_run != takeover) { return; }
		_run = null;
		_runState = RunState.Idle;
		if (failure is null)
		{
			_retry.Remove(retry);
			if (!string.IsNullOrWhiteSpace(text))
			{
				string draft = _drafts.GetValueOrDefault(retry.Key, "");
				int at = retry.At.Value;
				SetDraft(retry.Key, Glue(Glue(draft[..at], text.Trim()), draft[at..]));
				retry.Run.Heard = true;
			}
			Show(retry.Run.Heard ? "" : SttFailure.Message(SttFailureKind.NothingHeard), !retry.Run.Heard);
		}
		else { Show(SttFailure.Message(failure.Value), true); }
	}

	// Replaces the draft with the user's editor text; editing is disabled while listening.
	public void SetText(string text)
	{
		if (_viewKey is not { } key || State != DictationState.Editing) { return; }
		if (SetDraft(key, text))
		{
			foreach (var retry in _retry.Where(item => item.Key == key && item.At is null)) { retry.PlacementRequested = true; }
			Show(i18n.Translate(MovedBoundary), true);
		}
		// Typing clears stale run notices, but keeps Retry available for a retained recording.
		if (Status.Length > 0 && !CanRetry) { Show("", false); }
	}

	// Preserve retained insertion boundaries through edits, but never guess when an edit removes or ambiguously moves one.
	private bool SetDraft(string key, string text)
	{
		string before = _drafts.GetValueOrDefault(key, "");
		if (before == text) { return false; }
		int prefix = 0, suffix = 0;
		while (prefix < before.Length && prefix < text.Length && before[prefix] == text[prefix]) { prefix++; }
		while (suffix < before.Length && suffix < text.Length && before[^(suffix + 1)] == text[^(suffix + 1)]) { suffix++; }
		int shared = Math.Min(before.Length, text.Length);
		// Overlapping matches can explain more than one edit; use the union of their changed ranges.
		int start = Math.Min(prefix, shared - suffix);
		int end = before.Length - Math.Min(suffix, shared - prefix);
		bool moved = false;
		foreach (var retry in _retry.Where(item => item.Key == key))
		{
			if (retry.At is not { } at) { continue; }
			if (at >= start && at <= end)
			{
				retry.At = null;
				retry.PlacementRequested = false;
				moved = true;
			}
			else if (at > end) { retry.At += text.Length - before.Length; }
		}
		if (text.Length == 0) { _drafts.Remove(key); } else { _drafts[key] = text; }
		return moved;
	}

	// Sending or discarding clears the draft and closes its box.
	public void Clear()
	{
		if (_viewKey is not { } key) { return; }
		_startGeneration++;
		if (_run is { } run && run.Key == key) { _run = null; _runState = RunState.Idle; run.Closed = true; run.Done.TrySetResult(); run.Dictation.Cancel(); }
		DropRecording(key);
		_drafts.Remove(key);
		_open.Remove(key);
		Show("", false);
	}

	private void DropRecording(string key)
	{
		foreach (var retry in _retry.Where(item => item.Key == key)) { (retry.Run.Dictation as IRetryable)?.Discard(); }
		_retry.RemoveAll(item => item.Key == key);
	}

	// Leaving the pane stops listening and preserves its draft; an upload already in progress continues.
	public void Leave()
	{
		_startGeneration++;
		if (_viewKey is null) { return; }
		if (_run is { } run && run.Key == _viewKey && _runState == RunState.Listening) { _ = StopAsync(); }
		_viewKey = null;
		Status = "";
		StatusIsError = false;
		Changed?.Invoke();
	}

	// Stops listening when the app enters the background without closing the draft.
	public void Background()
	{
		_foreground = false;
		_startGeneration++;
		if (_runState == RunState.Listening) { _ = StopAsync(); }
	}

	public void Foreground() => _foreground = true;

	// Switches and saves the language for the next dictation run.
	public void SwitchLanguage()
	{
		App.SaveSetting("stt.lang", SttProviders.Language == "es-US" ? "en-US" : "es-US");
		Changed?.Invoke();
	}

	// Shows a status-row notice without closing the draft.
	public void Warn(string message) => Show(message, true);

	private void Show(string status, bool error)
	{
		Status = status;
		StatusIsError = error;
		Changed?.Invoke();
	}

	private static string Compose(Run run) => run.Spoken.Length == 0 && run.Live.Length == 0
		? run.Before + run.After : Glue(Glue(Glue(run.Before, run.Spoken), run.Live), run.After);

	// Joins text with one space unless either side already provides whitespace at the boundary.
	private static string Glue(string a, string b)
	{
		if (a.Length == 0) { return b; }
		if (b.Length == 0) { return a; }
		return char.IsWhiteSpace(a[^1]) || char.IsWhiteSpace(b[0]) ? a + b : a + " " + b;
	}
}
