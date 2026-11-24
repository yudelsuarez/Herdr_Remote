namespace HerdrRemote;

public partial class App : Application
{
	// Compute the About values on demand so the copyright uses the current language.
	public static string VersionText => $"{AppInfo.Current.Version.Major}.{AppInfo.Current.Version.Minor}.{AppInfo.Current.Version.Build} ({AppInfo.BuildString})";

	public static string CopyrightText => $"© {DateTime.Now.Year} Yudel Suarez. Apache License 2.0";

	// Check body text scale at startup; rows do not restack while the app is running.
	// In testing, Android scale 1.3 and iOS XXL first meet this threshold. Unstacked lists break at 1.3 and XXXL, respectively.
	// Android scale 1.15 and iOS XL keep the standard layout.
	private const double LargeTextScale = 1.2;
	public static bool LargeText { get; private set; }

	private static bool DetectLargeText()
	{
#if IOS
		// The Body style is 17 pt at the default Large size.
		return UIKit.UIFont.GetPreferredFontForTextStyle(UIKit.UIFontTextStyle.Body)!.PointSize / 17.0 >= LargeTextScale;
#elif ANDROID
		Android.Util.DisplayMetrics metrics = Android.App.Application.Context.Resources!.DisplayMetrics!;
		// API 34+ applies the non-linear font curve to sp.
		return Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Sp, 17, metrics) / Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Dip, 17, metrics) >= LargeTextScale;
#else
		return false;
