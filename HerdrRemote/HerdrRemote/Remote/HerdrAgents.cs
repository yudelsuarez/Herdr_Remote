using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace HerdrRemote.Remote;

// A live terminal as displayed by the app. Word maps OSC 7501 status to "blocked" (blocked or error, requiring user attention),
// "working", "done" (an unwatched completed turn) or "idle". It is null if the program never reported status or has exited.
// App is the reported 7501 program name, such as "claude-code", "pi" or "codex". Names, statuses and menus use App.
// Mark selects the icon using herdr-app rules: prefer the reported app's programs-table key, retaining unknown app names,
// then an exact foreground-process match; null uses the terminal glyph. All icons use Mark.
// Space is the Arranged space name, the folder name for a pending space, or empty for unplaced terminals in the Mac group.
// Detail follows herdr-app's "space · detail" label: a named tab, then a meaningful agent title, then null or empty.
// Use an automatic tab number only to distinguish otherwise identical rows in a space. Title uses Detail, then the program name.
// ChangedMs is a Unix-millisecond timestamp for raw 7501 status changes, treating done and idle alike, as herdr-app does.
// On first discovery use status_ms from the core. Seq increments on any terminal's raw status change to order same-ms changes.
// TabId comes from the document tree, then herdr.tab, or null. Parent is the core's parent-terminal address, or null.
// Read size and folder live through SizeOf and FolderOf; cached copies would become stale between passes.
internal sealed record AgentTerminal(string Address, string Name, string? Word, string? App, string? Mark, string SpaceId, string Space, string? Detail, string Title, bool Pinned, long ChangedMs, long Seq, string? TabId, string? Parent);

// A tab from herdr-app's document. Name is empty for unnamed tabs. Number is its one-based document position; pending tabs
// follow the document's tabs. Root is null until herdr-app places a pending tab. Terminals lists live terminals in tree order,
// then any live terminals assigned to the tab but absent from its tree, ordered by creation.
internal sealed record AgentTab(string Id, string Name, int Number, JsonObject? Root, List<AgentTerminal> Terminals)
{
	// The tree as one compact string (HerdrLayout.Key): part of a row's value, so a layout change redraws it.
	public string Key { get; } = Root is null ? "" : HerdrLayout.Key(Root);
}

// A herdr-app space, or unplaced terminals grouped under the Mac's name (Id = "", Order = -1).
// For a space, Order is its document position and determines avatar color; Folder comes from the document or, if pending,
// its first terminal. Tabs includes only tabs with live terminals and is empty for the Mac group.
// Pending means the space's ID is not yet present in herdr-app's document.
internal sealed record AgentSpace(string Id, string Name, int Order, List<AgentTerminal> Terminals, string Folder, List<AgentTab> Tabs, bool Pending);

// Where one live terminal belongs (Place): its space (empty: the Mac's group) and tab, and whether the document's tree holds it.
internal readonly record struct Placement(string SpaceId, string? TabId, bool Tree);

// Maintain the Mac's live terminal model from one events.subscribe snapshot and subsequent events.
// Place terminals using herdr-app's document tree first, falling back to shared labels for terminals absent from the tree.
// Use Arranged for display names. Apply events as they arrive and gate updates at the source; each pass computes the model,
// notifications and herdr.seen writes together, at most once per PassGapMs, following herdr-app's tend_terminals order.
// All model work runs on the UI thread. Protocol details are in herdr-core's Events API and herdr-app's shared-label documentation.
internal sealed class HerdrAgents
{
	private const string Document = "herdr-app";
	private const string SeenLabel = "herdr.seen";

	// Agent entries from herdr-app's programs table plus Gentle Shell: key, display name, alternative 7501 names and launch argv.
	// An empty argv means herdr-app cannot launch the program. A title containing only the program name or key is not session-specific.
	private static readonly (string Key, string Name, string[] Match, string[] Run)[] Programs =
	[
		("claude", "Claude Code", ["claude-code"], ["claude"]), ("codex", "Codex", [], ["codex", "--no-daemon"]), ("pi", "Pi", [], ["pi"]),
		("pig", "PiG", [], ["pig"]), ("opencode", "OpenCode", [], ["opencode"]), ("cursor", "Cursor", ["cursor-agent"], ["cursor-agent"]),
		("copilot", "Copilot", ["github-copilot"], ["copilot"]), ("amp", "Amp", [], ["amp"]), ("grok", "Grok", ["grok-build"], ["grok"]),
		("dsh-tui", "DeepSeek Harness", [], ["dsh-tui"]), ("antigravity", "Antigravity", ["agy"], ["agy"]),
		("kimi", "Kimi", ["kimi-code", "kimicodecli"], ["kimi"]), ("kiro", "Kiro", ["kiro-cli"], ["kiro-cli"]), ("hermes", "Hermes", [], ["hermes"]),
		("mastracode", "Mastra Code", [], []), ("qoder", "Qoder", ["qodercli"], ["qodercli"]), ("muse", "Muse", [], []), ("crush", "Crush", [], ["crush"]),
		("command-code", "Command Code", [], []), ("prime-agent", "Prime", [], []), ("gemini", "Gemini", [], ["gemini"]), ("ollama", "Ollama", [], []),
		("gentle-shell", "Gentle Shell", [], ["gentle-shell"]),
	];

	// Match herdr-app's agent menu: list these programs first, then the remaining programs by key.
	private static readonly string[] AgentsFirst = ["claude", "codex", "pi", "opencode", "cursor", "copilot", "amp", "gemini", "grok"];

	// Every agent herdr-app can start (an entry with a command), in its menu order: key, shown name, command.
	internal static readonly (string Key, string Name, string[] Run)[] Startable = [.. Programs
		.Where(p => p.Run.Length > 0)
		.OrderBy(p => Array.IndexOf(AgentsFirst, p.Key) is >= 0 and var first ? first : AgentsFirst.Length)
		.ThenBy(p => p.Key, StringComparer.Ordinal)
		.Select(p => (p.Key, p.Name, p.Run))];

