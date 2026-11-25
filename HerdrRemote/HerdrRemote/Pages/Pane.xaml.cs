using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using HerdrRemote.Remote;
using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Terminal page for one herdr-core address, pushed from the list or a notification using the "pane" query.
// It owns the header, terminal, tray, native keyboard responder, and stream while visible. Claim the phone's
// grid on attach, local grid changes, and user input (ClaimSize), in either orientation. Never claim in
// response to another viewer's size event: competing viewers can resize each other indefinitely
// (observed: 2029 events in 20 seconds). Encode typing, keys, pastes, and taps for the view's input modes.
public partial class Pane : ContentPage, IQueryAttributable
{
	private AppShell? _owner;
	private long _lease;
	private bool _leaseActive;
	private bool _released;
	private Window? _visibleWindow;
	private Task _parking = Task.CompletedTask;
	internal bool IsParking => !_parking.IsCompleted;
	internal Task WaitForParkingAsync() => _parking;
	private readonly record struct LeaseOrigin(long Lease, string Address, string Machine);
	private LeaseOrigin Origin => new(_lease, _paneId, HerdrSession.MachineKey);
	private bool Current(LeaseOrigin origin) => !_released && _leaseActive && _onScreen && origin.Lease == _lease
		&& origin.Address == _paneId && origin.Machine == HerdrSession.MachineKey
		&& _mac == (RemoteSettings.User, RemoteSettings.Host, RemoteSettings.Port);
	private Func<ContentPage, Layout, bool> InsertionGuard(LeaseOrigin origin, Func<bool>? current = null) =>
		(page, layout) => Current(origin) && current?.Invoke() != false && ReferenceEquals(page, this) && ReferenceEquals(layout, Root);
	internal Func<ContentPage, Layout, bool> CaptureInsertionGuard() => InsertionGuard(Origin);
	private Task ShowMessageAsync(LeaseOrigin origin, string title, string message, Func<bool>? current = null) =>
		PopupsHandle.PopDisplayAsync(i18n.Translate(title), message, [i18n.Translate("OK")], CanInsert: InsertionGuard(origin, current));
	internal string Address => _paneId;
	internal bool LeaseActive => _leaseActive;
	internal bool IsHostAttached => TerminalHost.IsAttached;
	internal Grid NoticeHost => NoticeSlot;
	internal event Action? HostDetached;
	private string _paneId = "";
	// The Mac this screen was opened on (its pane ids are that Mac's core's).
	private (string User, string Host, int Port) _mac;
	private TerminalStream? _terminal;
	private bool _landscape;
	// At least 600 by 600: show the sidebar, hide header Back, and use a 13.5-point default terminal font.
	private bool _wide;
	// The wide sidebar's list (its rows shorter, the open pane's agent row lit).
	private readonly AgentList _sideList = new() { Compact = true };
	// The header's last room for its lead (the pill and the "+"), 0 before the first.
	private double _leadRoom;
	// The stream ended because its kept terminal lost its program: it is attached again once its live record has a pid (its new
	// shell), checked on every change of the terminals and, for a paced wait, every ReopenTick (TryReopen).
	private bool _waiting;
	// Match herdr-app's reopen polling so a restart before stream end is still detected.
	// Run only during a paced wait while the page is visible and the app is foregrounded.
	private readonly IDispatcherTimer _reopen;
	// The wait is paced by ReopenTick (an end, or an attach that failed as unavailable); any other failed attach waits only for the next
	// change of the terminals.
	private bool _paced;
	private static readonly TimeSpan ReopenTick = TimeSpan.FromMilliseconds(500);
	// Between OnAppearing and OnDisappearing.
	private bool _onScreen;
	// Refresh sidebar status durations every 30 seconds while wide, visible, and foregrounded,
	// matching herdr-app and the Herdr list.
	private readonly IDispatcherTimer _sideRefresh;
	private bool _leaving;
	private bool _keyboardUp;
#if ANDROID
	private Android.Views.View? _imeRoot;
	private Android.Views.ViewTreeObserver? _imeObserver;
	private EventHandler? _imeLayout;
	private long _imeGeneration;
#endif
	// Find in terminal's bar floats below the header (OpenFind, CloseFind).
	private bool _finding;
	// Shared reading session for marked blocks, text from a selected position, or visible terminal text.
	private readonly ReadingSession _reader = App.Reading;
	private CancellationTokenSource? _acquisition;
	private long _captureGeneration, _presentationGeneration;
	private Action<int>? _readingPainted;
	private bool _readingVisible;
	// The page holds a read's chain and marks from this screen (cleared once the read is over).
	private bool _markShown;
	private string ReadingMachine => $"{_mac.Item1}@{_mac.Item2}:{_mac.Item3}";
	private bool OwnReading => _reader.Matches(ReadingMachine, _paneId);
	// The block of the last Copy or read menu pick, read by the next Read.
	private string? _selection;
	// The image viewer while it is open over this screen.
	private ImageViewer? _viewer;
	// The image work of this screen (a pasted image's upload); cancelled when the screen goes.
	private CancellationTokenSource _work = new();
	// How long the line must stay down before its strip shows, and the wait for it (ShowLinkStrip).
	private static readonly TimeSpan LinkStripDelay = TimeSpan.FromSeconds(1);
	// How long a picked file waits for the session to be connected again (ConnectedAsync), and then for the pane's stream to paint.
	private static readonly TimeSpan AttachWait = TimeSpan.FromSeconds(15);
	private CancellationTokenSource? _linkDelay;
	public Pane()
	{
		InitializeComponent();
		ReadingBar reading = Header.TakePaneReading();
		reading.SetAppThemeColor(BackgroundColorProperty, App.Res<Color>("Bg"), App.Res<Color>("BgDark"));
		OverlayRows.Add(reading, 0, 1);
		HandlerProperties.SetDisconnectPolicy(Screen, HandlerDisconnectPolicy.Manual);
		TerminalHost.Changed += Host_Changed;
		Screen.HostReady = () => TerminalHost.IsLayoutReady;
		PaneImages.Dropped += PaneImages_Dropped;
		_reopen = Dispatcher.CreateTimer();
		_reopen.Interval = ReopenTick;
		_reopen.Tick += Reopen_Elapsed;
		_sideRefresh = Dispatcher.CreateTimer();
		_sideRefresh.Interval = TimeSpan.FromSeconds(30);
		_sideRefresh.Tick += Side_Elapsed;
		App.ApplyKeyboardSafeArea(this);
		// Keep Spaces fixed and scroll only Agents; tapping another pane switches to it.
		SideTabs.Active = "Herdr";
		Side.Add(_sideList.Spaces, 0, 1);
		SideScroll.Content = _sideList.Agents;
		_sideList.Opened += address =>
		{
			if (address != _paneId) { SwitchPane(address); }
		};
		_sideList.Searched += () => _ = App.GoToAsync("SearchAll");
		_sideList.Asked += ask => _ = HerdrRemote.Pages.Arrange.AskAsync(ArrangeHere(), ask);
		TerminalLayer.SizeChanged += (_, _) => Draft.FitActions(TerminalLayer.Width, TerminalLayer.Height);
		Screen.Ready += OpenStream;
		Screen.Unusable += Screen_Unusable;
		// The phone's grid is the terminal's.
		Screen.GridChanged += (cols, rows) =>
		{
			if (_terminal is null) { return; }
			_terminal.Resize(cols, rows);
		};
		Screen.FontChanged += size => RemoteSettings.SaveFontSize(_paneId, size);
		Screen.Pinched += Screen_Pinched;
		Screen.SelectionStarted += () =>
		{
			if (HapticFeedback.Default.IsSupported) { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); }
		};
		Screen.Copied += Screen_Copied;
#if IOS
		Screen.NativePaste += Screen_NativePaste;
#else
		Screen.PasteAsked += Screen_PasteAsked;
#endif
		Screen.ReadStarted += Screen_ReadStarted;
		// The page's sentences, taken as the terminal writes them, go to the reader that is presented to it.
		Screen.ReadAppended += (seq, texts) => { if (OwnReading) { _reader.Append(seq, texts); } };
		Screen.ReadEnded += truncated =>
		{
			if (!OwnReading) { return; }
			_reader.End();
			if (truncated)
			{
				long generation = _reader.Generation;
				_ = ShowMessageAsync(Origin, "Read", i18n.Translate("Selection too long: only the first part will be read."), () => _reader.Generation == generation);
			}
		};
		Screen.ReadLost += () => { if (OwnReading) { _reader.PlaceLost(); } };
		Screen.SwipedPane += direction =>
		{
			if (Strip.Neighbour(_paneId, direction) is { } next) { SwitchPane(next); }
		};
		Screen.Tapped += Screen_Tapped;
		Screen.Dismissed += () => _ = HideKeyboardAsync();
		// The watchdog reset the view: the same stream again, whose first paint is the whole screen.
		Screen.Stalled += () => _terminal?.Reopen();
		// xterm.js wrote the bytes: the stream may send that much more.
		Screen.Written += bytes =>
		{
			_terminal?.Credit(bytes);
			// A Find opened by Search all panes on a freshly attached pane starts again when its first output is in the view.
			// The first batch may hold only part of the history, so it runs again once the rest has been written.
			if (_refind is { } query && bytes > 0)
			{
				_refind = null;
				long lease = _lease;
				Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(600), () => { if (_finding && lease == _lease && Finder.Text == query) { Screen.Find(query); } });
			}
		};
		Screen.ScrolledChanged += () => Bar.ScrolledBack = Screen.ScrolledBack;
		Screen.CellChanged += (width, height) => _terminal?.SetCell(width, height);
		Screen.ImageTapped += Screen_ImageTapped;
		// Ctrl+L, the redraw key of full-screen programs: a full redraw sends the program's images again (measured with Claude Code).
		// The page asks for it only on the alternate screen (on a shell Ctrl+L clears the screen).
		Screen.ImageReloadRequested += () => Send([0x0C]);
		// Wheel steps for a program on its alternate screen (no scrollback there): sent only while it has mouse reporting on.
		Screen.Wheeled += (direction, steps, column, row) =>
		{
			if (TerminalInput.Mouse(direction == "up" ? 64 : 65, steps, column, row, Screen.Modes) is { } wheel) { Send(wheel); }
		};
		Strip.PaneTapped += paneId =>
		{
			if (paneId != _paneId) { SwitchPane(paneId); }
		};
		Strip.Tapped += Strip_Tapped;
		Header.LeadRoomChanged += room =>
		{
			_leadRoom = room;
			FitPill();
		};
		Bar.KeysSent += keys =>
		{
			Input.ResetComposition();
			foreach (string key in keys) { Send(TerminalInput.Key(key, Screen.Modes)); }
		};
		Bar.TextSent += text => { Input.ResetComposition(); Send(TerminalInput.Text(text, false, Screen.Modes)); };
		Bar.SequenceSent += items => { Input.ResetComposition(); Send(Shortcuts.Encode(items, Screen.Modes)); };
		Bar.DPadTapped += () =>
		{
			Pad.IsVisible = !Pad.IsVisible;
			Bar.DPadShown = Pad.IsVisible;
			if (Pad.IsVisible) { Pad.Place(); }
		};
		// The pad's keys take the key bar's Ctrl / Alt / Shift like its own keys; the broom is already Ctrl+C and goes as it is.
		Pad.KeySent += key =>
		{
			Input.ResetComposition();
			Send(TerminalInput.Key(key == "ctrl+c" ? key : Bar.Modify(key) ?? key, Screen.Modes));
		};
		Pad.SequenceSent += items => { Input.ResetComposition(); Send(Shortcuts.Encode(items, Screen.Modes)); };
		Bar.PageScrolled += Screen.ScrollPage;
		Bar.BottomTapped += Screen.ScrollToBottom;
		Bar.AttachTapped += Bar_AttachTapped;
		Screen.MarkTapped += id => { if (OwnReading && id == Screen.ReadingId) { _reader.TogglePause(); } };
		Screen.ReadStopAsked += id => { if (OwnReading && id == Screen.ReadingId) { _reader.Stop(); } };
		// The reader owns terminal taps while active, including clicks queued before its start reached the view.
		Screen.RowClicked += (column, row) =>
		{
			if (OwnReading) { return; }
			if (TerminalInput.Mouse(0, 1, column, row, Screen.Modes) is { } click) { Input.ResetComposition(); Send(click); }
		};
		Bar.KeyboardTapped += Bar_KeyboardTapped;
		InitDictation();
		Finder.Searched += Screen.Find;
		Finder.Stepped += Screen.FindStep;
		Finder.Closed += CloseFind;
		Screen.Found += Finder.ShowFound;
