using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using HerdrRemote.Services;
using Popups;

namespace HerdrRemote;

// SingleTask: the app never runs twice (a second MainActivity made two MAUI windows and the Shell lookup threw, dotnet/maui #35108).
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = Android.Views.SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	private const string BatteryOnboarding = "android.background-permission-onboarding";
	private const string BatteryPendingResult = "background-permission-result-pending";
	private ActivityResultLauncher? _batteryRequest;
	private bool _requestInFlight, _resumed, _destroyed, _noticeAttempt, _noticeInserted;
	// Only the user's deferred affirmative survives Activity recreation, never process death or app-data storage.
	private static bool _explicitRequest;
	private Shell? _noticeShell;
	private ContentPage? _noticePage;
	private Layout? _noticeLayout;
	private Action? _dismissNotice;
	private AppShell? PaneOwner => Microsoft.Maui.Controls.Application.Current?.Windows
		.FirstOrDefault(window => ReferenceEquals(window.Handler?.PlatformView, this))?.Page as AppShell;

	// Notification taps arrive in OnCreate on a cold start and OnNewIntent when the SingleTask activity is running.
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		_requestInFlight = savedInstanceState?.GetBoolean(BatteryPendingResult) == true;
		// Always register in this order, before STARTED, so AndroidX can deliver a recreated Activity's pending result.
		_batteryRequest = RegisterForActivityResult(new ActivityResultContracts.StartActivityForResult(), new BatteryResult(this));
		OpenPane(Intent);
	}

	private sealed class BatteryResult(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
	{
		public void OnActivityResult(Java.Lang.Object? result)
		{
			if (!activity._requestInFlight) { return; }
			activity._requestInFlight = false;
			// RESULT_OK is not a grant; query PowerManager, only when this Activity is back in front.
			activity.AdvanceBatteryOnboarding();
		}
	}

	protected override void OnSaveInstanceState(Bundle outState)
	{
		outState.PutBoolean(BatteryPendingResult, _requestInFlight);
		base.OnSaveInstanceState(outState);
	}

	protected override void OnPostResume()
	{
		base.OnPostResume();
		_resumed = true;
		AdvanceBatteryOnboarding();
	}

	protected override void OnPause()
	{
		_resumed = false;
		UnwatchNoticeHost();
		base.OnPause();
	}

	protected override void OnStop()
	{
		_resumed = false;
		UnwatchNoticeHost();
		base.OnStop();
	}

	public override void OnTrimMemory(TrimMemory level)
	{
		base.OnTrimMemory(level);
		// UI_HIDDEN alone is not pressure and must not tear down a live held terminal.
		AppShell? owner = PaneOwner;
		MainThread.BeginInvokeOnMainThread(() => owner?.TrimRetainedPane(pressure: level != TrimMemory.UiHidden));
	}

	private bool CanRequest => _resumed && !_destroyed && !IsFinishing && !IsDestroyed;

	private bool? BatteryExempt()
	{
		if (GetSystemService(PowerService) is PowerManager power) { return power.IsIgnoringBatteryOptimizations(PackageName!); }
		System.Diagnostics.Debug.WriteLine("[Battery] PowerManager unavailable; exemption could not be queried");
		return null;
	}

	private void AdvanceBatteryOnboarding()
	{
		if (!CanRequest || _requestInFlight) { return; }
		try
		{
			if (_explicitRequest)
			{
				_explicitRequest = false;
				if (BatteryExempt() == false) { LaunchBatteryRequest(); }
				return;
			}
			string state = App.Setting(BatteryOnboarding, "");
			if (state is "notice-shown" or "granted" || _noticeInserted) { UnwatchNoticeHost(); return; }
			bool? exempt = BatteryExempt();
			if (exempt == true) { App.SaveSetting(BatteryOnboarding, "granted"); UnwatchNoticeHost(); return; }
			if (state.Length == 0)
			{
				App.SaveSetting(BatteryOnboarding, "requested"); // before launch: process death cannot repeat the initial request
				if (exempt is not null && LaunchBatteryRequest()) { return; }
			}
			if (_noticeAttempt) { return; }
			// Loaded.add can invoke synchronously on an already-loaded host.
			_noticeAttempt = true;
			WatchNoticeHost();
			if (PopupsHandle.GetCurrentPage() is { Content: Layout layout } page && page.Window is { } window
				&& NoticeHostEligible(page, layout, window)) { _ = ShowBatteryNoticeAsync(page, layout, window); }
			else { _noticeAttempt = false; }
		}
		catch (Exception ex) { _noticeAttempt = false; System.Diagnostics.Debug.WriteLine($"[Battery] onboarding failed: {ex}"); }
	}

	private bool LaunchBatteryRequest()
	{
		UnwatchNoticeHost();
		_requestInFlight = true;
		try
		{
			_batteryRequest!.Launch(new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
				Android.Net.Uri.Parse($"package:{PackageName}")));
			return true;
		}
		catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
		{
			_requestInFlight = false;
			System.Diagnostics.Debug.WriteLine($"[Battery] exemption request failed: {ex}");
			return false;
		}
	}

	private bool NoticeHostEligible(ContentPage page, Layout layout, Microsoft.Maui.Controls.Window window)
		=> CanRequest && !_requestInFlight && !_noticeInserted && App.Setting(BatteryOnboarding, "") == "requested"
			&& PopupsHandle.GetCurrentPage() == page && page.Content == layout && page.Window == window
			&& page.IsLoaded && layout.IsLoaded && window.Handler?.PlatformView == this
			&& !layout.Children.Any(child => child is DisplayMsg or BottomSheet);

	private async Task ShowBatteryNoticeAsync(ContentPage page, Layout layout, Microsoft.Maui.Controls.Window window, bool recheckHost = true)
	{
		bool saved = false;
		string give = Languages.i18n.Translate("Give permission");
		try
		{
			string? answer = await PopupsHandle.PopDisplayAsync(Languages.i18n.Translate("Background connection"),
				Languages.i18n.Translate("Without the battery permission, Android cuts the connection to your Mac about 5 seconds after the app goes to the background. It reconnects when you come back."),
				[give, Languages.i18n.Translate("Leave it")], Destructive: false, Handle: dismiss => _dismissNotice = dismiss,
				Shown: () =>
				{
					_noticeInserted = true;
					UnwatchNoticeHost();
					App.SaveSetting(BatteryOnboarding, "notice-shown");
					saved = true;
				},
				CanInsert: (actualPage, actualLayout) => actualPage == page && actualLayout == layout && NoticeHostEligible(page, layout, window));
			// A rejected insertion is not the displayed notice's Leave it. Failed storage cannot authorize a second request.
			if (saved && answer == give)
			{
				_explicitRequest = true;
				AdvanceBatteryOnboarding();
			}
		}
		finally
		{
			_dismissNotice = null;
			bool handedOff = false;
			try
			{
				if (!_noticeInserted && CanRequest)
				{
					WatchNoticeHost();
					if (recheckHost && PopupsHandle.GetCurrentPage() is { Content: Layout currentLayout } currentPage
						&& currentPage.Window is { } currentWindow && NoticeHostEligible(currentPage, currentLayout, currentWindow))
					{
						// Transfer the guard once: readiness may have completed during the rejected insertion.
						handedOff = true;
						_ = ShowBatteryNoticeAsync(currentPage, currentLayout, currentWindow, recheckHost: false);
					}
				}
			}
			finally { if (!handedOff) { _noticeAttempt = false; } }
		}
	}

	private void WatchNoticeHost()
	{
		UnwatchNoticeHost();
		_noticeShell = App.CurrentShell;
		_noticePage = PopupsHandle.GetCurrentPage();
		_noticeLayout = _noticePage?.Content as Layout;
		if (_noticeShell is not null) { _noticeShell.Navigated += NoticeNavigated; _noticeShell.Loaded += NoticeLoaded; }
		if (_noticePage is not null) { _noticePage.Loaded += NoticeLoaded; }
		if (_noticeLayout is not null) { _noticeLayout.Loaded += NoticeLoaded; _noticeLayout.ChildRemoved += NoticeChildRemoved; }
	}

	private void UnwatchNoticeHost()
	{
		if (_noticeShell is not null) { _noticeShell.Navigated -= NoticeNavigated; _noticeShell.Loaded -= NoticeLoaded; }
		if (_noticePage is not null) { _noticePage.Loaded -= NoticeLoaded; }
		if (_noticeLayout is not null) { _noticeLayout.Loaded -= NoticeLoaded; _noticeLayout.ChildRemoved -= NoticeChildRemoved; }
		_noticeShell = null;
		_noticePage = null;
		_noticeLayout = null;
	}

	private void NoticeLoaded(object? sender, EventArgs e) => AdvanceBatteryOnboarding();
	private void NoticeNavigated(object? sender, ShellNavigatedEventArgs e) => AdvanceBatteryOnboarding();
	private void NoticeChildRemoved(object? sender, ElementEventArgs e) => AdvanceBatteryOnboarding();

	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		OpenPane(intent);
	}

	private static void OpenPane(Intent? intent)
	{
		if (intent?.Action == ReadingService.ContentAction)
		{
			intent.SetAction(null);
			App.OpenReaderFromMedia();
			return;
		}
		if (intent?.GetStringExtra(AgentNotifier.PaneExtra) is { } paneId) { _ = App.OpenPaneFromNotification(paneId, intent.GetStringExtra(AgentNotifier.MachineExtra)); }
	}

	// Clear EditText focus before services are disposed. Otherwise Android's onDestroy triggers MAUI's
	// ViewHandler.OnPlatformViewFocusChange -> InputView.MapIsFocused and throws ObjectDisposedException.
	protected override void OnDestroy()
	{
		_destroyed = true;
		_resumed = false;
		UnwatchNoticeHost();
		_dismissNotice?.Invoke();
		CurrentFocus?.ClearFocus();
		PaneOwner?.DestroyOwner();
		base.OnDestroy();
	}
}