	private CoreApi? _api;
	private CoreEvents? _events;
	private CoreStream? _stream;
	// The last successful read of herdr-app's spaces and tabs; null if unwritten.
	// ReadDocumentAsync replaces it when the version changes; a failed read preserves it.
	private JsonNode? _document;
	// The version of _document, and whether a read of this Mac's document succeeded since its first connect.
	private ulong _docVersion;
	private bool _docRead;
	// One document read at a time: the running one, and whether one more must follow it.
	private Task _docTask = Task.CompletedTask;
	private bool _docReading;
	private bool _docAgain;
	// Retry failed document reads every DocRetryMs until one succeeds.
	private const int DocRetryMs = 2000;
	// Core name from hello, used as the group title for terminals without a space.
	private string _machine = "";
	// Settings-based machine identity (HerdrSession.MachineKey: user@host:port) for retained statuses and notifications.
	private string _machineKey = "";
	// Increment on Stop and each subscription so older read loops, document reads and scheduled passes cannot update the model.
	private int _epoch;
	// Cancellation for this subscription's calls and event loop; cancelled by Stop.
	private CancellationTokenSource _cancel = new();
	// After the subscription's initial pass, gated events, document updates and viewed-terminal changes can request passes.
	private bool _subscribed;
	// Something the pass reads changed since the last pass.
	private bool _dirty;
	// Last raw 7501 status per live terminal (RawWord; null means no current status). Preserve it across reconnects to the same
	// Mac, matching herdr-app's model retention. Existing addresses have been seen before; remove addresses that leave the live set.
	private readonly Dictionary<string, string?> _raw = [];
	// Live terminals eligible for notifications: assigned to a space and not sub-agents. Compare against the status recorded
	// while each terminal was in this set. A newly included terminal has no previous status, so a terminal already blocked on its
	// first listing requests attention. Remove it when it leaves the set.
	private readonly HashSet<string> _ringing = [];
	// Each terminal's status stamp (ChangedMs) and its seq (Seq); _seq is the last seq given (herdr-app's model.seq).
	private readonly Dictionary<string, long> _since = [];
	private readonly Dictionary<string, long> _seqs = [];
	private long _seq;
	// Fallback seen state for terminals without herdr.seen, matching herdr-app: false after an unwatched completion.
	private readonly Dictionary<string, bool> _seen = [];
	// Notifications for this Mac, keyed by terminal address: true means "needs you", false means "is done".
	// Pass and Look remove notifications when the terminal is answered, seen or gone, or no longer shows the reported status.
	private readonly Dictionary<string, bool> _noticed = [];
	// Suppress notifications for changes predating this Mac's first listing, matching herdr-app's listed_once.
	private bool _listedOnce;
	// Each live terminal's title as the last pass read it (Live): a title event that leaves it the same changes nothing shown.
	private Dictionary<string, string> _titles = [];
	// Each live terminal's foreground name as the last pass read it: a terminal's Name falls back to it.
	private Dictionary<string, string?> _fgNames = [];
	// Rate-limit passes, including redraw time, with a PassGapMs gap after the preceding pass ends (RequestPass).
	private const int PassGapMs = 500;
	// When the last pass ended (Environment.TickCount64).
	private long _builtAt;
	// A trailing pass is scheduled.
	private bool _buildPending;
	// A pass owns the model until its callbacks and redraws finish.
	private bool _passing;
	// Turns written to herdr.seen by this app, retained until the core's labels reflect the writes.
	private readonly Dictionary<string, long> _seenWritten = [];
	// The visible terminal (Look; null for none), foreground state, and last shared turn for each continuously viewed terminal.
	// Matches herdr-app's viewed-terminal tracking.
	private string? _look;
	private bool _foreground = true;
	private readonly Dictionary<string, long> _looked = [];
	// A background pass skipped Changed; raise it once on return to the foreground.
	private bool _changedAway;

	// The Mac's name (the Connection header's subtitle); empty before the first connect.
	internal string Machine => _machine;

	// Resolve a program by table key, then an alternative name. Unknown apps use their own name with word capitalization.
	// Return null when no app is reported.
	private static (string Key, string Name)? Listed(string? app) => app is null ? null
		: Programs.FirstOrDefault(p => string.Equals(p.Key, app, StringComparison.OrdinalIgnoreCase)) is { Key: not null } byKey ? (byKey.Key, byKey.Name)
		: Programs.FirstOrDefault(p => p.Match.Any(m => string.Equals(m, app, StringComparison.OrdinalIgnoreCase))) is { Key: not null } byMatch ? (byMatch.Key, byMatch.Name)
		: ("agent", OwnName(app));