#endif
	}

	// At large text sizes, an opted-in grid gives the first flexible column a row of its own and moves the following columns below it.
	// Set each child's Grid.Row and Grid.Column before adding it; ChildAdded uses those values.
	public static readonly BindableProperty StackOnLargeTextProperty = BindableProperty.CreateAttached("StackOnLargeText", typeof(bool), typeof(App), false, propertyChanged: OnStackOnLargeTextChanged);
	public static bool GetStackOnLargeText(BindableObject view) => (bool)view.GetValue(StackOnLargeTextProperty);
	public static void SetStackOnLargeText(BindableObject view, bool value) => view.SetValue(StackOnLargeTextProperty, value);

	private static readonly BindableProperty StackWatchedProperty = BindableProperty.CreateAttached("StackWatched", typeof(bool), typeof(App), false);
	private static readonly BindableProperty StackRemappedProperty = BindableProperty.CreateAttached("StackRemapped", typeof(bool), typeof(App), false);

	private static void OnStackOnLargeTextChanged(BindableObject bindable, object oldValue, object newValue)
	{
		if (newValue is not true || !LargeText || bindable is not Grid grid || (bool)grid.GetValue(StackWatchedProperty)) { return; }
		grid.SetValue(StackWatchedProperty, true);
		foreach (IView child in grid.Children.ToList()) { StackChild(grid, child); }
		grid.ChildAdded += (_, e) => StackChild(grid, e.Element);
	}

	private static void StackChild(Grid grid, object? element)
	{
		if (element is not BindableObject child || element is not IView || (bool)child.GetValue(StackRemappedProperty)) { return; }
		int columns = grid.ColumnDefinitions.Count;
		int n = grid.ColumnDefinitions.ToList().FindIndex(d => d.Width.GridUnitType != GridUnitType.Absolute);
		if (n < 0 || columns - 1 <= n) { return; }
		int r = Grid.GetRow(child), c = Grid.GetColumn(child), s = Grid.GetColumnSpan(child), rs = Grid.GetRowSpan(child);
		child.SetValue(StackRemappedProperty, true);
		if (c < n) { Grid.SetRow(child, 2 * r); Grid.SetRowSpan(child, 2 * rs); }
		else if (c <= n && n < c + s) { Grid.SetRow(child, 2 * r); Grid.SetColumnSpan(child, columns - c); }
		else { Grid.SetRow(child, 2 * r + 1); }
		int rows = grid.Children.Max(x => Grid.GetRow((BindableObject)x) + Grid.GetRowSpan((BindableObject)x));
		if (grid.RowDefinitions.Count >= rows) { return; }
		// Grids share their style's row definitions. Replace the collection rather than modifying it in place.
		RowDefinitionCollection copy = [];
		foreach (RowDefinition d in grid.RowDefinitions) { copy.Add(new RowDefinition { Height = d.Height }); }
		while (copy.Count < rows) { copy.Add(new RowDefinition { Height = GridLength.Auto }); }
		grid.RowDefinitions = copy;
	}

	public App()
	{
		LargeText = DetectLargeText();   // Grid styles read this during InitializeComponent.
		InitializeComponent();
		Herdr.Agents.TerminalRetired += Pages.PaneImages.RemoveTerminal;
		Herdr.Agents.ImageCacheReset += Pages.PaneImages.Clear;

#if ANDROID
		// MAUI's window handler defaults to Pan even if the manifest requests Resize.
		// Force Resize so panning does not subtract its offset from the keyboard inset.
		Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.Application.SetWindowSoftInputModeAdjust(
			this,
			Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.WindowSoftInputModeAdjust.Resize);
#endif

		RequestedThemeChanged += (_, _) => SetSystemBarIcons();

		LoadPreferLanguage();
		ApplyAppTheme(AppThemeSetting);
	}

	// Shared services and app settings. Keep Preferences and SecureStorage access here.
	internal static readonly Services.TtsClient Tts = new();
	internal static readonly Services.ReadingSession Reading = new();
	internal static readonly Services.DictationSession Dictation = new();

	// The app shares one SSH session to the herdr host.
	internal static readonly Remote.HerdrSession Herdr = new();

	// Cache preferences (null means missing) and write changes through to storage.
	// The UI and voice providers read the cache from different threads.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Settings = new();

	internal static string Setting(string key, string fallback) =>
		Settings.GetOrAdd(key, static stored => Preferences.ContainsKey(stored) ? Preferences.Get(stored, "") : null) ?? fallback;

	internal static void SaveSetting(string key, string value)
	{
		Preferences.Set(key, value);
		Settings[key] = value;
	}

	// System follows the OS. Refresh the bars even when the effective theme stays the same:
	// iOS reads UserAppTheme for the window style, but RequestedThemeChanged only fires for an effective change.
	private const string AppThemeKey = "AppTheme";
	public static string AppThemeSetting => Setting(AppThemeKey, "System");
	public static void ApplyAppTheme(string theme)
	{
		SaveSetting(AppThemeKey, theme);
		if (Current is not null)
			Current.UserAppTheme = theme switch { "Light" => AppTheme.Light, "Dark" => AppTheme.Dark, _ => AppTheme.Unspecified };
		SetSystemBarIcons();
	}

	internal static Task<string?> GetApiKeyAsync(string providerId) => SecureStorage.Default.GetAsync($"tts.{providerId}.api_key");

	internal static Task SaveApiKeyAsync(string providerId, string key) => SecureStorage.Default.SetAsync($"tts.{providerId}.api_key", key);

	// SshKeyStore defines the format of the phone's SSH key pair.
	internal static Task<string?> GetSshKeyAsync() => SecureStorage.Default.GetAsync("herdr.ssh_key");

	internal static Task SaveSshKeyAsync(string value) => SecureStorage.Default.SetAsync("herdr.ssh_key", value);

	/// <summary>
	/// Configures the keyboard safe area from page constructors. On edge-to-edge Android, adjustResize adds no padding,
	/// and MAUI 10 only applies the keyboard inset to edges that include SoftInput. Set the bottom edge to All
	/// to use the navigation bar inset when the keyboard is closed and the keyboard inset when open.
	/// iOS scrolls the focused field itself.
	/// </summary>
	internal static void ApplyKeyboardSafeArea(ContentPage page)
	{
#if ANDROID
		if (page.Content is Layout root)
		{
			root.SafeAreaEdges = new SafeAreaEdges(
				SafeAreaRegions.Container,
				SafeAreaRegions.Container,
				SafeAreaRegions.Container,
				SafeAreaRegions.All);
			// After Back or hardware Enter clears focus in non-touch mode, let the root take it instead of a button or list.
			// Apply this on HandlerChanged because a tab root can get a new native view.
			root.HandlerChanged += (_, _) =>
			{
				if (root.Handler?.PlatformView is not Android.Views.View sink) return;
				sink.Focusable = true;
				sink.FocusableInTouchMode = true;
				if (OperatingSystem.IsAndroidVersionAtLeast(26)) sink.DefaultFocusHighlightEnabled = false;
				sink.ImportantForAccessibility = Android.Views.ImportantForAccessibility.No; // Skip only the root, not its accessible children.
			};
		}
		// When a tab gets a new native view, MAUI can drop insets received before its first layout at zero size.
		// Request the insets again before drawing and wait up to three frames for the header to clear the status bar.
		page.Loaded += (_, _) =>
		{
			if (page.Handler?.PlatformView is not Android.Views.View view) return;
			// On each attach in non-touch mode, give the root focus unless an input field already has it.
			if (page.Content?.Handler?.PlatformView is Android.Views.View sink) sink.Post(() => { if (!sink.IsInTouchMode && FocusedEntry is null) sink.RequestFocus(); });
			// After Back hides the keyboard, Shell's full-screen pager can take focus and tint the whole window.
			// Disable only the container highlight; buttons still show keyboard focus.
			for (Android.Views.IViewParent? parent = view.Parent; parent is not null; parent = parent.Parent)
			{
				if (parent is AndroidX.RecyclerView.Widget.RecyclerView { Parent: AndroidX.ViewPager2.Widget.ViewPager2 } pager)
				{
					pager.DefaultFocusHighlightEnabled = false;
					break;
				}
			}
			// A retained tab with a top inset already applied does not need to wait before drawing.
			if ((page.Content?.Handler?.PlatformView as Android.Views.View)?.Top > 0) return;
			Android.Views.ViewTreeObserver observer = view.ViewTreeObserver!;
			int heldFrames = 0;
			void DrawWhenPadded(object? sender, Android.Views.ViewTreeObserver.PreDrawEventArgs e)
			{
				bool padded = view.PaddingTop > 0 && (page.Content?.Handler?.PlatformView as Android.Views.View)?.Top >= view.PaddingTop;
				if (!padded && heldFrames == 0) view.RootView!.RequestApplyInsets();
				e.Handled = padded || ++heldFrames > 3;   // False skips the draw.
				if (e.Handled) observer.PreDraw -= DrawWhenPadded;
			}
			observer.PreDraw += DrawWhenPadded;
		};
#endif
	}

	/// <summary>
	/// Tracks the input whose outer border and focus ring must stay visible above the keyboard.
	/// Page constructors with scrollable inputs opt in alongside keyboard safe-area setup. On both platforms, the page's
	/// SoftInput edge handles the keyboard inset, so MAUI's automatic keyboard scrolling does not run.
	/// Hide the tab bar while an input has focus so its Auto row does not cover content above the keyboard.
	/// Scroll on focus and again when the inset resizes the scroll view, without a fixed delay.
	/// </summary>
	// A popup can dismiss this input's keyboard without walking the page tree.
	internal static InputView? FocusedEntry;

	// Popups can skip the tree walk on pages that already track their inputs.
	internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContentPage, object> TrackedPages = new();

	internal static void KeepFocusedFieldVisible(View scroll, View? primary = null)
	{
		InputView? focused = null;

		// The root grid hosts both the tab bar and the bottom sheet's scroll view.
		void ShowChrome(bool show)
		{
			Element? root = scroll;
			while (root?.Parent is not null and not ContentPage) { root = root.Parent; }
			if (root is not Layout layout) { return; }
			foreach (IView child in layout.Children)
			{
				if (child is Pages.TabsFooter) { ((VisualElement)child).IsVisible = show; }
			}
		}

		void ScrollToField()
		{
			if (focused is null || scroll is not ScrollView view) { return; }
			VisualElement? field = null;
			for (Element? el = focused.Parent; el is not null && el != view.Content; el = el.Parent)
			{
				if (el is Border) { field = (Border)el; }
			}
			// Scroll vertically only; on iOS, scrolling to the element also applies its X offset.
			if (field is null) { return; }
			double y = view.GetScrollPositionForElement(field, ScrollToPosition.MakeVisible).Y;
			// Keep the primary action visible with the field if both fit. Compare their positions in scroll-content coordinates.
			if (primary is not null)
			{
				double buttonY = view.GetScrollPositionForElement(primary, ScrollToPosition.MakeVisible).Y;
				if (view.GetScrollPositionForElement(field, ScrollToPosition.Start).Y >= buttonY) { y = buttonY; }
			}
			_ = view.ScrollToAsync(0, y, true);
		}

		// Hook each input only once, even if it appears more than once in the visual tree.
		System.Runtime.CompilerServices.ConditionalWeakTable<InputView, object> hooked = new();
		void HookField(InputView entry)
		{
			if (!hooked.TryAdd(entry, entry)) { return; }
			entry.Focused += (_, _) => { focused = FocusedEntry = entry; ShowChrome(false); scroll.Dispatcher.Dispatch(ScrollToField); };
			// Defer showing the tab bar so it does not flash when focus moves between inputs.
			entry.Unfocused += (_, _) =>
			{
				if (focused == entry) { focused = null; }
				if (FocusedEntry == entry) { FocusedEntry = null; }
				scroll.Dispatcher.Dispatch(() => { if (focused is null) { ShowChrome(true); } });
			};
		}

		void Hook(IVisualTreeElement element)
		{
			if (element is Entry or Editor) { HookField((InputView)element); }
			foreach (IVisualTreeElement child in element.GetVisualChildren()) { Hook(child); }
		}

		// Walk the visual tree only on the first Loaded; a retained tab raises it on each later attach.
		void HookOnce(object? sender, EventArgs e)
		{
			scroll.Loaded -= HookOnce;
			for (Element? el = scroll; el is not null; el = el.Parent)
			{
				if (el is ContentPage page) { TrackedPages.AddOrUpdate(page, page); break; }
			}
			Hook(scroll);
		}

		scroll.SizeChanged += (_, _) => scroll.Dispatcher.Dispatch(ScrollToField);
		scroll.Loaded += HookOnce;
	}

	internal static T Res<T>(string key) => (T)Current!.Resources[key];

	public static void LoadPreferLanguage()
	{
		string PreferLanguage = Preferences.Get("PreferLanguage", System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);

		if (string.IsNullOrEmpty(PreferLanguage) == false)
		{
			Languages.i18n.setLanguage(PreferLanguage);
		}
	}

	public static void SavePreferLanguage(string newLanguage)
	{
		Languages.i18n.setLanguage(newLanguage);
		Preferences.Set("PreferLanguage", Languages.i18n.defaultLanguageCode);
	}

	/// <summary>
	/// Matches system bar icons to the effective theme, including OS theme changes, and paints both bars through Android 14.
	/// Android needs a live activity, so first apply this when the window activates.
	/// iOS view controllers use their default status bar style, which follows the interface style. MAUI only sets the
	/// current controller's style, so set the window's style from UserAppTheme too. System stays unspecified so OS changes still apply.
	/// </summary>
	internal static void SetSystemBarIcons()
	{
		bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
#if IOS
		try
		{
			if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is UIKit.UIWindow window)
				window.OverrideUserInterfaceStyle = Application.Current.UserAppTheme switch
				{
					AppTheme.Light => UIKit.UIUserInterfaceStyle.Light,
					AppTheme.Dark => UIKit.UIUserInterfaceStyle.Dark,
					_ => UIKit.UIUserInterfaceStyle.Unspecified,
				};
		}
		catch { /* A cosmetic update must not break the page. */ }
