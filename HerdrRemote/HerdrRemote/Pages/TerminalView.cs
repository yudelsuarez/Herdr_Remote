using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HerdrRemote.Remote;
using HerdrRemote.Services;
using Languages;

namespace HerdrRemote.Pages;

// xterm.js 6.0.0 in a HybridWebView, built from terminal-web sources into Resources/Raw/wwwroot.
// The bridge sends one JSON string per raw message in either direction and caches reported values; never fetch them.
// Render raw stream output with 2000+ scrollback lines; input bypasses the web page. Report input modes for
// the Pane to encode keys. Queue pooled frames until ready, then send batches with at most two in flight,
// encoding base64 directly into a reused JSON buffer. Write callbacks return credits and raise Written with
// stream byte counts for flow control. A missing credit after five seconds resets the view and raises Stalled;
// reattachment repaints the full screen. Arm one watchdog for the oldest batch only, and suspend it while Held.
public sealed partial class TerminalView : HybridWebView
{
	private const int MaxBatchesInFlight = 2;
	// Batch caps: 64 frames or 1 MiB of base64 characters.
	private const int MaxBatchFrames = 64;
	private const int MaxBatchChars = 1 << 20;
	private static readonly TimeSpan CreditTimeout = TimeSpan.FromSeconds(5);

	// A batch in flight: when it was posted (Stopwatch ticks; restarted when the view stops being held) and the stream bytes it
	// holds (0 once a reset made it belong to an older stream).
	private sealed class Batch(long posted, int bytes, long generation)
	{
		public long Posted { get; set; } = posted;
		public int Bytes { get; set; } = bytes;
		public long Generation { get; } = generation;
	}

	// Frames waiting: a pooled array (returned once written into a batch, or dropped by Reset), its length and its stream bytes.
	private readonly Queue<(byte[] Data, int Length, int Bytes)> _pending = new();
	// One batch per write in flight, oldest first.
	private readonly Queue<Batch> _inFlight = new();
	// The frames message, reused by every batch: the page parses JSON and nothing is HTML-embedded, so base64's plus sign is written
	// as it is (the default encoder escapes it).
	private readonly ArrayBufferWriter<byte> _frames = new(64 * 1024);
	private readonly Utf8JsonWriter _framesWriter;
	// The stall watchdog, created on first use.
	private IDispatcherTimer? _watchdog;
	// The terminal the view shows now (from Reset): a stall resets the page to it again.
	private string _address = "";
	// The pictures the page holds, by key: the thumbnail sent (compared by reference) and the grid sent last. Cleared by hello.
	private readonly Dictionary<string, (string Thumbnail, int Cols, int Rows)> _shown = [];
	// The page's #terminal border box in CSS px as last reported (ready, grid); null until one. Settled: it equals the view's own size.
	private (double Width, double Height)? _box;
	private bool _settledNow;
	private TaskCompletionSource? _settling;
	// Font messages the page has not answered yet: each is answered by one grid marked "font" (the grid fit() gives at that font, sent
	// even when unchanged). The view is not settled until all are, so an attach never takes the grid of the font before.
	private int _fontAnswers;
	private long _generation;
	private string _lease = "";
	private bool _leaseActive;
	private bool _freshResetPending;
	private bool _awaitingPaint;
	private bool _released;
	private bool _documentFailed;
	private bool _documentReady;
	internal event Action? Unusable;
	internal Func<bool>? HostReady { get; set; }
	internal void HostLayoutChanged() => CheckSettled();
	internal void ActivateLease() => _leaseActive = true;