	// Capitalize words in unknown app names: "gentle-shell" becomes "Gentle Shell".
	private static string OwnName(string app) => string.Join(' ', app.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
		.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

	// A program's shown name from its 7501 app name (the Pane header's subtitle).
	internal static string? ProgramName(string? app) => Listed(app)?.Name;

	// Select the icon program using herdr-app rules. Prefer the reported 7501 app: its table key, or its own name if unknown.
	// Otherwise match the foreground process name exactly against table keys, aliases or Run[0]; use foreground.name, then argv[0]
	// with its directory removed. Return null without a match. Both paths use one icon ID per program;
	// a process-only match does not make the terminal an agent.
	private static string? MarkOf(JsonObject record, string? app)
	{
		if (app is not null) { return Listed(app) is { Key: not "agent" } listed ? listed.Key : app; }
		string? process = record["foreground"]?["name"]?.GetValue<string>() is { Length: > 0 } name ? name
			: record["foreground"]?["argv"]?[0]?.GetValue<string>() is { Length: > 0 } argv0 ? Path.GetFileName(argv0) : null;
		if (process is null) { return null; }
		return Programs.FirstOrDefault(p => p.Key == process || p.Match.Contains(process, StringComparer.Ordinal) || (p.Run.Length > 0 && p.Run[0] == process)).Key;
	}

	// List document spaces and tabs in document order, with terminals in tree order; omit groups without live terminals.
	// Then list pending spaces, named by folder, and finally unplaced terminals under the Mac's name.
	// Within each space, Terminals follows tab order.
	public List<AgentSpace> Arranged { get; private set; } = [];

	// herdr-app's document as last read (null: never written, or not read yet).
	internal JsonNode? LayoutDocument => _document;

	// Resolve the folder at action time from live records: foreground-process cwd, then the terminal's own cwd.
	// Return null for unknown terminals.
	internal string? FolderOf(string address) =>
		_events?.Terminals.GetValueOrDefault(address) is not { } record ? null
		: record["foreground"]?["cwd"]?.GetValue<string>() is { Length: > 0 } live ? live
		: record["cwd"]?.GetValue<string>();

	// The terminal's startup cwd from live records, included in New space's folders in use. Null for an unknown terminal.
	internal string? CwdOf(string address) => _events?.Terminals.GetValueOrDefault(address)?["cwd"]?.GetValue<string>();

	// Current terminal size from live records; the displayed model may lag by PassGapMs. Null for unknown terminals.
	internal (int Cols, int Rows)? SizeOf(string address) =>
		_events?.Terminals.GetValueOrDefault(address) is not { } record ? null
		: (record["cols"]?.GetValue<int>() ?? 0, record["rows"]?.GetValue<int>() ?? 0);

	// Check the live records rather than the potentially delayed display model.
	internal bool IsLive(string address) => _events?.Terminals.GetValueOrDefault(address)?["state"]?.GetValue<string>() == "live";

	// Whether a live terminal currently has a process. A lost terminal has none until restarted, matching herdr-app's reattach rule.
	// Read directly from live records.
	internal bool HasPid(string address) => IsLive(address) && _events?.Terminals.GetValueOrDefault(address)?["pid"] is not null;

	// Every live terminal by address.
	public Dictionary<string, AgentTerminal> Terminals { get; private set; } = [];

	// Raised on the UI thread after Arranged and Terminals changed, while the app is in front.
	public event Action? Changed;
	// Retire cached state when terminals are authoritatively removed or the machine changes, not on transient disconnects.
	public event Action<string>? TerminalRetired;
	public event Action? ImageCacheReset;

	// The Pane screen updates this when shown, switched or hidden (null).
	// ShareSeen marks turns seen only while their terminal is visible and the app is in the foreground.
	public void Look(string? address)
	{
		if (_look == address) { return; }
		_look = address;
		if (_foreground && address is not null)
		{
			Services.AgentNotifier.DismissForPane(_machineKey, address);
			if (_noticed.Remove(address)) { Services.AgentNotifier.RemoveSystem(_machineKey, address); }
		}
		_dirty = true;
		RequestPass();
	}

	// App.CreateWindow sets this false on Stopped and true on Resumed. Background passes still compute state and notifications,
	// but set _changedAway instead of raising Changed. Leaving clears viewed turns, matching herdr-app on focus loss.
	// After a background pass, returning schedules a pass that shares the visible terminal's seen turn and raises Changed once.
	// If no pass ran in the background, nothing changed and no Changed event is raised.
	public bool Foreground
	{
		get => _foreground;
		set
		{
			if (_foreground == value) { return; }
			_foreground = value;
			if (!value)
			{
				Services.AgentNotifier.DismissInApp();
				_looked.Clear();
				return;
			}
			AdoptNotices();
			if (!_changedAway) { return; }
			_changedAway = false;
			_dirty = true;
			RequestPass();
		}
	}

	// Include this Mac's existing phone notifications, even from earlier app runs, in reconciliation on connect and foreground return.
	// Remove any whose terminals have since been answered, seen or closed.
	private async void AdoptNotices()
	{
		string machine = _machineKey;
		List<(string Pane, bool Needs)> delivered;
		try { delivered = await Services.AgentNotifier.DeliveredAsync(machine); }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Agents] delivered notices not read: {ex.Message}"); return; }
		if (machine != _machineKey) { return; }
		bool added = false;
		foreach ((string pane, bool needs) in delivered)
		{
			if (_noticed.TryAdd(pane, needs)) { added = true; }
		}
		if (!added) { return; }
		_dirty = true;
		RequestPass();
	}

	// On connect or reconnect, load the snapshot and herdr-app document, then process events. Return once the initial view is built.
	// machineKey identifies the Mac from settings. Preserve raw statuses, timestamps and the document for the same Mac so the first
	// pass can report changes during the gap, matching herdr-app. A different Mac starts fresh.
	public async Task StartAsync(CoreApi api, string machine, string machineKey)
	{
		// Process pending applied events before replacing their records, including notifications and seen writes.
		// A resubscribed stream retains the old records until the new snapshot arrives.
		if (_dirty && _events is not null) { Pass(); }
		int epoch = ++_epoch;
		_cancel.Cancel();
		_cancel.Dispose();
		_cancel = new CancellationTokenSource();
		_stream?.Close();
		_stream = null;
		_buildPending = false;
		_subscribed = false;
		_docReading = false;
		_docAgain = false;
		// Reevaluate seen state from labels, matching herdr-app clearing written_seen on resync.
		_seenWritten.Clear();
		_looked.Clear();
		if (machineKey != _machineKey)
		{
			_machineKey = machineKey;
			ImageCacheReset?.Invoke();
			_raw.Clear();
			_ringing.Clear();
			_since.Clear();
			_seqs.Clear();
			_seen.Clear();
			_listedOnce = false;
			_document = null;
			_docVersion = 0;
			_docRead = false;
		}
		CancellationToken ct = _cancel.Token;
		_api = api;
		_machine = machine;
		(CoreStream stream, JsonObject snapshot) = await api.SubscribeAsync(ct);
		if (epoch != _epoch) { stream.Close(); _ = stream.Drain(); return; }
		_stream = stream;
		_events = new CoreEvents(snapshot);
		_events.Changed += Events_Changed;
		// Subscribed first, then read: a change of the document after the read arrives as an event (API docs, "Documents").
		await ReadDocumentAsync();
		if (epoch != _epoch) { _ = stream.Drain(); return; }
		// The first pass of a subscription is direct, outside the gap.
		_subscribed = true;
		_dirty = true;
		Pass();
		AdoptNotices();
		_ = RunAsync(epoch, _events, stream, ct);
	}