#endif
#if ANDROID
		try
		{
			if (Platform.CurrentActivity?.Window is not { } window) return;
			if (AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView) is not { } controller) return;
			controller.AppearanceLightStatusBars = !dark;
			controller.AppearanceLightNavigationBars = !dark;

			// Through Android 14, the bars are opaque (colorPrimaryDark above and system gray below).
			// Match both backgrounds to the page so the icon colors stay readable.
			if (Application.Current is { } app
				&& app.Resources.TryGetValue(dark ? "BgDark" : "Bg", out object? band)
				&& band is Color bandColor)
				PaintSystemBars(window, bandColor);
		}
		catch { /* A cosmetic update must not break the page. */ }
#endif
	}
#if ANDROID

	// API 35+ draws the page behind the bars. On older versions, a bottom sheet can supply its own navigation bar color.
	internal static void PaintSystemBars(Android.Views.Window window, Color band, Color? navigationBand = null)
	{
		if (OperatingSystem.IsAndroidVersionAtLeast(35)) return;
		window.SetStatusBarColor(Microsoft.Maui.Platform.ColorExtensions.ToPlatform(band));
		window.SetNavigationBarColor(Microsoft.Maui.Platform.ColorExtensions.ToPlatform(navigationBand ?? band));
	}
#endif

	protected override Window CreateWindow(IActivationState? activationState)
	{
		AppShell shell = new();
		Window window = new(shell);
		shell.BindWindow(window);
		shell.Loaded += ReaderShellLoaded;
		if (shell.IsLoaded) { ReaderShellLoaded(shell, EventArgs.Empty); }
		window.Activated += (_, _) => SetSystemBarIcons();   // The root page appears before the Android activity is ready.
		// Keep SSH alive for up to 15 minutes in the background, unless the OS ends it sooner. Reconnect on return if needed.
		// Mark agents as backgrounded before pausing and foregrounded after resuming, so they do not draw or mark replies seen while away.
		window.Stopped += (_, _) =>
		{
			Dictation.Background();
			Herdr.Agents.Foreground = false;
			Herdr.EnterBackground();
		};
		window.Resumed += (_, _) =>
		{
			Dictation.Foreground();
			Herdr.LeaveBackground();
			Herdr.Agents.Foreground = true;
		};
		return window;
	}

	private static bool _openingReadingSource;

	internal static async Task OpenReadingSourceAsync(long generation, AppShell? expectedOwner = null, Func<bool>? canStart = null)
	{
		if (_openingReadingSource || generation != Reading.Generation || Reading.IsPreview || (!Reading.IsReading && !Reading.CanContinue)) { return; }
		bool guarded = expectedOwner is not null || canStart is not null;
		string? paneId = Reading.PaneId, machine = Reading.Machine;
		bool LifetimeCurrent() => canStart?.Invoke() == true && ReferenceEquals(expectedOwner, CurrentShell)
			&& generation == Reading.Generation && paneId == Reading.PaneId && machine == Reading.Machine
			&& !Reading.IsPreview && (Reading.IsReading || Reading.CanContinue);
		bool CanStartSource() => LifetimeCurrent() && (paneId is null || Herdr.State == Remote.SessionState.Connected
			&& machine == Remote.HerdrSession.MachineKey && Herdr.Agents.IsLive(paneId));
		ContentPage? page = null;
		Layout? layout = null;
		if (guarded)
		{
			if (expectedOwner is null || canStart is null || !LifetimeCurrent()
				|| expectedOwner.CurrentPage is not ContentPage { Content: Layout originLayout } originPage) { return; }
			page = originPage;
			layout = originLayout;
		}
		Func<ContentPage, Layout, bool>? canInsert = guarded
			? (actualPage, actualLayout) => LifetimeCurrent() && ReferenceEquals(page, actualPage) && ReferenceEquals(layout, actualLayout)
			: null;
		_openingReadingSource = true;
		try
		{
			if (paneId is null)
			{
				if (guarded) { await expectedOwner!.NavigateAsync(() => expectedOwner.GoToAsync("//Reader"), canStart: CanStartSource); }
				else { await GoToAsync("//Reader"); }
				return;
			}
			AppShell? owner = guarded ? expectedOwner : CurrentShell as AppShell;
			if (Herdr.State != Remote.SessionState.Connected || machine != Remote.HerdrSession.MachineKey
				|| !Herdr.Agents.IsLive(paneId) || owner is null)
			{
				await Pages.BusyRunner.Show("Read", Languages.i18n.Translate("Reading source is unavailable."), CanInsert: canInsert);
				return;
			}
			await owner.OpenPaneAsync(paneId, selectHerdr: true, expectedMachine: machine, canStart: guarded ? CanStartSource : null);
		}
		catch (OperationCanceledException) when (guarded) { }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[App] reading source: {ex.GetType().Name}");
			await Pages.BusyRunner.Show("Read", Languages.i18n.Translate("Reading source is unavailable."), CanInsert: canInsert);
		}
		finally { _openingReadingSource = false; }
	}

	private static bool _pendingReader;

	internal static void OpenReaderFromMedia() => MainThread.BeginInvokeOnMainThread(() =>
	{
		_pendingReader = true;
		if (CurrentShell is { IsLoaded: true } shell) { ReaderShellLoaded(shell, EventArgs.Empty); }
	});

	private static async void ReaderShellLoaded(object? sender, EventArgs e)
	{
		if (sender is not Shell shell || !shell.IsLoaded) { return; }
		shell.Loaded -= ReaderShellLoaded;
		if (!_pendingReader) { return; }
		_pendingReader = false;
		try { await GoToAsync("//Reader"); }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] media navigation failed: {ex.GetType().Name}"); }
	}

	// Only the last notification tap is kept while disconnected; open it after connecting.
	private static (AppShell Owner, string Address, string Machine)? _pendingPane;

	// Open the pane immediately when connected; otherwise queue the tap and start or join a connection.
	// A notification without a machine key belongs to the current host. A different host's notification opens only the list.
	internal static Task OpenPaneFromNotification(string paneId, string? machine)
	{
		if (CurrentShell is not AppShell owner) { return Task.CompletedTask; }
		string currentMachine = Remote.HerdrSession.MachineKey;
		if (machine is not null && machine != currentMachine) { return owner.NavigateAsync(() => owner.GoToAsync("//Herdr")); }
		string expectedMachine = machine ?? currentMachine;
		if (Herdr.State == Remote.SessionState.Connected) { return OpenNotificationPaneAsync(owner, paneId, expectedMachine); }
		if (_pendingPane is null) { Herdr.StateChanged += OpenPendingPane; }
		_pendingPane = (owner, paneId, expectedMachine);
		if (Remote.RemoteSettings.IsConfigured) { _ = Herdr.ConnectAsync(CancellationToken.None); }
		return Task.CompletedTask;
	}

	private static void OpenPendingPane()
	{
		if (Herdr.State != Remote.SessionState.Connected || _pendingPane is not { } pending) { return; }
		Herdr.StateChanged -= OpenPendingPane;
		_pendingPane = null;
		if (pending.Machine == Remote.HerdrSession.MachineKey) { _ = OpenNotificationPaneAsync(pending.Owner, pending.Address, pending.Machine); }
	}

	internal static void CancelPendingPane(AppShell owner)
	{
		if (_pendingPane is not { } pending || pending.Owner != owner) { return; }
		Herdr.StateChanged -= OpenPendingPane;
		_pendingPane = null;
	}

	internal static Task OpenPaneAsync(string paneId, string? find = null) => CurrentShell is AppShell owner ? owner.OpenPaneAsync(paneId, find: find) : Task.CompletedTask;

	private static async Task OpenNotificationPaneAsync(AppShell owner, string paneId, string machine)
	{
		System.Diagnostics.Debug.WriteLine($"[A07] t={System.Diagnostics.Stopwatch.GetTimestamp()} route=notification address={paneId}");
		try { await owner.OpenPaneAsync(paneId, selectHerdr: true, expectedMachine: machine); }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] open pane from notification unexpected: {ex}"); }
	}

	// Shell.Current throws when two Android windows exist and neither is active (dotnet/maui #35108, #35109).
	// Prefer the newest active window with a Shell, then the newest window with a Shell. Page-local operations use their own window.
	internal static Shell? CurrentShell
	{
		get
		{
			if (Current is not { } app || app.Windows.Count == 0) return null;
			IReadOnlyList<Window> windows = app.Windows;
			for (int i = windows.Count - 1; i >= 0; i--)
				if (windows[i].IsActivated && windows[i].Page is Shell active) return active;
			for (int i = windows.Count - 1; i >= 0; i--)
				if (windows[i].Page is Shell root) return root;
			return null;
		}
	}

	internal static Task GoToAsync(ShellNavigationState state, IDictionary<string, object>? parameters = null)
	{
		if (CurrentShell is not { } shell) return Task.CompletedTask;
		Task Navigate() => parameters is null ? shell.GoToAsync(state) : shell.GoToAsync(state, parameters);
		return shell is AppShell owner ? owner.NavigateAsync(Navigate) : Navigate();
	}
}