#if ANDROID
		Root.HandlerChanging += (_, _) => UnbindIme();
		Root.HandlerChanged += (_, _) => BindIme();
		Root.Loaded += (_, _) => BindIme();
		Root.Unloaded += (_, _) => UnbindIme();
#endif
		Input.TextReceived += Input_TextReceived;
		Input.KeyReceived += key => Send(TerminalInput.Key(Bar.Modify(key) ?? key, Screen.Modes));
#if IOS
		Input.ActiveChanged += active =>
		{
			_keyboardUp = active;
			ApplyLayout();
		};
		Input.PasteRequested += () =>
		{
			LeaseOrigin origin = Origin;
			var payload = TerminalView.ReadPasteboard();
			_ = PasteCapturedAsync(origin, () => Current(origin), payload.Kinds, payload.Text, payload.Image, payload.Error, payload.Rejected);
		};
#else
		Input.CorrectionBackspaceRequested += () => Send(TerminalInput.Key("backspace", Screen.Modes));
		Input.PasteRequested += () => _ = PasteAsync();
#endif
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (!query.TryGetValue("pane", out object? value) || value is not string paneId) { return; }
		SwitchPane(paneId);
	}

	internal void BindOwner(AppShell owner, IMauiContext context)
	{
		if (_released || (_owner is not null && _owner != owner)) { throw new InvalidOperationException("The Pane belongs to another owner."); }
		TerminalHost.Bind(context);
		_owner = owner;
	}

	internal void Prepare(string address, long lease)
	{
		if (_released) { throw new ObjectDisposedException(nameof(Pane)); }
		EndLease(lease, address);
		_work = new CancellationTokenSource();
		_paneId = address;
		_mac = (RemoteSettings.User, RemoteSettings.Host, RemoteSettings.Port);
		_leaving = _waiting = _paced = false;
		_openFind = _refind = null;
		Header.TitleText = "";
		ApplyLayout();
	}

	internal void ActivateLease()
	{
		if (_released || _leaseActive || !_onScreen || _mac != (RemoteSettings.User, RemoteSettings.Host, RemoteSettings.Port)) { return; }
		_leaseActive = true;
		Screen.ActivateLease();
		SubscribeVisible();
		Screen.Held = !App.Herdr.Agents.Foreground;
		App.Herdr.Agents.Look(_paneId);
		Bar.ApplyLanguage();
		Pad.ApplyLanguage();
		Finder.ApplyLanguage();
		SemanticProperties.SetDescription(NewKey, i18n.Translate("New"));
		SideHeader.TitleText = i18n.Translate("Herdr");
		SideHeader.SubtitleText = $"{RemoteSettings.User}@{RemoteSettings.Host}";
		SideTabs.ApplyLanguage();
		Screen.ApplyTheme();
		ShowThemeKey();
		ApplyLayout();
		ShowSideRefresh();
		OpenStream();
		ApplyOpenFind();
		DictationRestore();
	}

	internal void Park(long lease) => EndLease(lease, _paneId);

	private void EndLease(long lease, string address)
	{
		_lease = lease; // Invalidate continuations before any cancellation/event callback can run.
		bool wasActive = _leaseActive;
		_leaseActive = false;
		if (wasActive) { App.Herdr.Agents.Look(null); }
		UnsubscribeVisible();
		CloseStream();
		_reopen.Stop();
		_sideRefresh.Stop();
		_acquisition?.Cancel();
		_captureGeneration++;
		_sideList.Stop();
		if (!_work.IsCancellationRequested) { _work.Cancel(); }
		_work.Dispose();
		_linkDelay?.Cancel();
		_linkDelay?.Dispose();
		_linkDelay = null;
		Screen.Held = true;
		Screen.Reset(address, lease);
		TerminalHost.RequireArrangement();
		_viewer?.Close();
		_viewer = null;
		_selection = null;
		_finding = false;
		Finder.Clear();
		Finder.IsVisible = false;
		Screen.FindClose();
		if (Finder.IsFieldFocused) { _ = HideKeyboardAsync(); }
		Input.Close();
		Pad.IsVisible = Badge.IsVisible = LinkStrip.IsVisible = false;
		Bar.DPadShown = Bar.ScrolledBack = false;
		Bar.ResetModifiers();
		DictationLeave();
		ConnectionCard.Turn(Lbl_LinkGlyph, false);
		Lbl_LinkWord.Text = "";
		if (_parking.IsCompletedSuccessfully && Root.Children.Any(c => c is BottomSheet or DisplayMsg))
		{
			_parking = ClosePopupsAsync();
			_ = ObserveParkingAsync(_parking);
		}
	}

	private async Task ClosePopupsAsync()
	{
		await PopupsHandle.CloseAllAsync(this);
		if (Root.Children.Any(c => c is BottomSheet or DisplayMsg)) { throw new InvalidOperationException("The previous terminal's popup did not close."); }
	}

	private async Task ObserveParkingAsync(Task parking)
	{
		try { await parking; }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] closing terminal popups failed: {ex}");
			_owner?.TrimRetainedPane(pressure: true);
		}
		if (!_released && !TerminalHost.IsAttached) { HostDetached?.Invoke(); }
	}

	internal Task WaitForHostDetachAsync() => TerminalHost.WaitForDetachAsync();
	internal void DetachHost() => TerminalHost.Detach();

	private void Screen_Unusable()
	{
		CloseStream();
		if (_owner is { } owner) { _ = owner.RetirePaneAsync(); }
	}

	internal void Release()
	{
		if (_released) { return; }
		_released = true;
		Screen.Unusable -= Screen_Unusable;
		try { Park(_lease); }
		finally
		{
			Screen.ClearMark();
			_reopen.Tick -= Reopen_Elapsed;
			_sideRefresh.Tick -= Side_Elapsed;
			PaneImages.Dropped -= PaneImages_Dropped;
			TerminalHost.Changed -= Host_Changed;
			try { TerminalHost.Detach(); }
			finally
			{
				try { Screen.Release(); }
				finally
				{
					try { TerminalHost.ClearContext(); }
					finally { _owner = null; }
				}
			}
		}
	}
	private void Reopen_Elapsed(object? sender, EventArgs e) => TryReopen();
	private void Side_Elapsed(object? sender, EventArgs e) => ShowSide();

	private void Host_Changed()
	{
		Screen.HostLayoutChanged();
		if (!TerminalHost.IsAttached) { HostDetached?.Invoke(); }
		else if (_terminal is null) { OpenStream(); }
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		_onScreen = true;
		// Leave silently if the user disconnected while this pane stayed on the stack, or changed the Mac's
		// address, port, or user on Connection.
		if (!Tolerated || _mac != (RemoteSettings.User, RemoteSettings.Host, RemoteSettings.Port))
		{
			_ = LeaveAsync(null);
			return;
		}
		_owner?.PaneAppeared(this);
	}

	// Leaving the screen closes the stream (the terminal goes back to the Mac's size), the image viewer (its share file goes)
	// and cancels foreground capture and image work (a pick, an upload), not captured speech.
	protected override void OnDisappearing()
	{
		Header.DetachReading();
		base.OnDisappearing();
		_onScreen = false;
		DictationLeave();
#if ANDROID
		UnbindIme();
#endif
		_owner?.PaneDisappeared(this);
	}

	private void SubscribeVisible()
	{
		UnsubscribeVisible();
		SubscribeReading();
		App.Herdr.Agents.Changed += Agents_Changed;
		App.Herdr.StateChanged += Session_StateChanged;
		_visibleWindow = Window;
		if (_visibleWindow is { } window) { window.Stopped += Window_Stopped; window.Resumed += Window_Resumed; }
		Application.Current!.RequestedThemeChanged += Theme_Changed;
#if ANDROID
		BindIme();
#endif
	}

	private void SubscribeReading()
	{
		if (!_onScreen || !_leaseActive || _readingVisible) { return; }
		_readingVisible = true;
		_reader.Changed += Reader_Changed;
		_reader.StateChanged += Reader_StateChanged;
		if (OwnReading && Screen.Opacity == 0)
		{
			_readingPainted = _ =>
			{
				Screen.Written -= _readingPainted;
				_readingPainted = null;
				Reader_Changed();
			};
			Screen.Written += _readingPainted;
		}
		else { Reader_Changed(); }
	}

	private void UnsubscribeReading()
	{
		_readingVisible = false;
		_reader.Changed -= Reader_Changed;
		_reader.StateChanged -= Reader_StateChanged;
		if (_readingPainted is not null) { Screen.Written -= _readingPainted; _readingPainted = null; }
	}

	private void UnsubscribeVisible()
	{
		UnsubscribeReading();
#if ANDROID
		UnbindIme();
#endif
		App.Herdr.Agents.Changed -= Agents_Changed;
		App.Herdr.StateChanged -= Session_StateChanged;
		if (_visibleWindow is { } window) { window.Stopped -= Window_Stopped; window.Resumed -= Window_Resumed; }
		_visibleWindow = null;
		Application.Current!.RequestedThemeChanged -= Theme_Changed;
	}

	// Background detaches reading presentation and holds frames; captured speech owns no terminal work. The connection keeps its stream while
	// the OS lets it, or closes it and reattaches it on return.
	private void Window_Stopped(object? sender, EventArgs e)
	{
		UnsubscribeReading();
		// Detached, not ended: the page keeps its chain and the voice goes on; the read is presented again on return.
		Screen.SetReading(false);
#if ANDROID
		UnbindIme();
#endif
		_acquisition?.Cancel();
		_captureGeneration++;
		_reopen.Stop();
		Screen.Held = true;
		ShowSideRefresh();
	}

	private void Window_Resumed(object? sender, EventArgs e)
	{
		SubscribeReading();
#if ANDROID
		BindIme();
#endif
		Screen.Held = false;
		if (_waiting && _paced && _onScreen) { _reopen.Start(); }
		ShowSideRefresh();
		ShowSide();
	}

	// The sidebar's 30 s refresh runs only while wide, on screen and the app in front. App.CreateWindow's handlers run first, so
	// Agents.Foreground is already the new value in Window_Stopped and Window_Resumed.
	private void ShowSideRefresh()
	{
		if (_leaseActive && _onScreen && _wide && App.Herdr.Agents.Foreground) { _sideRefresh.Start(); } else { _sideRefresh.Stop(); }
	}

	// The view's colors for the core (it answers the program's display queries with them): the theme's ground, ink and cursor, as the
	// page draws them. A change goes with the next attach.
	private void Theme_Changed(object? sender, EventArgs e)
	{
		_terminal?.Colors = DisplayColors();
		ShowThemeKey();
	}

	// Core display colors for attachment and terminals started from the phone.
	internal static JsonObject DisplayColors()
	{
		string theme = Application.Current?.RequestedTheme == AppTheme.Dark ? "Dark" : "";
		return new JsonObject
		{
			["bg"] = App.Res<Color>("Bg" + theme).ToRgbaHex(),
			["fg"] = App.Res<Color>("ConInk" + theme).ToRgbaHex(),
			["cursor"] = App.Res<Color>("Acc" + theme).ToRgbaHex(),
		};
	}

	// Menu host for this page: open new terminals at this grid, leave affected panes before closing,
	// and route Find and Read aloud / Stop reading to the page's existing actions.
	private ArrangeHost ArrangeHere()
	{
		LeaseOrigin origin = Origin;
		return new(address =>
		{
			if (Current(origin) && address != _paneId) { SwitchPane(address); }
			return Task.CompletedTask;
		}, _paneId, (Screen.Cols, Screen.Rows), kill => Current(origin) ? Closing(kill) : Task.CompletedTask,
			() => { if (Current(origin)) { OpenFind(); } }, () => { if (Current(origin)) { Bar_ReadTapped(); } }, () => Current(origin) && (_reader.IsReading || _acquisition is { IsCancellationRequested: false }), InsertionGuard(origin));
	}

	// For terminals in a space, open the pane sheet: select a pane, split right/down, find, close,
	// or open pane/tab menus. Ungrouped terminals have no tabs or map, so open their pane menu directly.
	private async void Strip_Tapped()
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { return; }
		try
		{
			string address = _paneId;
			ArrangeHost host = ArrangeHere();
			if (App.Herdr.Agents.Arranged.Find(space => space.Terminals.Exists(t => t.Address == address)) is not { Id.Length: > 0 } space)
			{
				await HerdrRemote.Pages.Arrange.AskAsync(host, new ArrangeAsk(AskKind.Pane, Address: address));
				return;
			}
			string? home = await HerdrRemote.Pages.Arrange.HomeAsync();
			if (!Current(origin)) { return; }
			PaneSheet.Pick? pick = await PaneSheet.PopAsync(space, address, home, HerdrRemote.Pages.Arrange.CanClosePane(address), InsertionGuard(origin));
			if (!Current(origin) || pick is null) { return; }
			switch (pick.Act)
			{
				case PaneSheet.Act.Open:
					if (pick.Value != _paneId) { SwitchPane(pick.Value); }
					break;
				case PaneSheet.Act.Find:
					OpenFind();
					break;
				case PaneSheet.Act.Close:
					await HerdrRemote.Pages.Arrange.AskAsync(host, new ArrangeAsk(AskKind.ClosePane, Address: address));
					break;
				case PaneSheet.Act.More:
					await HerdrRemote.Pages.Arrange.AskAsync(host, new ArrangeAsk(AskKind.Pane, Address: address));
					break;
				case PaneSheet.Act.Tab:
					await HerdrRemote.Pages.Arrange.AskAsync(host, new ArrangeAsk(AskKind.Tab, space.Id, pick.Value));
					break;
				case PaneSheet.Act.SplitRight:
					await HerdrRemote.Pages.Arrange.AskAsync(host, new ArrangeAsk(AskKind.SplitRight, Address: address));
					break;
				case PaneSheet.Act.SplitDown:
					await HerdrRemote.Pages.Arrange.AskAsync(host, new ArrangeAsk(AskKind.SplitDown, Address: address));
					break;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] pane sheet unexpected: {ex}");
			if (Current(origin)) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again")); }
		}
	}

	// Before a user-initiated close kills this pane, switch to the nearest surviving terminal in the pill's
	// space order, searching backward before forward. Return to the list if none remain.
	// Do not show "This pane was closed." for the user's own close.
	private async Task Closing(IReadOnlyCollection<string> kill)
	{
		if (!kill.Contains(_paneId)) { return; }
		List<string> order = App.Herdr.Agents.Arranged.Find(space => space.Terminals.Exists(t => t.Address == _paneId))?.Terminals.Select(t => t.Address).ToList() ?? [];
		int at = order.IndexOf(_paneId);
		string? next = order.Take(Math.Max(at, 0)).Reverse().Concat(order.Skip(at + 1)).FirstOrDefault(address => !kill.Contains(address));
		if (next is not null) { SwitchPane(next); }
		else { await LeaveAsync(null); }
	}

	// The header's "+": the new menu of this pane's space.
	private void NewKey_Tapped(object? sender, TappedEventArgs e)
	{
		if (App.Herdr.Agents.Arranged.Find(space => space.Terminals.Exists(t => t.Address == _paneId)) is { Id.Length: > 0 } space)
		{
			_ = HerdrRemote.Pages.Arrange.AskAsync(ArrangeHere(), new ArrangeAsk(AskKind.New, space.Id));
		}
	}

	// Fit the pill into the header's lead area, subtracting "+" while that button is visible.
	private void FitPill()
	{
		if (_leadRoom <= 0) { return; }
		Strip.FitName(NewKey.IsVisible ? _leadRoom - NewKey.WidthRequest - ((Grid)NewKey.Parent).ColumnSpacing : _leadRoom);
	}

	// The theme key: the sun while the app is dark (a tap goes light), the moon while it is light (a tap goes dark).
	private void ShowThemeKey()
	{
		bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
		Lbl_Theme.Text = dark ? "" : "";
		SemanticProperties.SetDescription(ThemeKey, i18n.Translate(dark ? "Light theme" : "Dark theme"));
	}

	// One tap: the other of light and dark, saved as the same Appearance setting the Settings page shows (App.ApplyAppTheme).
	private void ThemeKey_Tapped(object? sender, TappedEventArgs e) =>
		App.ApplyAppTheme(Application.Current?.RequestedTheme == AppTheme.Dark ? "Light" : "Dark");

	// Keep this page during connection attempts, retrying disconnects, and clean background disconnections.
	private static bool Tolerated => App.Herdr.State is SessionState.Connected or SessionState.Connecting or SessionState.Reconnecting or SessionState.Disconnected;

	// Tolerated states update status; other states, including explicit Disconnect, return silently to the list.
	// The Herdr card explains the connection state.
	private void Session_StateChanged()
	{
		if (Tolerated) { ShowPane(); } else { _ = LeaveAsync(null); }
	}

	private void Agents_Changed()
	{
		TryReopen();
		ShowPane();
	}

	// Match herdr-app's level-triggered reopen rule: reattach when a retained terminal's live record has a pid.
	// If a lost event arrives after stream end, a stale pid can cause an unavailable attach; retry at ReopenTick.
	private void TryReopen()
	{
		if (!_leaseActive || !_onScreen || !App.Herdr.Agents.Foreground || !_waiting || _terminal is null || !App.Herdr.Agents.IsLive(_paneId) || !App.Herdr.Agents.HasPid(_paneId)) { return; }
		_waiting = false;
		_reopen.Stop();
		_terminal.Reopen();
	}

	private void Wait(bool paced = true)
	{
		_waiting = true;
		_paced = paced;
		if (paced && _onScreen && App.Herdr.Agents.Foreground) { _reopen.Start(); }
	}

	// Opens the terminal's stream once xterm.js is ready, at the view's grid after fit() at the pane's font size: ApplyLayout sends the
	// font first, and the stream's settle wait includes the page's grid at that font (TerminalView.SetFont), so the first attach is at
	// the final grid. The stream attaches again if the grid still changes before the first paint is written.
	private void OpenStream()
	{
		if (_terminal is not null || !_leaseActive || !_onScreen || !TerminalHost.IsAttached || _paneId.Length == 0 || !Screen.IsReady || _leaving) { return; }
		if (_terminal is null)
		{
			_terminal = new TerminalStream(_paneId) { GridSettled = Screen.WaitSettledAsync };
			Theme_Changed(this, EventArgs.Empty);
			_terminal.SetCell(Screen.Cell.Width, Screen.Cell.Height);
			_terminal.Opening += Terminal_Opening;
			_terminal.Output += Screen.Feed;
			_terminal.Ended += Terminal_Ended;
			_terminal.Failed += Terminal_Failed;
			_terminal.ImageReceived += Terminal_ImageReceived;
			_terminal.ImageReceiving += Terminal_ImageReceiving;
			_terminal.ImageDropped += Terminal_ImageDropped;
			_terminal.ImagePlaced += Terminal_ImagePlaced;
		}
		// Local: leaving the screen clears _terminal.
		TerminalStream terminal = _terminal;
		App.Herdr.Terminal = terminal;
		_waiting = false;
		_reopen.Stop();
		ApplyLayout();
		terminal.Control(Screen.Cols, Screen.Rows);
	}

	private void CloseStream()
	{
		if (_terminal is null) { return; }
		_terminal.Opening -= Terminal_Opening;
		_terminal.Output -= Screen.Feed;
		_terminal.Ended -= Terminal_Ended;
		_terminal.Failed -= Terminal_Failed;
		_terminal.ImageReceived -= Terminal_ImageReceived;
		_terminal.ImageReceiving -= Terminal_ImageReceiving;
		_terminal.ImageDropped -= Terminal_ImageDropped;
		_terminal.ImagePlaced -= Terminal_ImagePlaced;
		_terminal.Close();
		if (App.Herdr.Terminal == _terminal) { App.Herdr.Terminal = null; }
		_terminal = null;
	}

	// Another terminal of the group: the stream, the read and the selection belong to the old one.
	private async void SwitchPane(string paneId)
	{
		LeaseOrigin origin = Origin;
		try
		{
			if (_owner is not null) { await _owner.OpenPaneAsync(paneId); }
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { Debug.WriteLine($"[Pane] switch unexpected: {ex}"); if (Current(origin)) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again")); } }
	}

	// A new stream's first paint comes: the view starts empty and gets the terminal's kept images back (the core never sends them
	// again). Placeholder images show again by themselves, their cells being part of the repaint; direct placements do not come back:
	// the core's screen never kept the rows they took, so the repaint has text where they were.
	private void Terminal_Opening()
	{
		Screen.Reset(_paneId);
		foreach (PaneImage image in PaneImages.Of(_paneId)) { Screen.ShowImage(image); }
		Screen.SetImageReloadable(ImageReloadable());
	}

	// Reload images only for programs that resend them on Ctrl+L. Claude Code resends all images on normal
	// and alternate screens. pi and Gentle Shell open a model selector instead, and their images cannot be
	// recovered from the core. Codex and OpenCode draw no inline images. Other programs keep missing images.
	private bool ImageReloadable() => App.Herdr.Agents.Terminals.GetValueOrDefault(_paneId)?.Mark == "claude";

	// An image the program sent: kept, and its thumbnail drawn in every place the program put it; loading while it is made, missing
	// when it cannot be kept. The same picture sent again is the kept one, which the page may already hold (no image message then):
	// its loading state ends here.
	private async void Terminal_ImageReceived(PaneImages.Pending pending)
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { pending.Drop(); return; }
		string address = _paneId;
		Screen.ShowImageLoading(pending.Id, true);
		PaneImage? kept = await PaneImages.AddAsync(address, pending);
		if (!Current(origin)) { return; }
		if (kept is null) { if (!PaneImages.Busy(address, pending.Id)) { Screen.ShowImageLoading(pending.Id, false); } }
		else
		{
			Screen.ShowImage(kept);
			Screen.ShowImageLoading(pending.Id, false);
		}
	}

	// An image's bytes on their way, and a transmission that ended without an image: its placeholder cells loading, then missing.
	private void Terminal_ImageReceiving(long id) => Screen.ShowImageLoading(id, true);

	private void Terminal_ImageDropped(long id) => Screen.ShowImageLoading(id, false);

	// The kept images let an image go (evicted or replaced), of this terminal or another: the view lets its picture go too when it
	// holds it (TerminalView.DropImage).
	private void PaneImages_Dropped(PaneImage image)
	{
		Screen.DropImage(image.Key);
	}

	// A virtual placement: its placeholder grid, for the cells that draw the image.
	private void Terminal_ImagePlaced(long id, int cols, int rows)
	{
		if (PaneImages.Grid(_paneId, id, cols, rows) is { } image) { Screen.ShowImage(image); }
	}

	// A tap on an image: the full-size viewer laid over the whole screen, which stays (its stream and images with it), with Send to
	// agent. Its header: "Image i of n" (the image's place among the terminal's kept images) and the
	// terminal's name, space and program. Closed by its own button (Android's Back does nothing inside the app, AppShell).
	private void Screen_ImageTapped(long id)
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { return; }
		if (_viewer is not null || PaneImages.Get(_paneId, id) is not { } png) { return; }
		List<PaneImage> images = [.. PaneImages.Of(_paneId)];
		string subtitle = App.Herdr.Agents.Terminals.GetValueOrDefault(_paneId) is { } terminal
			? string.Join(" · ", new[] { terminal.Name, terminal.Space, HerdrAgents.ProgramName(terminal.App) }.Where(part => !string.IsNullOrEmpty(part)))
			: "";
		ImageViewer viewer = _viewer = new(png, full => Current(origin) ? SendImageAsync(full, "png") : Task.CompletedTask, i18n.Translate("Image"),
			images.FindIndex(image => image.Id == id) + 1, images.Count, subtitle);
		viewer.Closed += () =>
		{
			Root.Remove(viewer);
			if (_viewer == viewer) { _viewer = null; }
		};
		Grid.SetRowSpan(viewer, 3);
		Grid.SetColumnSpan(viewer, 2);
		_ = HideKeyboardAsync();
		Root.Add(viewer);
	}

	// Herdr stages the image beside the selected terminal. Paste the returned path without submitting it.
	private async Task SendImageAsync(byte[] data, string extension, Func<bool>? canSend = null)
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin) || canSend?.Invoke() == false) { return; }
		CancellationToken ct = _work.Token;
		using MemoryStream stream = new(data, false);
		string? path = await StageAttachmentAsync(origin, stream, $"image.{extension}", ct);
		if (path is not null && Current(origin) && canSend?.Invoke() != false) { Send(TerminalInput.Attachment(path, Screen.Modes)); }
	}

	private async Task<string?> StageAttachmentAsync(LeaseOrigin origin, Stream source, string name, CancellationToken ct)
	{
		try
		{
			CoreApi api = App.Herdr.Api ?? throw new CoreLostException("terminal.put");
			return await api.PutAsync(origin.Address, source, name, ct);
		}
		catch (InvalidDataException ex) when (ex.Message is CoreApi.AttachmentTooLarge or CoreApi.AttachmentsUnavailable)
		{
			if (Current(origin)) { await ShowMessageAsync(origin, "Attach", i18n.Translate(ex.Message)); }
			return null;
		}
	}

	// Route all program input here; user input first reclaims the phone's terminal size through ClaimSize.
	private bool Send(byte[] bytes)
	{
		if (_leaving || !Current(Origin)) { return false; }
		ClaimSize();
		return _terminal?.Input(bytes) == true;
	}

	// On keys, typing, paste, terminal taps, or the keyboard button, reclaim the phone's grid if another viewer
	// has changed the terminal size. Never claim in response to size events, which would make viewers compete.
	private void ClaimSize()
	{
		if (_terminal is not { } terminal || App.Herdr.Agents.SizeOf(_paneId) is not { Cols: > 0, Rows: > 0 } shown) { return; }
		if (shown != (terminal.Cols, terminal.Rows)) { terminal.Claim(); }
	}

	// The core ended the stream (null: the line dropped, and the session reattaches it once it is back). A kept terminal that lost its
	// program comes back with a new shell: it is attached again once its record has a pid (TryReopen). Any other end means its program
	// is over.
	private void Terminal_Ended(string? reason)
	{
		if (reason is null) { return; }
		if (App.Herdr.Agents.IsLive(_paneId) && reason is "lost" or "signaled" or "spawn_pending" or "holder_lost")
		{
			Wait();
			return;
		}
		_ = ClosedAsync();
	}

	// The attach failed: gone (not_found, finished) leaves; a kept terminal waiting for its new shell (unavailable) waits for its
	// record's pid (TryReopen), paced by ReopenTick; any other error waits for the next change of the terminals, unpaced, so a lasting
	// error is not tried again twice a second; no connection (null) waits for the session, which attaches it again once connected.
	private void Terminal_Failed(string? kind)
	{
		if (kind is "not_found" or "finished") { _ = ClosedAsync(); }
		else if (kind is "unavailable") { Wait(); }
		else if (kind is not null) { Wait(false); }
	}

	// Show "This pane was closed." once, or herdr-app's launch-failure notice for a terminal started
	// by this phone that exits immediately with a nonzero code.
	private async Task ClosedAsync()
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { return; }
		string address = _paneId;
		string? failure = await HerdrRemote.Pages.Arrange.LaunchFailureAsync(address);
		// Switched to another pane while it was asked: that one is not over.
		if (!Current(origin)) { return; }
		await (failure is null ? LeaveAsync(i18n.Translate("This pane was closed.")) : LeaveAsync(failure, "Terminal failed to start"));
	}

	// A null message leaves silently; a message is shown already translated, under the title's catalog key.
	private async Task LeaveAsync(string? message, string title = "Herdr")
	{
		LeaseOrigin origin = Origin;
		AppShell? owner = _owner;
		if (_leaving || !Current(origin) || owner is null) { return; }
		_leaving = true;
		try
		{
			CloseStream();
			// A sheet or dialog still open on this screen (an Arrange menu) closes first, so its ask ends (Arrange's one-at-a-time
			// guard is released) and the message below can show.
			await PopupsHandle.CloseAllAsync(this);
			if (!Current(origin)) { return; }
			if (message is not null) { await ShowMessageAsync(origin, title, message); }
			if (!Current(origin)) { return; }
			await owner.NavigateAsync(() => Current(origin) ? owner.GoToAsync("..") : Task.CompletedTask);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] leave unexpected: {ex}");
		}
	}

	// The strip under the header while the line is down: shown LinkStripDelay after it went down if it is still down then, hidden at
	// once when it is back.
	private void ShowLinkStrip(bool down, SessionState state)
	{
		if (!down)
		{
			_linkDelay?.Cancel();
			_linkDelay?.Dispose();
			_linkDelay = null;
			LinkStrip.IsVisible = false;
			Lbl_LinkWord.Text = "";
			ConnectionCard.Turn(Lbl_LinkGlyph, false);
			return;
		}
		if (LinkStrip.IsVisible)
		{
			Lbl_LinkWord.Text = ConnectionCard.StateWord(state, true);
			return;
		}
		if (_linkDelay is not null) { return; }
		CancellationTokenSource delay = _linkDelay = new CancellationTokenSource();
		LeaseOrigin origin = Origin;
		_ = Task.Delay(LinkStripDelay, delay.Token).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() =>
		{
			if (!Current(origin) || _linkDelay != delay) { return; }
			_linkDelay = null;
			delay.Dispose();
			SessionState now = App.Herdr.State;
			if (now is not (SessionState.Connecting or SessionState.Reconnecting)) { return; }
			LinkStrip.IsVisible = true;
			Lbl_LinkWord.Text = ConnectionCard.StateWord(now, true);
			ConnectionCard.Turn(Lbl_LinkGlyph, true);
		}), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
	}

	// Link strip, header, pill and the jump-to-bottom button from the session's state, its terminals and the view (no request). The
	// open pane's turn is shared as seen by HerdrAgents (Look).
	private void ShowPane()
	{
		Screen.SetImageReloadable(ImageReloadable());
		SessionState state = App.Herdr.State;
		bool linkDown = state is SessionState.Connecting or SessionState.Reconnecting;
		// Show connection status even without a terminal record. Delay the strip by LinkStripDelay to hide quick
		// reconnects, such as app return; always show the Mac status dot in the header or wide sidebar.
		ShowLinkStrip(linkDown, state);
		HeaderTone tone = ConnectionCard.Tone(state, true);
		string description = string.Format(CultureInfo.CurrentCulture, i18n.Translate("Connection: {0}"), ConnectionCard.StateWord(state, true));
		Header.ActionTone = tone;
		Header.ActionDescription = description;
		SideHeader.ActionTone = tone;
		SideHeader.ActionDescription = description;
		if (App.Herdr.Agents.Terminals.GetValueOrDefault(_paneId) is { } terminal)
		{
			Header.TitleText = terminal.Name;
		}
		// Order panes by the space's tabs in document order, then tree order within each tab.
		Strip.Show(App.Herdr.Agents.Arranged, _paneId);
		bool placed = App.Herdr.Agents.Arranged.Exists(space => space.Id.Length > 0 && space.Terminals.Exists(t => t.Address == _paneId));
		if (NewKey.IsVisible != placed)
		{
			NewKey.IsVisible = placed;
			FitPill();
		}
		Bar.ScrolledBack = Screen.ScrolledBack;
		ShowSide();
	}

	// Show the sidebar and highlight this pane only when wide, visible, and foregrounded.
	// Never build rows in the background; AgentList.Stop also stops off-screen spinners.
	// Every ShowPane uses this guard; Window_Resumed draws the current list once on return.
	private void ShowSide()
	{
		if (!_leaseActive || !_wide || !_onScreen || !App.Herdr.Agents.Foreground) { return; }
		_sideList.Selected = _paneId;
		_sideList.Show();
	}

	// Both orientations: header, terminal, tray. While typing the header stays in portrait and goes in landscape,
	// where the keyboard leaves room for the key bar and a few rows only (the key bar's keyboard key brings the header and its back again).
	private void ApplyLayout()
	{
		Header.ChromeVisible = !(_landscape && _keyboardUp);
		Finder.IsVisible = _finding;
		// Wide: the header has no back (the list is on screen), and the Mac action and its dot are the sidebar's (shown once).
		Header.BackVisible = !_wide;
		Header.ActionGlyph = _wide ? "" : "";
		Bar.Show(_keyboardUp);
		Bar.Reading = _reader.IsReading || _acquisition is { IsCancellationRequested: false };
		// Use the pane's saved pinch size, else 13.5 points when wide, 12.5 in phone landscape, or 13 in portrait.
		// Line heights adapt the reference spacing (1.5, or 1.45 in phone landscape) to Geist Mono.
		// Wait until ready: the web page drops earlier font messages, leaving Screen.FontSize ahead of the actual font.
		double size = RemoteSettings.FontSize(_paneId) ?? (_wide ? 13.5 : _landscape ? 12.5 : 13);
		double lineHeight = _landscape && !_wide ? 1.115 : 1.154;
		if (Screen.IsReady && (size != Screen.FontSize || lineHeight != Screen.LineHeight)) { Screen.SetFont(size, lineHeight); }
		ShowPane();
	}

	// Recompute wide mode on every resize, including folds without rotation: both dimensions must reach 600.
	// Use a 320-wide sidebar from width 800, otherwise 300. Width greater than height selects landscape layout.
	protected override void OnSizeAllocated(double width, double height)
	{
		base.OnSizeAllocated(width, height);
		if (width <= 0) { return; }
		bool wide = width >= 600 && height >= 600;
		bool wideChanged = wide != _wide;
		if (wideChanged)
		{
			_wide = wide;
			Side.IsVisible = wide;
			if (!wide) { _sideList.Stop(); }
			ShowSideRefresh();
		}
		if (wide) { Side.WidthRequest = width >= 800 ? 320 : 300; }
		bool landscape = width > height;
		if (landscape == _landscape)
		{
			if (wideChanged) { ApplyLayout(); }
			return;
		}
		_landscape = landscape;
		ApplyLayout();
	}

	// The Mac action (the header's, or the sidebar's when wide), as Herdr's header: the Connection page over this screen, in every
	// state.
	private void Header_ActionTapped(object? sender, EventArgs e) => _ = App.GoToAsync("Connection");

	private void Header_BackTapped(object? sender, EventArgs e) => _ = App.GoToAsync("..");

	// Open a focused, empty Find bar from the pane sheet's Find or the pane menu's Find in terminal.
	internal void OpenFind()
	{
		_finding = true;
		ApplyLayout();
		Finder.Start();
	}

	// Search all panes opened this pane for a query: its Find starts with it (the field not focused, no keyboard) once the lease is
	// active, so the terminal's own count and marks appear on the newest match.
	private string? _openFind;

	internal void FindOnOpen(string query)
	{
		_openFind = query;
		_retained = _leaseActive;
		if (_leaseActive) { ApplyOpenFind(); }
	}

	private string? _refind;
	private bool _retained;

	private void ApplyOpenFind()
	{
		if (_openFind is not { } query) { return; }
		_openFind = null;
		_finding = true;
		ApplyLayout();
		Finder.Show(query);
		if (!_retained) { _refind = query; }
	}

	// The bar's close, and switching panes: the search and its marks end; the keyboard goes down when the
	// bar's field had it.
	private void CloseFind()
	{
		if (!_finding) { return; }
		_finding = false;
		_refind = null;
		if (Finder.IsFieldFocused) { _ = HideKeyboardAsync(); }
		Screen.FindClose();
		ApplyLayout();
	}

	// A lone terminal tap claims the size without opening the keyboard.
	private void Screen_Tapped()
	{
		ClaimSize();
	}

	// Hides the soft keyboard through the shared popup path (Unfocus alone leaves the Android keyboard up).
	private async Task HideKeyboardAsync()
	{
		LeaseOrigin origin = Origin;
		try { Input.Close(); await PopupsHandle.HideKeyboardAsync(); }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] hide keyboard unexpected: {ex}");
			if (Current(origin)) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again")); }
		}
	}

	// Toggle the soft keyboard.
	private async void Bar_KeyboardTapped()
	{
		ClaimSize();
		if (_keyboardUp)
		{
			await HideKeyboardAsync();
			return;
		}
		Input.Open();
	}

	private void Screen_Pinched(bool ended, int size)
	{
		Lbl_Badge.Text = $"{size.ToString(CultureInfo.CurrentCulture)} pt";
		Badge.IsVisible = !ended;
	}