	public TerminalView()
	{
		HybridRoot = "wwwroot";
		DefaultFile = "index.html";
		_framesWriter = new Utf8JsonWriter(_frames, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
		SizeChanged += (_, _) => CheckSettled();
		// So the view never flashes before the page draws: hidden until ready, over the page's own ground (the terminal's).
		this.SetAppThemeColor(BackgroundColorProperty, App.Res<Color>("Bg"), App.Res<Color>("BgDark"));
		Opacity = 0;
		// The page follows the app's theme while it is on screen.
		Loaded += View_Loaded;
		Unloaded += View_Unloaded;
		RawMessageReceived += (_, e) =>
		{
			// The platform may raise it off the UI thread (Android's JavaScript bridge); everything here runs on it.
			if (e.Message is { } message) { MainThread.BeginInvokeOnMainThread(() => Receive(message)); }
		};
		HandlerChanging += (_, e) => MenuHandlerChanging(e.OldHandler?.PlatformView);
		HandlerChanged += (_, _) => { if (!_released) { ObservePlatform(); } };
#if ANDROID
		// Use a hardware WebView layer to avoid terminal flicker. Android defaults MinimumFontSize and
		// MinimumLogicalFontSize to 8 px; lower both to 1 px so pinch zoom can render every supported size, as on iOS.
		HandlerChanged += (_, _) =>
		{
			if (Handler?.PlatformView is not Android.Webkit.WebView web) { return; }
			web.SetLayerType(Android.Views.LayerType.Hardware, null);
			if (web.Settings is { } settings)
			{
				settings.MinimumFontSize = 1;
				settings.MinimumLogicalFontSize = 1;
			}
		};
#endif
	}
	partial void ObservePlatform();
	partial void MenuHandlerChanging(object? native);

	// A lost document cannot acknowledge outstanding writes; never reuse its FIFO with a replacement document.
	private void DocumentFailed(string reason)
	{
		if (_released || _documentFailed) { return; }
		System.Diagnostics.Debug.WriteLine($"[TerminalView] document unusable: {reason}");
		_documentFailed = true;
		IsReady = _leaseActive = false;
		Opacity = 0;
		Held = true;
		while (_pending.TryDequeue(out var frame)) { ArrayPool<byte>.Shared.Return(frame.Data); }
		_inFlight.Clear();
		_settling?.TrySetCanceled();
		Unusable?.Invoke();
	}

	// Fonts loaded, the theme in and the first fit() done; messages sent before it (the theme aside) are dropped by the page, so nothing
	// else is sent until then.
	public bool IsReady { get; private set; }

	// The grid the view shows now (from ready and grid).
	public int Cols { get; private set; }

	public int Rows { get; private set; }

	// The font size in pt, from the last font message (pinch end) or SetFont; 0 until either.
	public double FontSize { get; private set; }

	// The line height (xterm.js's, in font sizes) from the last SetFont; 0 until one.
	public double LineHeight { get; private set; }

	public event Action? Ready;

	// fit() gave a different grid (the page fits again on its own resize: keyboard up or down, rotation, the strip collapsing).
	public event Action<int, int>? GridChanged;

	// The pinch ended at a new font size.
	public event Action<double>? FontChanged;

	// A pinch step (ended false; the font size in pt) and the fingers lifting (ended true).
	public event Action<bool, int>? Pinched;

	// The long-press selection (terminal-web selection.ts): a hold began it (a haptic tick), its edit menu's Copy (the selected text),
	// Paste and Read from here (the page takes the sentences and starts a streaming read: ReadStarted).
	public event Action? SelectionStarted;
	public event Action<string>? Copied;
	public event Action? PasteAsked;

	// The selection's edit menu as the page last reported it (selection.ts MenuState): what it offers and where it is, in the page's
	// CSS pixels; null while it is hidden. Shown by the platform's own edit menu (TerminalView.iOS.cs: UIEditMenuInteraction;
	// TerminalView.Android.cs: a floating ActionMode).
	private sealed record EditMenu(int Id, bool Text, bool Readable, string Image, double X, double Y, double Width, double Height);

	private EditMenu? _menu;

	// One action order on both platforms; unavailable operations are omitted.
	private List<(string Action, string Title)> MenuItems()
	{
		List<(string, string)> items = [];
		if (_menu is null) { return items; }
		(bool text, bool image) = ClipboardImage.Kinds();
		if (_menu.Text) { items.Add(("read", i18n.Translate("Read"))); }
		if (_menu.Text && _menu.Readable) { items.Add(("readfrom", i18n.Translate("Read from here"))); }
		if (_menu.Text) { items.Add(("copy", i18n.Translate("Copy"))); }
		if (text || image) { items.Add(("paste", i18n.Translate("Paste"))); }
		items.Add(("all", i18n.Translate("Select all")));
		if (_menu.Image == "open") { items.Add(("image", i18n.Translate("Open image"))); }
		else if (_menu.Image == "reload") { items.Add(("reloadimage", i18n.Translate("Reload image"))); }
		return items;
	}

	// An item of the edit menu was chosen: the page does it (selection.ts act).
	private void ActOnMenu(string action, string origin, int id)
	{
		if (_leaseActive && origin == _lease && _menu is not null && id == _menu.Id)
		{
			Post("selaction", writer =>
			{
				writer.WriteString("action", action);
				writer.WriteNumber("id", id);
				writer.WriteString("lease", origin);
			});
		}
	}

	// Shows the platform's edit menu for _menu (again, when it moved or changed), or takes it away.
	partial void PresentMenu();
	partial void DismissMenu();

	// A lone one-finger tap (no second tap followed it) on no image, read mark or jump-to-the-end row.
	public event Action? Tapped;

	// A streaming read begins with these sentences (seq 0..n-1); the page then appends (ReadAppended), ends (ReadEnded) or loses its
	// place (ReadLost). The append, end and lost events carry only the reading the view presents now.
	public event Action<List<string>>? ReadStarted;
	public event Action<int, List<string>>? ReadAppended;
	public event Action<bool>? ReadEnded;
	public event Action? ReadLost;

	// Reader-owned taps carry the reading that owned the touch, never the current read relabeled at receipt.
	public event Action<int>? MarkTapped;
	public event Action<int>? ReadStopAsked;
	private int _readingSequence;
	public int ReadingId { get; private set; }

	// A one-finger tap on a row holding an agent's jump-to-the-end button (Claude Code's "Jump to bottom" / "N new messages", pi's
	// "Jump to latest message", Codex's "Back to bottom"): the cell (column, row) at the center of that text, to click.
	public event Action<int, int>? RowClicked;

	// A one-finger horizontal swipe; +1 = next pane, -1 = previous.
	public event Action<int>? SwipedPane;

	// A one-finger drag or a page key on the alternate screen of a program with mouse reporting on, where there is no scrollback:
	// wheel steps for the program at a cell; direction "up" (older lines) or "down".
	public event Action<string, int, int, int>? Wheeled;

	// A drag went on past the bottom of the view (the keyboard goes down).
	public event Action? Dismissed;

	// Find in terminal's answer to Find and FindStep: the current match (1-based; 0: none) and how many there are.
	public event Action<string, int, int>? Found;

	// Find performs a case-insensitive literal search across retained lines, selects the newest match,
	// and marks every match in Work. Empty text removes the marks. FindStep wraps: -1 selects an older match,
	// +1 a newer one. FindClose clears search and marks without changing reading marks or selection.
	public void Find(string text) => Post("find", writer => writer.WriteString("text", text));

	public void FindStep(int direction) => Post("findstep", writer => writer.WriteNumber("direction", direction < 0 ? -1 : 1));

	public void FindClose() => Post("findclose", _ => { });

	// A batch's credit did not come back in time; the view was reset and the stream must be attached again.
	public event Action? Stalled;

	// xterm.js wrote this many stream bytes (a batch's credit).
	public event Action<int>? Written;

	// The program's input modes changed (from the stream, as xterm.js tracks them).
	public event Action? ModesChanged;

	// The view scrolled away from the newest line, or back to it.
	public event Action? ScrolledChanged;

	// The cell size in device pixels changed (the font, the first fit): images placed without their own size take it.
	public event Action<int, int>? CellChanged;

	// The last cell size reported (device pixels); (0, 0) until the page reports one.
	public (int Width, int Height) Cell { get; private set; }

	// A tap on an image drawn in the terminal: its id.
	public event Action<long>? ImageTapped;

	// A tap on an image that never reached this phone, when the program sends its images again on a redraw (SetImageReloadable): the
	// program's full redraw is asked for.
	public event Action? ImageReloadRequested;

	// What the page was last told about the program's redraw sending its images again; null after a reset (told again).
	private bool? _imageReloadable;

	// Whether a missing image can be loaded again by asking the program for a redraw: the page offers it only then. Sent on a change.
	internal void SetImageReloadable(bool on)
	{
		if (!IsReady || _imageReloadable == on) { return; }
		_imageReloadable = on;
		Post("imagereloadable", writer => writer.WriteBoolean("on", on));
	}

	// The picture for an image (its key, id, thumbnail as a data URL, pixel size and placeholder grid, 0 x 0 when it has none): every
	// placement and every placeholder cell of that id on its terminal shows it. Sent once per page and thumbnail: when the page holds
	// the key with this same thumbnail, only a changed grid goes (imagegrid). Before the page is ready it drops what it gets, so
	// nothing is counted as held then.
	internal void ShowImage(PaneImage image)
	{
		string key = image.Key;
		if (_shown.TryGetValue(key, out (string Thumbnail, int Cols, int Rows) shown) && ReferenceEquals(shown.Thumbnail, image.Thumbnail))
		{
			if ((shown.Cols, shown.Rows) == (image.Cols, image.Rows)) { return; }
			Post("imagegrid", writer =>
			{
				writer.WriteString("key", key);
				writer.WriteNumber("cols", image.Cols);
				writer.WriteNumber("rows", image.Rows);
			});
		}
		else
		{
			Post("image", writer =>
			{
				writer.WriteString("key", key);
				writer.WriteNumber("id", image.Id);
				writer.WriteString("url", image.Thumbnail);
				writer.WriteNumber("width", image.Width);
				writer.WriteNumber("height", image.Height);
				writer.WriteNumber("cols", image.Cols);
				writer.WriteNumber("rows", image.Rows);
			});
		}
		if (IsReady) { _shown[key] = (image.Thumbnail, image.Cols, image.Rows); }
	}

	// The kept images let an image go (PaneImages.Dropped): the page lets its picture go too.
	internal void DropImage(string key)
	{
		if (_shown.Remove(key)) { Post("imagedrop", writer => writer.WriteString("key", key)); }
	}

	// The page lays out at the view's own size: its last reported #terminal border box (CSS px, = dp) equals Width and Height within 1.
	public event Action? Settled;

	// Complete at once when the view is settled, else when it settles (or ct is cancelled).
	public Task WaitSettledAsync(CancellationToken ct)
	{
		if (_released || _documentFailed) { return Task.FromCanceled(new CancellationToken(true)); }
		if (IsSettled) { return Task.CompletedTask; }
		_settling ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		return _settling.Task.WaitAsync(ct);
	}

	private bool IsSettled => !_released && !_documentFailed && HostReady?.Invoke() != false && _fontAnswers == 0 && _box is { } box && Math.Abs(box.Width - Width) <= 1 && Math.Abs(box.Height - Height) <= 1;

	// After a size report or a size change: Settled once it becomes settled, and the waits end.
	private void CheckSettled()
	{
		bool settled = IsSettled;
		if (settled == _settledNow) { return; }
		_settledNow = settled;
		if (!settled) { return; }
		_settling?.TrySetResult();
		_settling = null;
		Settled?.Invoke();
	}

	// Take the border box reported by ready/grid. Older pages omit it, so settlement waits reach their timeout.
	private void TakeBox(JsonElement root)
	{
		if (root.TryGetProperty("width", out JsonElement width) && root.TryGetProperty("height", out JsonElement height))
		{
			_box = (width.GetDouble(), height.GetDouble());
		}
		CheckSettled();
	}

	// An image id's bytes on their way (loading) or not any more: its placeholder cells show an activity indicator until its picture
	// comes, else a missing image.
	internal void ShowImageLoading(long id, bool loading) => Post("imageloading", writer =>
	{
		writer.WriteNumber("id", id);
		writer.WriteBoolean("loading", loading);
	});

	internal TerminalModes Modes { get; private set; } = TerminalModes.Default;

	// The view shows older lines than the newest ones (its own scrollback).
	public bool ScrolledBack { get; private set; }

	// Queues stream output (one or more frames: a pooled array this view now owns, its length) with its stream byte count, credited
	// back once written. The array goes back to the pool once written into a batch.
	public void Feed(byte[] data, int length, int bytes)
	{
		if (_released || _documentFailed) { ArrayPool<byte>.Shared.Return(data); return; }
		_pending.Enqueue((data, length, bytes));
		Pump();
	}

	// A new stream of the terminal at address starts: the queued frames are dropped, the batches still in flight belong to the old one
	// (their credit is not raised) and the page empties its screen, scrollback and modes when the new stream's first bytes come,
	// writing them in the same turn (the old screen stays drawn until then). The page keeps its pictures: it looks them up by address.
	public void Reset(string address, long? lease = null)
	{
		if (_released) { return; }
		_address = address;
		_generation++;
		if (lease is { } next)
		{
			ReadingId = 0;
			_lease = next.ToString(CultureInfo.InvariantCulture);
			_leaseActive = false;
			_awaitingPaint = true;
			Opacity = 0;
			_menu = null;
			DismissMenu();
			_freshResetPending = true;
		}
		while (_pending.TryDequeue(out (byte[] Data, int Length, int Bytes) frame)) { ArrayPool<byte>.Shared.Return(frame.Data); }
		foreach (Batch batch in _inFlight) { batch.Bytes = 0; }
		Modes = TerminalModes.Default;
		_imageReloadable = null;
		ScrolledBack = false;
		ScrolledChanged?.Invoke();
		if (IsReady) { SendReset(_freshResetPending); _freshResetPending = false; }
	}

	private void SendReset(bool fresh) => Post("reset", writer =>
	{
		writer.WriteString("address", _address);
		if (fresh) { writer.WriteBoolean("fresh", true); writer.WriteString("lease", _lease); }
	});

	// The page key's scroll, a screen up or down: the view's scrollback, or wheel steps for a program without one (Wheeled).
	public void ScrollPage(string direction) => Post("page", writer => writer.WriteString("direction", direction));

	// Back to the newest line.
	public void ScrollToBottom() => Post("bottom", _ => { });

	// The pane's font size (remembered, or the layout's default) and the layout's line height. Not settled until the page answers it
	// with its grid at that font (a page that is not ready drops it, so nothing is awaited then).
	public void SetFont(double size, double lineHeight)
	{
		FontSize = size;
		LineHeight = lineHeight;
		Post("font", writer =>
		{
			writer.WriteNumber("size", size);
			writer.WriteNumber("lineHeight", lineHeight);
		});
		if (!IsReady) { return; }
		_fontAnswers++;
		CheckSettled();
	}

	// Send the current theme on hello, theme changes, and Pane appearance (including language changes).
	// Use Bg, ConInk, and Acc for background, text, and cursor; Acc at 0.30 for long-press/selection and 0.17
	// for light reading marks. Find uses Work: current match at 0.40 with a 1.5 px inner edge, other matches at 0.22.
	// ANSI green/blue use Ok/Acc; bright colors match normal colors except bright black (AnsiDim).
	// Also send the Work loading indicator and translated missing-image labels.
	public void ApplyTheme()
	{
		if (_released) { return; }
		string theme = Application.Current?.RequestedTheme == AppTheme.Dark ? "Dark" : "";
		string Hex(string key) => App.Res<Color>(key + theme).ToRgbaHex();
		App.Res<Color>("Acc" + theme).ToRgba(out byte red, out byte green, out byte blue, out _);
		string mark = $"rgba({red},{green},{blue},0.30)";
		App.Res<Color>("Work" + theme).ToRgba(out byte workRed, out byte workGreen, out byte workBlue, out _);
		Post("theme", writer =>
		{
			writer.WriteString("ground", Hex("Bg"));
			writer.WriteString("ink", Hex("ConInk"));
			writer.WriteString("cursor", Hex("Acc"));
			writer.WriteString("mark", mark);
			writer.WriteString("lightMark", $"rgba({red},{green},{blue},0.17)");
			writer.WriteString("selection", mark);
			writer.WriteString("work", Hex("Work"));
			writer.WriteString("findMark", $"rgba({workRed},{workGreen},{workBlue},0.40)");
			writer.WriteString("findLightMark", $"rgba({workRed},{workGreen},{workBlue},0.22)");
			writer.WriteString("missingText", i18n.Translate("Image not received"));
			writer.WriteString("reloadText", i18n.Translate("Tap to load it again"));
			writer.WriteStartObject("ansi");
			foreach ((string name, string bright, string key) in new[] { ("black", "brightBlack", "AnsiBlack"), ("red", "brightRed", "AnsiRed"), ("green", "brightGreen", "Ok"),
				("yellow", "brightYellow", "AnsiYellow"), ("blue", "brightBlue", "Acc"), ("magenta", "brightMagenta", "AnsiMagenta"), ("cyan", "brightCyan", "AnsiCyan"),
				("white", "brightWhite", "AnsiWhite") })
			{
				writer.WriteString(name, Hex(key));
				writer.WriteString(bright, Hex(name == "black" ? "AnsiDim" : key));
			}
			writer.WriteEndObject();
		});
	}

	private void Theme_Changed(object? sender, AppThemeChangedEventArgs e) => ApplyTheme();
	private void View_Loaded(object? sender, EventArgs e)
	{
		if (_released || Application.Current is not { } app) { return; }
		app.RequestedThemeChanged -= Theme_Changed;
		app.RequestedThemeChanged += Theme_Changed;
	}
	private void View_Unloaded(object? sender, EventArgs e)
	{
		if (Application.Current is { } app) { app.RequestedThemeChanged -= Theme_Changed; }
	}

	// The read is over: the page drops its chain and marks.
	public void ClearMark() => Post("clearmark", _ => { });

	// The read is presented to the page (a new reading id), or detached from it (false: the pane was left or the app went to the
	// background; the page keeps its chain, draws nothing and takes nothing until a new id comes).
	public void SetReading(bool active)
	{
		ReadingId = active ? ++_readingSequence : 0;
		Post("reading", writer =>
		{
			writer.WriteBoolean("active", active);
			writer.WriteNumber("id", ReadingId);
		});
	}

	// The sentence being read (its sequence number) and its state: "waiting", "playing", "paused" or "listening".
	public void ShowReadState(int seq, string state)
	{
		Post("readstate", writer =>
		{
			writer.WriteNumber("id", ReadingId);
			writer.WriteNumber("seq", seq);
			writer.WriteString("state", state);
		});
	}

	// While the app is in the background (the Pane screen sets it): no batch is posted and the batches in flight wait without a
	// deadline, since the OS may not run the page then and a missing credit is not a stall. Frames stay queued, the core waits for
	// credit, and everything goes on when the app comes back: nothing is reset or attached again.
	public bool Held
	{
		get => _held;
		set
		{
			if (_held == value) { return; }
			_held = value;
			if (!value)
			{
				// Each batch in flight gets CreditTimeout again from now.
				long now = Stopwatch.GetTimestamp();
				foreach (Batch batch in _inFlight) { batch.Posted = now; }
			}
			ArmWatchdog();
			if (!value) { Pump(); }
		}
	}

	private bool _held;

	// Up to MaxBatchesInFlight batches in flight; each takes the queued frames up to the frame and size caps, written as base64 into
	// the reused frames message, each array returned to the pool once written.
	private void Pump()
	{
		bool posted = false;
		while (IsReady && !_held && _inFlight.Count < MaxBatchesInFlight && _pending.Count > 0)
		{
			if (Handler?.PlatformView is null) { DocumentFailed("The frame sender has no native handler."); break; }
			_frames.ResetWrittenCount();
#if IOS
			_frames.Write("window.remoteReceiveFrames("u8);
#endif
			_framesWriter.Reset(_frames);
			_framesWriter.WriteStartObject();
			_framesWriter.WriteString("type", "frames");
			_framesWriter.WriteStartArray("data");
			int count = 0;
			int chars = 0;
			int bytes = 0;
			while (_pending.Count > 0 && count < MaxBatchFrames)
			{
				(byte[] data, int length, int stream) = _pending.Peek();
				int size = (length + 2) / 3 * 4;
				if (count > 0 && chars + size > MaxBatchChars) { break; }
				_pending.Dequeue();
				_framesWriter.WriteBase64StringValue(data.AsSpan(0, length));
				ArrayPool<byte>.Shared.Return(data);
				count++;
				chars += size;
				bytes += stream;
			}
			_framesWriter.WriteEndArray();
			_framesWriter.WriteEndObject();
			_framesWriter.Flush();
			_inFlight.Enqueue(new Batch(Stopwatch.GetTimestamp(), bytes, _generation));
#if IOS
			_frames.Write(");"u8);
			SendFrames(_frames.WrittenMemory);
#else
			try { SendRawMessage(Encoding.UTF8.GetString(_frames.WrittenSpan)); }
			catch (Exception ex) { DocumentFailed(ex.Message); }
#endif
			posted = true;
		}
		if (posted) { ArmWatchdog(); }
	}
#if IOS
	private partial void SendFrames(ReadOnlyMemory<byte> script);
#endif

	// The watchdog runs for the oldest batch in flight, due CreditTimeout after it was posted; stopped while none is in flight or the
	// view is held.
	private void ArmWatchdog()
	{
		if (_held || !_inFlight.TryPeek(out Batch? oldest))
		{
			_watchdog?.Stop();
			return;
		}
		if (_watchdog is null)
		{
			_watchdog = Dispatcher.CreateTimer();
			_watchdog.IsRepeating = false;
			_watchdog.Tick += Watchdog_Elapsed;
		}
		TimeSpan due = CreditTimeout - Stopwatch.GetElapsedTime(oldest.Posted);
		_watchdog.Stop();
		_watchdog.Interval = due > TimeSpan.FromMilliseconds(1) ? due : TimeSpan.FromMilliseconds(1);
		_watchdog.Start();
	}

	// The oldest batch's credit is overdue: a stall. Else (a credit came back meanwhile) armed again for the batch now oldest.
	private void Watchdog_Tick()
	{
		if (!_held && _inFlight.TryPeek(out Batch? oldest) && Stopwatch.GetElapsedTime(oldest.Posted) >= CreditTimeout) { Stall(); }
		else { ArmWatchdog(); }
	}
	private void Watchdog_Elapsed(object? sender, EventArgs e) => Watchdog_Tick();

	// Manual disconnection belongs to the owning slot, never a disposable Shell wrapper.
	internal void Release()
	{
		if (_released) { return; }
		_released = true;
		_leaseActive = false;
		IsReady = false;
		ReadingId = 0;
		_held = true;
		Opacity = 0;
		Loaded -= View_Loaded;
		Unloaded -= View_Unloaded;
		View_Unloaded(this, EventArgs.Empty);
		if (_watchdog is { } timer) { timer.Stop(); timer.Tick -= Watchdog_Elapsed; _watchdog = null; }
		_settling?.TrySetCanceled();
		_settling = null;
		while (_pending.TryDequeue(out var frame)) { ArrayPool<byte>.Shared.Return(frame.Data); }
		_inFlight.Clear();
		_shown.Clear();
		_menu = null;
		_box = null;
		_fontAnswers = 0;
		_freshResetPending = _awaitingPaint = false;
		_address = _lease = "";
		HostReady = null;
		_framesWriter.Dispose();
		_frames.Clear();
		IElementHandler? handler = Handler;
		object? native = handler?.PlatformView;
		try { handler?.DisconnectHandler(); }
		finally
		{
			Handler = null;
			try { ReleasePlatform(native); }
			finally
			{
				try { (native as IDisposable)?.Dispose(); }
				finally { (handler as IDisposable)?.Dispose(); }
			}
		}
	}
	partial void ReleasePlatform(object? native);

	// Reset the page and output queue on a stall, then reattach so the first paint restores the full screen.
	private void Stall()
	{
		// A stalled page may never answer these: they leave the queue now.
		_inFlight.Clear();
		ArmWatchdog();
		Reset(_address);
		Stalled?.Invoke();
	}

	// One host message: {"type": type, ...fields}.
	private void Post(string type, Action<Utf8JsonWriter> fields)
	{
		if (_released || _documentFailed) { return; }
		ArrayBufferWriter<byte> buffer = new();
		using (Utf8JsonWriter writer = new(buffer))
		{
			writer.WriteStartObject();
			writer.WriteString("type", type);
			fields(writer);
			writer.WriteEndObject();
		}
		SendRawMessage(Encoding.UTF8.GetString(buffer.WrittenSpan));
	}

	// Whether a page message names the reading presented now.
	private bool ReadingMatches(JsonElement root)
		=> root.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int value) && value == ReadingId;