	public void Stop()
	{
		// Process pending applied events, including notifications and seen writes, using this Mac's current records and names.
		if (_dirty && _events is not null) { Pass(); }
		_epoch++;
		_buildPending = false;
		_subscribed = false;
		_docReading = false;
		_docAgain = false;
		_cancel.Cancel();
		_cancel.Dispose();
		_cancel = new CancellationTokenSource();
		_stream?.Close();
		_stream = null;
		_events = null;
		_api = null;
		_seenWritten.Clear();
		_looked.Clear();
	}

	// Serialize document reads, matching herdr-app. Concurrent callers share the current task and request exactly one trailing read.
	// Replace the document after the first successful read for this Mac or a version change.
	// On failure, preserve the last successful value and retry after DocRetryMs until successful.
	private Task ReadDocumentAsync()
	{
		if (_docReading)
		{
			_docAgain = true;
			return _docTask;
		}
		_docReading = true;
		return _docTask = ReadDocumentLoopAsync(_epoch);
	}

	private async Task ReadDocumentLoopAsync(int epoch)
	{
		try
		{
			do
			{
				_docAgain = false;
				if (_api is not { } api) { return; }
				CoreDoc doc;
				try
				{
					doc = await api.DocReadAsync(Document, _cancel.Token);
				}
				catch (Exception ex)
				{
					if (epoch != _epoch) { return; }
					Debug.WriteLine($"[Herdr] doc.get failed: {ex.GetType().Name}: {ex.Message}");
					_ = Task.Delay(DocRetryMs).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() =>
					{
						if (epoch == _epoch) { _ = ReadDocumentAsync(); }
					}), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
					return;
				}
				if (epoch != _epoch) { return; }
				if (!_docRead || doc.Version != _docVersion)
				{
					_document = doc.Value;
					_docVersion = doc.Version;
					_docRead = true;
					_dirty = true;
					RequestPass();
				}
			}
			while (_docAgain);
		}
		finally
		{
			if (epoch == _epoch) { _docReading = false; }
		}
	}

	// Process events until the stream ends. Resubscribe after a core-initiated close; let the session recover a lost connection.
	private async Task RunAsync(int epoch, CoreEvents events, CoreStream stream, CancellationToken ct)
	{
		try
		{
			CoreFrame end = await events.RunAsync(stream, ct);
			if (epoch == _epoch && end.Type == CoreFrameType.Closed && _api is { } api) { await StartAsync(api, _machine, _machineKey); }
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Herdr] events ended: {ex.GetType().Name}: {ex.Message}");
			stream.Close();
			_ = stream.Drain();
		}
	}

	// CoreEvents already applied this event. Request a pass only if it can change the computed model.
	private void Events_Changed(CoreEvent change)
	{
		if (_events is not { } records) { return; }
		bool dirty;
		switch (change.Type)
		{
			// Ignore title changes that disappear after stripping leading marks, such as spinner frames. Pass reads titles through Live.
			case "title":
				dirty = change.Terminal is not { } titled || !_titles.TryGetValue(titled, out string? shownTitle)
					|| records.Terminals.GetValueOrDefault(titled) is not { } titledRecord || Live(titledRecord["title"]?.GetValue<string>() ?? "") != shownTitle;
				break;
			// Foreground-process changes affect MarkOf and the fallback Name when neither an hc name nor a title is available.
			case "foreground":
				dirty = change.Terminal is { } address && Terminals.GetValueOrDefault(address) is { } shown && records.Terminals.GetValueOrDefault(address) is { } record
					&& (MarkOf(record, shown.App) != shown.Mark || ForegroundName(record) != _fgNames.GetValueOrDefault(address));
				break;
			// Size is not cached in the model; Pane reads it live through SizeOf.
			case "size":
				dirty = false;
				break;
			// A fresh snapshot replaces all records. Reevaluate seen labels, matching herdr-app resync, and reread its document.
			case "missed":
				_seenWritten.Clear();
				_looked.Clear();
				_ = ReadDocumentAsync();
				dirty = true;
				break;
			// herdr-app renamed or rearranged its spaces: read its document again.
			case "doc" when (change.Data?["name"]?.GetValue<string>()) == Document:
				_ = ReadDocumentAsync();
				dirty = true;
				break;
			default:
				dirty = true;
				break;
		}
		if (!dirty) { return; }
		_dirty = true;
		RequestPass();
	}

	// Run immediately if the previous pass ended at least PassGapMs ago; otherwise schedule one trailing pass with current records.
	// Stop or a new subscription invalidates scheduled work through the epoch. StartAsync owns the first pass;
	// do not schedule another until it completes.
	private void RequestPass()
	{
		if (_passing || _buildPending || !_subscribed) { return; }
		long remaining = PassGapMs - (Environment.TickCount64 - _builtAt);
		if (remaining <= 0)
		{
			Pass();
			return;
		}
		_buildPending = true;
		SchedulePass(remaining, _epoch);
	}

	// Run after `delay` ms. iOS timers can fire slightly early (observed by 3 ms), so recheck the gap and wait any remaining time.
	private void SchedulePass(long delay, int epoch)
	{
		_ = Task.Delay(TimeSpan.FromMilliseconds(delay)).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() =>
		{
			if (epoch != _epoch) { return; }
			long remaining = PassGapMs - (Environment.TickCount64 - _builtAt);
			if (remaining > 0)
			{
				SchedulePass(remaining, epoch);
				return;
			}
			_buildPending = false;
			Pass();
		}), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
	}

	// Trim the title and strip characters before the first letter or digit, including spinners, stars and path prefixes (~ or /).
	private static string Live(string title) => string.Concat(title.SkipWhile(c => !char.IsLetter(c) && !char.IsNumber(c))).Trim();

	// What the core reports as running in the terminal (its foreground process name), or null.
	private static string? ForegroundName(JsonObject record) => record["foreground"]?["name"]?.GetValue<string>();

	// A terminal is an agent only while its program reports status, matching herdr-app. Status may be an object or bare string;
	// it is null before the first report and after process exit.
	private static bool IsAgent(JsonObject record) => record["status"] is JsonObject || RawWord(record) is not null;

	// The reported 7501 state: status.state for an object, or status itself for a bare string; null without a status.
	private static string? RawWord(JsonObject record) => CoreEvents.StatusWord(record["status"]);

	// The program's 7501 app name, or null (none reported, or a bare-word status).
	private static string? AppOf(JsonObject record) => (record["status"] as JsonObject)?["app"]?.GetValue<string>() is { Length: > 0 } app ? app : null;

	// Parse herdr.seen as a turn number. A missing label is null, not turn 0, as specified by herdr-app's label protocol.
	private static long? SeenLabelOf(JsonObject record) =>
		long.TryParse(record["labels"]?[SeenLabel]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out long seen) ? seen : null;

	private static long TurnOf(JsonObject record) => record["turn"]?.GetValue<long>() ?? 0;

	// Match herdr-app's attention rule for raw 7501 state changes: true for newly blocked or error, false for busy-to-not-busy
	// while unwatched (done), and null for no alert.
	private static bool? Alert(string? previous, string? next, bool looking)
	{
		static bool Blocked(string? w) => w is "blocked" or "error";
		static bool Busy(string? w) => w is "working" or "blocked" or "error";
		return Blocked(next) && !Blocked(previous) ? true
			: Busy(previous) && next is not null && !Busy(next) && !looking ? false
			: null;
	}

	// Treat done and idle as one timestamp state, matching herdr-app's agent list.
	private static string? Merged(string? word) => word == "done" ? "idle" : word;

	// A terminal is viewed only when its Pane is visible and the app is in the foreground.
	private bool Looking(string address) => _foreground && address == _look;

	// Displayed live-agent status, matching herdr-app: blocked/error requires attention; busy is working.
	// Otherwise show done if the turn ended after the last seen turn, using herdr.seen or a local write not yet reflected by the core;
	// show idle if seen. Without either source, use herdr-app's fallback seen rule (_seen).
	// Return null for non-live terminals or programs without current status, including programs that never reported or have exited.
	private string? WordOf(string address, JsonObject record, bool notice = false)
	{
		if (record["state"]?.GetValue<string>() != "live" || RawWord(record) is not { } raw) { return null; }
		if (raw is "blocked" or "error") { return "blocked"; }
		if (raw == "working") { return "working"; }
		long? seenTurn = SeenLabelOf(record);
		if (_seenWritten.TryGetValue(address, out long written) && (seenTurn is null || written > seenTurn)) { seenTurn = written; }
		return seenTurn is long seen ? TurnOf(record) > seen ? "done" : "idle"
			: notice ? Looking(address) ? "idle" : "done" : _seen.GetValueOrDefault(address, true) ? "idle" : "done";
	}

	// Place live terminals using herdr-app rules: document trees define membership; labels place only terminals absent from trees.
	// Visit local terminals once in space, tab and tree order, then remaining local terminals with herdr.space labels in their
	// labelled space and tab. Put all remaining terminals, including other machines' terminals, in the Mac group.
	private Dictionary<string, Placement> Place(JsonArray spaces, CoreEvents events)
	{
		Dictionary<string, Placement> placed = [];
		foreach (JsonObject space in spaces.OfType<JsonObject>())
		{
			if (HerdrLayout.Id(space) is not { } spaceId) { continue; }
			foreach (JsonObject tab in HerdrLayout.Tabs(space))
			{
				if (tab["root"] is not JsonObject root) { continue; }
				string tabId = HerdrLayout.Id(tab) ?? "";
				foreach (string address in HerdrLayout.Terminals(root))
				{
					if (!address.Contains('/', StringComparison.Ordinal) && !placed.ContainsKey(address)
						&& events.Terminals.GetValueOrDefault(address)?["state"]?.GetValue<string>() == "live")
					{
						placed[address] = new Placement(spaceId, tabId, true);
					}
				}
			}
		}
		foreach ((string address, JsonObject record) in events.Terminals)
		{
			if (record["state"]?.GetValue<string>() != "live" || placed.ContainsKey(address)) { continue; }
			string? spaceId = address.Contains('/', StringComparison.Ordinal) ? null : record["labels"]?["herdr.space"]?.GetValue<string>();
			placed[address] = spaceId is { Length: > 0 } ? new Placement(spaceId, record["labels"]?["herdr.tab"]?.GetValue<string>(), false) : new Placement("", null, false);
		}
		return placed;
	}

	// Match herdr-app's tend_terminals order: placements; raw states, alerts, timestamps and sequence numbers; seen writes; model;
	// notifications (only after this Mac's initial listing); then Changed while in the foreground.
	// Skip the pass if none of its inputs changed.
	private void Pass()
	{
		if (_passing || _events is not { } events || !_dirty) { return; }
		int epoch = _epoch;
		_passing = true;
		bool completed = false;
		try
		{
			_dirty = false;
			JsonArray spaces = _document?["spaces"] as JsonArray ?? [];
			Dictionary<string, Placement> placed = Place(spaces, events);
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			List<(string Address, bool Needs)> alerts = [];
			foreach ((string address, JsonObject record) in events.Terminals)
			{
				if (record["state"]?.GetValue<string>() != "live") { continue; }
				string? next = RawWord(record);
				bool looking = Looking(address);
				string? previous = _raw.GetValueOrDefault(address);
				// Notify only for terminals assigned to a space and not sub-agents. Suppress notifications while viewed.
				// Compare against the status recorded while eligible; newly eligible terminals have no previous status (_ringing).
				bool rings = placed[address].SpaceId.Length > 0 && string.IsNullOrEmpty(record["parent"]?.GetValue<string>());
				if (rings && Alert(_ringing.Contains(address) ? previous : null, next, looking) is bool needs && !looking)
				{
					alerts.Add((address, needs));
				}
				if (rings) { _ringing.Add(address); }
				else { _ringing.Remove(address); }
				if (!_since.ContainsKey(address))
				{
					_seqs[address] = ++_seq;
					_since[address] = record["status_ms"]?.GetValue<long>() is long stamp and > 0 ? stamp : now;
					_seen[address] = true;
				}
				else if (previous != next)
				{
					_seqs[address] = ++_seq;
					if (Merged(previous) != Merged(next)) { _since[address] = now; }
					// Match herdr-app's set_word: busy counts as seen; an unwatched completion does not.
					if (next is "working" or "blocked") { _seen[address] = true; }
					else if (next is not null && previous is "working" or "blocked") { _seen[address] = looking; }
				}
				_raw[address] = next;
				// The core now reflects this app's seen write; use its label again.
				if (_seenWritten.TryGetValue(address, out long written) && SeenLabelOf(record) is long label && label >= written) { _seenWritten.Remove(address); }
			}
			foreach (string gone in _raw.Keys.Where(address => !placed.ContainsKey(address)).ToList())
			{
				_raw.Remove(gone);
				_ringing.Remove(gone);
				_since.Remove(gone);
				_seqs.Remove(gone);
				_seen.Remove(gone);
				_seenWritten.Remove(gone);
				TerminalRetired?.Invoke(gone);
				if (epoch != _epoch) { return; }
			}
			ShareSeen();
			Build(spaces, placed, events);
			// Remove notifications when their terminal has been answered here or on the Mac, seen, closed or otherwise changed status.
			foreach ((string address, bool needs) in _noticed.ToList())
			{
				string? word = events.Terminals.TryGetValue(address, out JsonObject? record) ? WordOf(address, record, notice: true) : null;
				if (word != (needs ? "blocked" : "done"))
				{
					_noticed.Remove(address);
					Services.AgentNotifier.RemoveSystem(_machineKey, address);
				}
			}
			// Use current names for notifications. Suppress changes predating this Mac's first listing, matching herdr-app.
			if (_listedOnce)
			{
				foreach ((string address, bool needs) in alerts)
				{
					if (!Terminals.TryGetValue(address, out AgentTerminal? terminal)) { continue; }
					_noticed[address] = needs;
					Services.AgentNotifier.Show(_machineKey, address, needs,
						string.Format(CultureInfo.CurrentCulture, Languages.i18n.Translate(needs ? "{0} needs you" : "{0} is done"), terminal.Name),
						terminal.Space.Length > 0 ? terminal.Space : _machine);
				}
			}
			_listedOnce = true;
			// Skip background redraws; the next Foreground pass raises Changed once.
			if (_foreground)
			{
				Changed?.Invoke();
				if (epoch != _epoch) { return; }
			}
			else { _changedAway = true; }
			completed = true;
		}
		finally
		{
			_passing = false;
			// Record completion after redraws, even on failure, so the next pass waits the full gap.
			if (epoch == _epoch) { _builtAt = Environment.TickCount64; }
		}
		if (completed && epoch == _epoch && _dirty && _subscribed) { RequestPass(); }
	}

	// Match herdr-app's share_seen: raise herdr.seen to the visible terminal's turn while the app is in the foreground, even when
	// the label is missing. Write once per turn while continuously viewed, so leaving the screen open does not undo "mark unread"
	// elsewhere. Show it as seen immediately (_seenWritten). On write failure, clear both local trackers and request another pass.
	private void ShareSeen()
	{
		string? look = _foreground ? _look : null;
		foreach (string stale in _looked.Keys.Where(address => address != look).ToList()) { _looked.Remove(stale); }
		if (look is null || _api is not { } api || _events?.Terminals.GetValueOrDefault(look) is not { } record
			|| record["state"]?.GetValue<string>() != "live" || RawWord(record) is null) { return; }
		long turn = TurnOf(record);
		if (_looked.TryGetValue(look, out long lookedTurn) && lookedTurn == turn) { return; }
		_looked[look] = turn;
		long? seenTurn = SeenLabelOf(record);
		if (_seenWritten.TryGetValue(look, out long written) && (seenTurn is null || written > seenTurn)) { seenTurn = written; }
		if (seenTurn >= turn) { return; }
		_seenWritten[look] = turn;
		_ = WriteSeenAsync(api, look, turn, _epoch);
	}

	private async Task WriteSeenAsync(CoreApi api, string address, long turn, int epoch)
	{
		try
		{
			await api.LabelAsync(address, SeenLabel, turn.ToString(CultureInfo.InvariantCulture), _cancel.Token);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Herdr] seen label failed: {ex.GetType().Name}: {ex.Message}");
			if (epoch == _epoch && _seenWritten.TryGetValue(address, out long written) && written == turn)
			{
				_seenWritten.Remove(address);
				_looked.Remove(address);
				// MainThread may run inline: the pass guard keeps this dirty retry for the trailing pass after the current one ends.
				MainThread.BeginInvokeOnMainThread(() =>
				{
					if (epoch != _epoch) { return; }
					_dirty = true;
					RequestPass();
				});
			}
		}
	}

	// Build Arranged and Terminals from live records, placements and herdr-app's document.
	private void Build(JsonArray spaces, Dictionary<string, Placement> placed, CoreEvents events)
	{
		// Index document tabs by space and ID, with their positions. Build once per model, not once per terminal.
		Dictionary<(string Space, string Tab), (JsonObject Tab, int Index)> documentTabs = [];
		foreach (JsonObject space in spaces.OfType<JsonObject>())
		{
			if (HerdrLayout.Id(space) is not { } spaceId) { continue; }
			int index = 0;
			foreach (JsonObject tab in HerdrLayout.Tabs(space))
			{
				if (HerdrLayout.Id(tab) is { } tabId) { documentTabs.TryAdd((spaceId, tabId), (tab, index)); }
				index++;
			}
		}
		Dictionary<string, AgentTerminal> terminals = [];
		// Snapshot each terminal's where label and creation time for Arrange.
		Dictionary<string, (string ShownSpace, long Created)> raw = [];
		// Compute each terminal's list label using herdr-app rules, and its program display name.
		Dictionary<string, (string Label, string? Program)> labels = [];
		// Cache normalized live titles and foreground names for event-change detection.
		Dictionary<string, string> titles = [];
		Dictionary<string, string?> fgNames = [];
		// Count status-reporting terminals per tab; multi-agent tabs need each row to show its own title.
		Dictionary<string, int> agentsPerTab = events.Terminals
			.Where(pair => pair.Value["state"]?.GetValue<string>() == "live" && IsAgent(pair.Value))
			.Select(pair => placed[pair.Key].TabId)
			.OfType<string>()
			.CountBy(tabId => tabId)
			.ToDictionary();
		foreach ((string address, JsonObject record) in events.Terminals)
		{
			if (record["state"]?.GetValue<string>() != "live") { continue; }
			Placement place = placed[address];
			string? tabId = place.TabId;
			(JsonObject Tab, int Index)? tab = place.SpaceId.Length > 0 && tabId is not null && documentTabs.TryGetValue((place.SpaceId, tabId), out var found) ? found : null;
			// Use herdr-app's where label ("<space name> / <tab name>") for display names, including renames and moves.
			// herdr-app names an unnamed space after its first terminal's live checkout folder.
			// Split at the last " / " because space names can contain that separator.
			string where = record["labels"]?["where"]?.GetValue<string>() ?? "";
			int cut = where.LastIndexOf(" / ", StringComparison.Ordinal);
			string? app = AppOf(record);
			(string Key, string Name)? listed = Listed(app);
			// Strip leading title marks (spinners, stars and path prefixes ~ or /).
			// A Windows path, user@host string or bare program name/key is not a meaningful session title.
			string live = Live(record["title"]?.GetValue<string>() ?? "");
			// A program built on pi that keeps pi's default title ("π - <folder>", gentle-shell) shows its own name there, not pi's.
			// Listing Gentle Shell for launch does not change how its titles are interpreted.
			string? titleKey = listed?.Key == "gentle-shell" ? "agent" : listed?.Key;
			bool piTitle = titleKey == "agent" && live.StartsWith("π - ", StringComparison.Ordinal);
			if (piTitle) { live = listed!.Value.Name; }
			titles[address] = live;
			fgNames[address] = ForegroundName(record);
			bool telling = piTitle || live.Length > 0 && live != listed?.Name && !(live.Length > 2 && char.IsAsciiLetter(live[0]) && live[1] == ':' && live[2] == '\\')
				&& !live.Contains('@', StringComparison.Ordinal) && !string.Equals(live, titleKey, StringComparison.OrdinalIgnoreCase);
			// Match herdr-app's agent labels: prefer a meaningful agent title for unnamed or program-named tabs and multi-agent tabs.
			// Otherwise use the tab name, then its automatic position number.
			// For a terminal without a known tab, use the normalized title, then the program name.
			string tabName = tab?.Tab["name"]?.GetValue<string>() ?? "";
			bool many = tabId is not null && agentsPerTab.GetValueOrDefault(tabId) > 1;
			string label = tab is not { } known ? (live.Length > 0 ? live : listed?.Name ?? "")
				: (tabName.Length == 0 || many || tabName == listed?.Name) && telling ? live
				: tabName.Length > 0 ? tabName
				: (known.Index + 1).ToString(CultureInfo.InvariantCulture);
			labels[address] = (label, listed?.Name);
			// Its hc name, else its title without its leading marks, else what runs in it, else its address.
			string name = record["name"]?.GetValue<string>() is { Length: > 0 } named ? named
				: live.Length > 0 ? live
				: fgNames[address] is { Length: > 0 } program ? program
				: address;
			AgentTerminal terminal = new(
				address,
				name,
				WordOf(address, record),
				app,
				MarkOf(record, app),
				place.SpaceId,
				"",
				IsTabNumber(label) ? null : label,
				"",
				record["labels"]?["herdr.pinned"]?.GetValue<string>() is { Length: > 0 },
				_since.GetValueOrDefault(address),
				_seqs.GetValueOrDefault(address),
				tabId,
				record["parent"]?.GetValue<string>());
			terminals[address] = terminal;
			raw[address] = (cut >= 0 ? where[..cut] : "", record["created_ms"]?.GetValue<long>() ?? 0);
		}
		// Build Arranged first: prefer document name, then where label, then folder name; pending spaces use their folder.
		// Copy each arranged space's ID and name to its terminals so notifications and menus match the displayed list.
		List<AgentSpace> arranged = Arrange(spaces, placed, terminals, raw);
		foreach (AgentTerminal member in arranged.SelectMany(space => space.Terminals)) { terminals[member.Address] = member; }
		// Use tab numbers to distinguish otherwise identical agent rows within a space.
		// Under a space header, show the row's detail, then its program name as fallback.
		HashSet<string> alike = [.. terminals.Values.Where(t => t.Word is not null && string.IsNullOrEmpty(t.Detail))
			.GroupBy(t => t.Space).Where(same => same.Count() > 1).Select(same => same.Key)];
		foreach (string address in terminals.Keys.ToList())
		{
			AgentTerminal member = terminals[address];
			(string label, string? program) = labels[address];
			string? detail = member.Detail is null && member.Word is not null && alike.Contains(member.Space) ? label : member.Detail;
			terminals[address] = member with { Detail = detail, Title = detail ?? (program is { Length: > 0 } ? program : label) };
		}
		// The spaces and tabs hold the terminals with their details and titles.
		Arranged = [.. arranged.Select(space => space with
		{
			Terminals = [.. space.Terminals.Select(t => terminals[t.Address])],
			Tabs = [.. space.Tabs.Select(tab => tab with { Terminals = [.. tab.Terminals.Select(t => terminals[t.Address])] })],
		})];
		Terminals = terminals;
		_titles = titles;
		_fgNames = fgNames;
	}

	// Arrange each live terminal exactly once from the document and placements. Include a tab only if it has a live terminal,
	// and a space only if one of its tabs does. Within tabs, list tree terminals first, then labelled terminals by creation time.
	// Name spaces from the document, then herdr-app's where label, folder name and first terminal's name.
	// Pending spaces use their first terminal's folder, matching herdr-app adoption. Pending tabs follow the known space's document tabs.
	private List<AgentSpace> Arrange(JsonArray spaces, Dictionary<string, Placement> placed, Dictionary<string, AgentTerminal> terminals, Dictionary<string, (string ShownSpace, long Created)> raw)
	{
		List<AgentSpace> arranged = [];
		HashSet<string> known = [.. spaces.Select(HerdrLayout.Id).OfType<string>()];
		// A space's live terminals, by creation.
		List<AgentTerminal> Of(string spaceId) => [.. terminals.Values.Where(t => placed[t.Address].SpaceId == spaceId).OrderBy(t => raw[t.Address].Created)];
		// Group live terminals absent from document tabs by tab label, in creation order, assigning numbers from `number`.
		static List<AgentTab> Pending(IEnumerable<AgentTerminal> members, int number) => [.. members
			.GroupBy(t => t.TabId ?? "")
			.Select((tab, i) => new AgentTab(tab.Key, "", number + i, null, [.. tab]))];
		for (int order = 0; order < spaces.Count; order++)
		{
			if (spaces[order] is not JsonObject space || HerdrLayout.Id(space) is not { } spaceId) { continue; }
			List<AgentTerminal> members = Of(spaceId);
			List<AgentTab> tabs = [];
			HashSet<string> placedTabs = [];
			int number = 0;
			foreach (JsonObject tab in HerdrLayout.Tabs(space))
			{
				number++;
				string tabId = HerdrLayout.Id(tab) ?? "";
				placedTabs.Add(tabId);
				JsonObject? root = tab["root"] as JsonObject;
				List<string> tree = root is null ? [] : HerdrLayout.Terminals(root);
				List<AgentTerminal> live = [.. tree.Distinct().Where(address => placed.TryGetValue(address, out Placement p) && p.Tree && p.SpaceId == spaceId && p.TabId == tabId)
						.Select(address => terminals[address]),
					.. members.Where(t => !placed[t.Address].Tree && t.TabId == tabId)];
				if (live.Count > 0) { tabs.Add(new AgentTab(tabId, tab["name"]?.GetValue<string>() ?? "", number, root, live)); }
			}
			tabs.AddRange(Pending(members.Where(t => !placed[t.Address].Tree && !placedTabs.Contains(t.TabId ?? "")), number + 1));
			if (tabs.Count == 0) { continue; }
			List<AgentTerminal> ordered = [.. tabs.SelectMany(tab => tab.Terminals)];
			string folder = space["folder"]?.GetValue<string>() ?? "";
			string name = space["name"]?.GetValue<string>() is { Length: > 0 } named ? named
				: ordered.Select(t => raw[t.Address].ShownSpace).FirstOrDefault(shown => shown.Length > 0) is { } shownSpace ? shownSpace
				: Path.GetFileName(folder.TrimEnd('/')) is { Length: > 0 } folderName ? folderName
				: ordered[0].Name;
			arranged.Add(Named(new AgentSpace(spaceId, name, order, ordered, folder, tabs, false)));
		}
		int next = spaces.Count;
		foreach (string spaceId in terminals.Values.Select(t => placed[t.Address].SpaceId).Where(id => id.Length > 0 && !known.Contains(id))
			.Distinct().OrderBy(id => Of(id).Min(t => raw[t.Address].Created)).ToList())
		{
			List<AgentTab> tabs = Pending(Of(spaceId), 1);
			List<AgentTerminal> ordered = [.. tabs.SelectMany(tab => tab.Terminals)];
			string folder = FolderOf(ordered[0].Address) ?? "";
			string name = Path.GetFileName(folder.TrimEnd('/')) is { Length: > 0 } folderName ? folderName : ordered[0].Name;
			arranged.Add(Named(new AgentSpace(spaceId, name, next++, ordered, folder, tabs, true)));
		}
		List<AgentTerminal> machine = Of("");
		if (machine.Count > 0) { arranged.Add(new AgentSpace("", _machine, -1, [.. machine.Select(t => t with { SpaceId = "", Space = "" })], "", [], false)); }
		return arranged;
	}

	// Copy the space ID and name into its terminals, including the copies held by tabs.
	private static AgentSpace Named(AgentSpace space)
	{
		List<AgentTab> tabs = [.. space.Tabs.Select(tab => tab with { Terminals = [.. tab.Terminals.Select(t => t with { SpaceId = space.Id, Space = space.Name })] })];
		return space with { Terminals = [.. tabs.SelectMany(tab => tab.Terminals)], Tabs = tabs };
	}

	// An unnamed tab's automatic name: its place in its space (1, 2, ...).
	private static bool IsTabNumber(string label) => label.Length > 0 && label.All(char.IsAsciiDigit);
}