#if ANDROID
	private void BindIme()
	{
		if (!_leaseActive || !_onScreen || _released || !App.Herdr.Agents.Foreground || Root.Handler?.PlatformView is not Android.Views.View root) { return; }
		if (!ReferenceEquals(_imeRoot, root))
		{
			UnbindIme();
			_imeRoot = root;
			LeaseOrigin origin = Origin;
			long generation = _imeGeneration;
			_imeLayout = (_, _) =>
			{
				if (generation != _imeGeneration || !ReferenceEquals(root, _imeRoot) || !Current(origin) || !App.Herdr.Agents.Foreground || !root.IsAttachedToWindow) { return; }
				var insets = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(root);
				if (insets is null) { return; }
				bool shown = insets.IsVisible(AndroidX.Core.View.WindowInsetsCompat.Type.Ime());
				if (_keyboardUp == shown) { return; }
				_keyboardUp = shown;
				ApplyLayout();
			};
			root.ViewAttachedToWindow += ImeRoot_Attached;
			root.ViewDetachedFromWindow += ImeRoot_Detached;
		}
		ObserveIme();
	}

	private void ObserveIme()
	{
		if (_imeRoot is not { IsAttachedToWindow: true } root || _imeObserver is not null || root.ViewTreeObserver is not { IsAlive: true } observer) { return; }
		_imeObserver = observer;
		observer.GlobalLayout += _imeLayout;
		_imeLayout?.Invoke(root, EventArgs.Empty);
	}

	private void ImeRoot_Attached(object? sender, Android.Views.View.ViewAttachedToWindowEventArgs e)
	{
		if (ReferenceEquals(sender, _imeRoot)) { ObserveIme(); }
	}

	private void ImeRoot_Detached(object? sender, Android.Views.View.ViewDetachedFromWindowEventArgs e)
	{
		if (ReferenceEquals(sender, _imeRoot)) { StopImeObserver(); }
	}

	private void StopImeObserver()
	{
		if (_imeObserver is { IsAlive: true } observer) { observer.GlobalLayout -= _imeLayout; }
		_imeObserver = null;
	}

	private void UnbindIme()
	{
		_imeGeneration++;
		StopImeObserver();
		if (_imeRoot is { } root)
		{
			root.ViewAttachedToWindow -= ImeRoot_Attached;
			root.ViewDetachedFromWindow -= ImeRoot_Detached;
		}
		_imeRoot = null;
		_imeLayout = null;
	}