	// Receive page messages, including "hello", "pinch", "dismiss", "tap", "readstart", "readadd", "readend",
	// "readlost", "marktap", "readstop", "rowclick", "wheel", "modes", "scrolled", "cell", "imagetap",
	// "imagereload", "selmenu", and "found", alongside lifecycle, grid, and credit messages.
	private void Receive(string message)
	{
		if (_released || _documentFailed) { return; }
		using JsonDocument document = JsonDocument.Parse(message);
		JsonElement root = document.RootElement;
		string? type = root.GetProperty("type").GetString();
		// Validate the emitted origin here, after UI dispatch; a generation captured at native receipt is too late.
		if (type is not ("hello" or "ready" or "credit" or "grid" or "cell")
			&& (!IsReady || _freshResetPending || !_leaseActive || _lease.Length == 0
				|| !root.TryGetProperty("lease", out JsonElement origin) || origin.ValueKind != JsonValueKind.String || origin.GetString() != _lease)) { return; }
		switch (type)
		{
			case "hello":
				if (_documentReady) { DocumentFailed("The retained document reloaded."); return; }
				// A new page: it holds no picture, has reported no size yet and is not ready until it says so.
				IsReady = false;
				_shown.Clear();
				_box = null;
				_fontAnswers = 0;
				_freshResetPending = _lease.Length != 0;
				CheckSettled();
				ApplyTheme();
				break;
			case "ready":
				Cols = root.GetProperty("cols").GetInt32();
				Rows = root.GetProperty("rows").GetInt32();
				TakeBox(root);
				IsReady = true;
				_documentReady = true;
				if (_freshResetPending) { SendReset(true); _freshResetPending = false; }
				Post("reading", writer => { writer.WriteBoolean("active", ReadingId != 0); writer.WriteNumber("id", ReadingId); });
				Opacity = _awaitingPaint ? 0 : 1;
				Ready?.Invoke();
				Pump();
				break;
			case "credit":
				// The credited batches are the oldest in flight; their bytes are credited and the watchdog moves to the next one.
				for (int credits = root.GetProperty("batches").GetInt32(); credits > 0 && _inFlight.TryDequeue(out Batch? batch); credits--)
				{
					if (_leaseActive && batch.Generation == _generation && _awaitingPaint) { _awaitingPaint = false; Opacity = 1; }
					if (batch.Bytes > 0) { Written?.Invoke(batch.Bytes); }
				}
				ArmWatchdog();
				Pump();
				break;
			case "modes":
				Modes = new TerminalModes(root.GetProperty("cursor").GetBoolean(), root.GetProperty("paste").GetBoolean(), root.GetProperty("mouse").GetString() ?? "none",
					root.GetProperty("sgr").GetBoolean(), root.GetProperty("kitty").GetInt32());
				ModesChanged?.Invoke();
				break;
			case "scrolled":
				ScrolledBack = root.GetProperty("back").GetBoolean();
				Debug.WriteLine($"[A07] t={Stopwatch.GetTimestamp()} scrolled address={_address} lease={_lease} back={ScrolledBack}");
#if DEBUG
				Post("diag", _ => { });
#endif
				ScrolledChanged?.Invoke();
				break;
#if DEBUG
			case "diagstate":
				Debug.WriteLine($"[A07] t={Stopwatch.GetTimestamp()} diagstate address={_address} lease={_lease} state={root.GetRawText()}");
				break;
#endif
			case "cell":
				Cell = (root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32());
				CellChanged?.Invoke(Cell.Width, Cell.Height);
				break;
			case "imagetap":
				ImageTapped?.Invoke(root.GetProperty("id").GetInt64());
				break;
			case "imagereload":
				ImageReloadRequested?.Invoke();
				break;
			case "grid":
				Cols = root.GetProperty("cols").GetInt32();
				Rows = root.GetProperty("rows").GetInt32();
				if (_fontAnswers > 0 && root.TryGetProperty("font", out JsonElement font) && font.GetBoolean()) { _fontAnswers--; }
				TakeBox(root);
				GridChanged?.Invoke(Cols, Rows);
				break;
			case "font":
				FontSize = root.GetProperty("size").GetDouble();
				FontChanged?.Invoke(FontSize);
				break;
			case "pinch":
				Pinched?.Invoke(root.GetProperty("phase").GetString() == "end", root.GetProperty("size").GetInt32());
				break;
			case "selectstart":
				SelectionStarted?.Invoke();
				break;
			case "copy":
				Copied?.Invoke(root.GetProperty("text").GetString() ?? "");
				break;
			case "paste":
				PasteAsked?.Invoke();
				break;
			case "selmenu":
				JsonElement menu = root.GetProperty("menu");
				_menu = menu.ValueKind == JsonValueKind.Null ? null : new EditMenu(menu.GetProperty("id").GetInt32(), menu.GetProperty("text").GetBoolean(), menu.GetProperty("readable").GetBoolean(), menu.GetProperty("image").GetString() ?? "none",
					menu.GetProperty("x").GetDouble(), menu.GetProperty("y").GetDouble(), menu.GetProperty("width").GetDouble(), menu.GetProperty("height").GetDouble());
				if (_menu is null) { DismissMenu(); }
				else { PresentMenu(); }
				break;
			case "tap":
				Tapped?.Invoke();
				break;
			case "readstart":
				_menu = null;
				DismissMenu();
				ReadStarted?.Invoke([.. root.GetProperty("texts").EnumerateArray().Select(text => text.GetString() ?? "")]);
				break;
			case "readadd":
				if (ReadingId != 0 && ReadingMatches(root) && root.TryGetProperty("seq", out JsonElement seq) && seq.TryGetInt32(out int first))
				{
					ReadAppended?.Invoke(first, [.. root.GetProperty("texts").EnumerateArray().Select(text => text.GetString() ?? "")]);
				}
				break;
			case "readend":
				if (ReadingId != 0 && ReadingMatches(root))
				{
					ReadEnded?.Invoke(root.TryGetProperty("truncated", out JsonElement truncated) && truncated.ValueKind == JsonValueKind.True);
				}
				break;
			case "readlost":
				if (ReadingId != 0 && ReadingMatches(root)) { ReadLost?.Invoke(); }
				break;
			case "marktap":
			case "readstop":
				if (ReadingId != 0 && root.TryGetProperty("id", out JsonElement tapId) && tapId.ValueKind == JsonValueKind.Number
					&& tapId.TryGetInt32(out int reading) && reading == ReadingId)
				{
					if (type == "marktap") { MarkTapped?.Invoke(reading); }
					else { ReadStopAsked?.Invoke(reading); }
				}
				break;
			case "rowclick":
				if (ReadingId != 0 || Modes.Mouse == "none"
					|| !root.TryGetProperty("mouse", out JsonElement mouse) || mouse.ValueKind != JsonValueKind.String || mouse.GetString() != Modes.Mouse
					|| !root.TryGetProperty("sgr", out JsonElement sgr) || sgr.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || sgr.GetBoolean() != Modes.Sgr
					|| !root.TryGetProperty("cols", out JsonElement cols) || cols.ValueKind != JsonValueKind.Number || !cols.TryGetInt32(out int columns) || columns != Cols
					|| !root.TryGetProperty("rows", out JsonElement rows) || rows.ValueKind != JsonValueKind.Number || !rows.TryGetInt32(out int lines) || lines != Rows
					|| !root.TryGetProperty("column", out JsonElement column) || column.ValueKind != JsonValueKind.Number || !column.TryGetInt32(out int x) || x < 0 || x >= Cols
					|| !root.TryGetProperty("row", out JsonElement row) || row.ValueKind != JsonValueKind.Number || !row.TryGetInt32(out int y) || y < 0 || y >= Rows) { break; }
				RowClicked?.Invoke(x, y);
				break;
			case "swipe":
				SwipedPane?.Invoke(root.GetProperty("direction").GetInt32());
				break;
			case "wheel":
				Wheeled?.Invoke(root.GetProperty("direction").GetString() ?? "down", root.GetProperty("lines").GetInt32(), root.GetProperty("column").GetInt32(), root.GetProperty("row").GetInt32());
				break;
			case "dismiss":
				Dismissed?.Invoke();
				break;
			case "found":
				Found?.Invoke(root.GetProperty("text").GetString() ?? "", root.GetProperty("index").GetInt32(), root.GetProperty("total").GetInt32());
				break;
		}
	}
}