#endif

	private void Input_TextReceived(string typed) => TerminalKeyEvents.Insert(typed, Bar.Modify,
		text => Send(TerminalInput.Text(text, false, Screen.Modes)),
		key =>
		{
			if (key.StartsWith("ctrl+", StringComparison.Ordinal) || key.StartsWith("alt+", StringComparison.Ordinal)) { Input.ResetComposition(); }
			Send(TerminalInput.Key(key, Screen.Modes));
		});

	// Paste text directly; upload an image to the Mac and paste its path (Claude Code shows "[Image #n]").
	// If the clipboard has both, ask which to send.
#if IOS
	private async void Screen_NativePaste(TerminalView.NativePastePayload payload)
	{
		LeaseOrigin origin = Origin;
		bool CurrentPaste() => Current(origin) && payload.Current;
		try
		{
			if (!CurrentPaste() || payload.Lease != origin.Lease.ToString(System.Globalization.CultureInfo.InvariantCulture) || !payload.TryAccept()) { return; }
			await PasteCapturedAsync(origin, CurrentPaste, payload.Kinds, payload.Text, payload.Image, payload.Error, payload.Rejected);
		}
		catch (OperationCanceledException) when (!CurrentPaste()) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] native paste unexpected: {ex.GetType().Name}");
			if (CurrentPaste()) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again"), CurrentPaste); }
		}
	}

	private async Task PasteCapturedAsync(LeaseOrigin origin, Func<bool> current, (bool Text, bool Image) kinds, string? text,
		(byte[] Data, string Extension)? image, Exception? error, bool rejected)
	{
		try
		{
			if (!current()) { return; }
			if (rejected || error is not null)
			{
				if (error is not null) { Debug.WriteLine($"[Pane] native paste unexpected: {error.GetType().Name}"); }
				await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again"), current);
				return;
			}
			(bool hasText, bool hasImage) = kinds;
			if (hasText && hasImage)
			{
				string asText = i18n.Translate("Text"), asImage = i18n.Translate("Image");
				string? picked = await PopupsHandle.PopListPickerAsync([asText, asImage], i18n.Translate("Paste"), null, InsertionGuard(origin, current));
				if (!current() || picked is null) { return; }
				hasImage = picked == asImage;
			}
			if (!current()) { return; }
			if (hasImage)
			{
				if (image is { } file && file.Data.Length > 0) { await SendImageAsync(file.Data, file.Extension, current); return; }
				await ShowMessageAsync(origin, "Paste", i18n.Translate("The clipboard image could not be read. Copy it again and try pasting."), current);
				return;
			}
			else if (hasText && text is { Length: > 0 } pasted) { Send(TerminalInput.Text(pasted, true, Screen.Modes)); }
		}
		catch (OperationCanceledException) when (!current()) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] native paste unexpected: {ex.GetType().Name}");
			if (current())
			{
				await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again"), current);
			}
		}
	}
#else
	private async Task PasteAsync()
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { return; }
		Input.ResetComposition();
		(bool text, bool image) = ClipboardImage.Kinds();
		if (text && image)
		{
			string asText = i18n.Translate("Text");
			string asImage = i18n.Translate("Image");
			string? picked = await PopupsHandle.PopListPickerAsync([asText, asImage], i18n.Translate("Paste"), null, InsertionGuard(origin));
			if (!Current(origin) || picked is null) { return; }
			image = picked == asImage;
		}
		if (image)
		{
			var file = await ClipboardImage.ReadAsync();
			if (!Current(origin)) { return; }
			if (file is { } found && found.Data.Length > 0) { await SendImageAsync(found.Data, found.Extension); return; }
			await ShowMessageAsync(origin, "Paste", i18n.Translate("The clipboard image could not be read. Copy it again and try pasting."));
			return;
		}
		string? pasted = await Clipboard.Default.GetTextAsync();
		if (Current(origin) && pasted is { Length: > 0 }) { Send(TerminalInput.Text(pasted, true, Screen.Modes)); }
	}
#endif

	// Start reading only on a tap: stop an active read, otherwise read the last copied/selected block,
	// falling back to visible terminal text. Attach becomes Stop during reading; idle Read aloud lives in the pane menu.
	private void Bar_ReadTapped()
	{
		if (_acquisition is { IsCancellationRequested: false } acquisition)
		{
			_acquisition = null;
			_captureGeneration++;
			acquisition.Cancel();
			Reader_Changed();
		}
		else if (_reader.IsReading) { _reader.Stop(); }
		else
		{
			string? selection = _selection;
			_selection = null;
			_ = ReadCapturedAsync(selection is null ? null : [selection]);
		}
	}

	// Attach stops an active read; otherwise offer Photos, Camera when available, and Files.
	// Use the system picker, stage the file through Herdr, then paste its path.
	// Permission refusal shows one shared message on both platforms; cancelling the picker does nothing.
	private async void Bar_AttachTapped()
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { return; }
		CancellationToken ct = _work.Token;
		if (_reader.IsReading || _acquisition is { IsCancellationRequested: false })
		{
			Bar_ReadTapped();
			return;
		}
		try
		{
			MenuSheet.Row photos = new(i18n.Translate("Photos"), "\uE90F");
			MenuSheet.Row camera = new(i18n.Translate("Camera"), "\uE90E");
			MenuSheet.Row files = new(i18n.Translate("Files"), "\uE962");
			List<MenuSheet.Entry> rows = MediaPicker.Default.IsCaptureSupported ? [photos, camera, files] : [photos, files];
			MenuSheet.Row? picked = await MenuSheet.PopAsync(i18n.Translate("Attach"), rows, CanInsert: InsertionGuard(origin));
			if (!Current(origin) || picked is null) { return; }
			FileResult? file;
			try
			{
				// Android's picker may not keep to the limit: only the first is taken.
				file = picked == photos ? (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 })).FirstOrDefault()
					: picked == camera ? await MediaPicker.Default.CapturePhotoAsync()
					: await FilePicker.Default.PickAsync();
			}
			catch (PermissionException)
			{
				if (Current(origin)) { await ShowMessageAsync(origin, "Attach", i18n.Translate("Remote Shell has no permission for this. Allow it in the phone's settings.")); }
				return;
			}
			if (!Current(origin) || file is null) { return; }
			await using Stream stream = await file.OpenReadAsync();
			if (!Current(origin)) { return; }
			// The line went down during the picker's trip: the session connects again, then attaches the pane's stream again, and keys
			// sent before that stream is open are lost (TerminalStream.Input). The path is typed once the stream paints again.
			TerminalStream? terminal = _terminal;
			TaskCompletionSource painted = new(TaskCreationOptions.RunContinuationsAsynchronously);
			void Painted(byte[] data, int length, int bytes) => painted.TrySetResult();
			bool down = App.Herdr.State != SessionState.Connected;
			if (down && terminal is not null) { terminal.Output += Painted; }
			try
			{
				if (down) { await ConnectedAsync(ct); }
				if (!Current(origin)) { return; }
				string? path = await StageAttachmentAsync(origin, stream, file.FileName, ct);
				if (path is null || !Current(origin)) { return; }
				if (down) { await painted.Task.WaitAsync(AttachWait, ct); }
				if (!Current(origin)) { return; }
				Send(TerminalInput.Attachment(path, Screen.Modes));
			}
			finally
			{
				if (terminal is not null) { terminal.Output -= Painted; }
			}
		}
		catch (OperationCanceledException) when (!Current(origin)) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] attach unexpected: {ex}");
			if (Current(origin)) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again")); }
		}
	}

	// A picker can outlast the SSH connection: Android has closed the socket five seconds after camera launch,
	// then reconnected on app return. Wait up to AttachWait for the session before sending the picked file.
	private static async Task ConnectedAsync(CancellationToken ct)
	{
		TaskCompletionSource back = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void Changed()
		{
			if (App.Herdr.State == SessionState.Connected) { back.TrySetResult(); }
		}
		App.Herdr.StateChanged += Changed;
		try
		{
			Changed();
			await back.Task.WaitAsync(AttachWait, ct);
		}
		finally { App.Herdr.StateChanged -= Changed; }
	}

	// The Read glyph follows the reader; a read's marks go when it ends or stops. A read of another pane, or one the voice still holds
	// while this pane is away, leaves the page's chain as it is (detached); the chain is dropped only once no read is left.
	private void Reader_Changed()
	{
		if (!_readingVisible || !_onScreen || !_leaseActive) { return; }
		Bar.Reading = _reader.IsReading || _acquisition is { IsCancellationRequested: false };
		if (!OwnReading)
		{
			if (Screen.ReadingId != 0) { Screen.SetReading(false); }
			if (_markShown && !_reader.IsReading && !_reader.CanContinue) { Screen.ClearMark(); _markShown = false; }
		}
		else if (Screen.IsReady && Screen.Opacity != 0 && (Screen.ReadingId == 0 || _presentationGeneration != _reader.Generation))
		{
			Screen.SetReading(true);
			_presentationGeneration = _reader.Generation;
			_markShown = true;
		}
		if (!_reader.IsPreview && _reader.TakeError() is { } error) { _ = ShowMessageAsync(Origin, "Read", error); }
	}

	private void Reader_StateChanged(int seq, string state)
	{
		if (_readingVisible && _onScreen && _leaseActive && OwnReading && Screen.ReadingId != 0) { Screen.ShowReadState(seq, state); }
	}

	// The key bar's Read: the given text, else the pane's whole text, as a finite read (no marks on the page).
	private async Task ReadCapturedAsync(IReadOnlyList<string>? blocks)
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin)) { return; }
		_acquisition?.Cancel();
		using CancellationTokenSource acquisition = CancellationTokenSource.CreateLinkedTokenSource(_work.Token);
		_acquisition = acquisition;
		long capture = ++_captureGeneration;
		long readingGeneration = _reader.Generation;
		Reader_Changed();
		try
		{
			if (blocks is null)
			{
				try
				{
					CoreRead? read = App.Herdr.Api is { } api ? await api.ReadAsync(_paneId, 0, acquisition.Token) : null;
					if (!Current(origin) || acquisition.IsCancellationRequested || capture != _captureGeneration || readingGeneration != _reader.Generation) { return; }
					if (read is null) { await ShowMessageAsync(origin, "Read", i18n.Translate("Something went wrong, please try again")); return; }
					blocks = PaneText.Paragraphs(read.Text);
				}
				catch (OperationCanceledException) when (acquisition.IsCancellationRequested) { return; }
			}
			if (!Current(origin) || acquisition.IsCancellationRequested || capture != _captureGeneration || readingGeneration != _reader.Generation) { return; }
			if (blocks.Count == 0) { await ShowMessageAsync(origin, "Read", i18n.Translate("Nothing to read on this pane.")); return; }
			if (_acquisition == acquisition) { _acquisition = null; }
			await _reader.ReadAsync(blocks, ReadingMachine, _paneId, App.Herdr.Agents.Terminals.GetValueOrDefault(_paneId)?.Name ?? _paneId);
		}
		catch (OperationCanceledException) when (acquisition.IsCancellationRequested) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] capture failed: {ex.GetType().Name}");
			if (Current(origin) && capture == _captureGeneration) { await ShowMessageAsync(origin, "Read", i18n.Translate("Something went wrong, please try again")); }
		}
		finally { if (_acquisition == acquisition) { _acquisition = null; Reader_Changed(); } }
	}

	// The selection menu's Paste (the system's edit menu offers it on any held touch, on text or not, when the clipboard has content).
#if !IOS
	private async void Screen_PasteAsked()
	{
		LeaseOrigin origin = Origin;
		try { await PasteAsync(); }
		catch (OperationCanceledException) when (!Current(origin)) { }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] paste unexpected: {ex}");
			if (Current(origin)) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again")); }
		}
	}
#endif

	// The selection menu's Copy: the selected text to the clipboard (and kept for the key bar's Read).
	private async void Screen_Copied(string text)
	{
		LeaseOrigin origin = Origin;
		try
		{
			_selection = text;
			await Clipboard.Default.SetTextAsync(text);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] copy unexpected: {ex}");
			if (Current(origin)) { await ShowMessageAsync(origin, "Error", i18n.Translate("Something went wrong, please try again")); }
		}
	}

	// Read from here: the page took the first sentences from the selection's first cell and goes on as the terminal writes; the reader
	// speaks them and waits for more (the page is told the read is on by Reader_Changed).
	private async void Screen_ReadStarted(List<string> texts)
	{
		LeaseOrigin origin = Origin;
		if (!Current(origin) || texts.Count == 0) { return; }
		_acquisition?.Cancel();
		_acquisition = null;
		_captureGeneration++;
		try { await _reader.StartStreamAsync(texts, ReadingMachine, _paneId, App.Herdr.Agents.Terminals.GetValueOrDefault(_paneId)?.Name ?? _paneId); }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Pane] read start failed: {ex.GetType().Name}");
			if (Current(origin)) { await ShowMessageAsync(origin, "Read", i18n.Translate("Something went wrong, please try again")); }
		}
	}
}
