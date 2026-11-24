namespace HerdrRemote;

public partial class AppShell : Shell
{
	private static readonly SemaphoreSlim NavigationGate = new(1, 1);
	private Pages.Pane? _pane;
	private string? _paneMachine;
	private IMauiContext? _paneContext;
	private long _paneLease;
	private Window? _ownerWindow;
	private bool _destroyed;
	private bool _navigating;
	private bool _openingPane;
	private bool _paneIdle;
	private bool _evictOnReturn;

	public AppShell()
	{
		InitializeComponent();
		Navigated += (_, _) => ObservePane();
		// Connection opens on the current tab from the workspace header or card, or from Settings. The card can request password focus.
		Routing.RegisterRoute("Connection", typeof(Pages.Connection));
		// Settings opens this route from "Providers and keys".
		Routing.RegisterRoute("Providers", typeof(Pages.Providers));
		Routing.RegisterRoute("Provider", typeof(Pages.Provider));
		Routing.RegisterRoute("Dictation", typeof(Pages.Dictation));
		Routing.RegisterRoute("SearchAll", typeof(Pages.SearchAll));
		Routing.RegisterRoute("Shortcuts", typeof(Pages.ShortcutsPage));
	}

	internal void BindWindow(Window window)
	{
		_ownerWindow = window;
		window.Destroying += Owner_Destroying;
		App.Herdr.StateChanged += Owner_StateChanged;
	}

	// Serialize app navigation, including page instances. Nested operations call Shell or INavigation directly to avoid taking the gate twice.
	internal async Task NavigateAsync(Func<Task> navigate, string? expectedMachine = null, Func<bool>? canStart = null)
	{
		await NavigationGate.WaitAsync();
		try
		{
			EnsureOwner(machine: expectedMachine);
			if (canStart?.Invoke() == false) { throw new OperationCanceledException(); }
			_navigating = true;
			await navigate();
		}
		finally
		{
			_navigating = false;
			try { ObservePane(); }
			finally { NavigationGate.Release(); }
		}
	}

	internal Task OpenPaneAsync(string address, bool selectHerdr = false, string? expectedMachine = null, Func<bool>? canStart = null, string? find = null)
	{
		System.Diagnostics.Debug.WriteLine($"[A07] t={System.Diagnostics.Stopwatch.GetTimestamp()} route={CurrentState?.Location} selectHerdr={selectHerdr} address={address}");
		string machine = expectedMachine ?? Remote.HerdrSession.MachineKey;
		return NavigateAsync(async () =>
		{
			_openingPane = true;
			try
			{
				IMauiContext? context = _ownerWindow?.Handler?.MauiContext;
				if (_pane is not null && (context != _paneContext || machine != _paneMachine)) { await RetirePaneCoreAsync(); }
				if (context is null) { throw new OperationCanceledException("The owning window has no native context."); }
				if (_pane is not null && FindPaneNavigation(activate: true) is { } stacked)
				{
					while (stacked.NavigationStack[^1] != _pane) { await stacked.PopAsync(); EnsureOwner(context, machine); }
				}
				else
				{
					if (selectHerdr) { await GoToAsync("//Herdr"); EnsureOwner(context, machine); }
					if (_pane is not null && !_paneIdle) { await _pane.WaitForHostDetachAsync(); EnsureOwner(context, machine); ObservePane(); }
					if (_pane is null)
					{
						_pane = new Pages.Pane();
						_paneMachine = machine;
						_paneContext = context;
						_pane.BindOwner(this, context);
						_pane.HostDetached += ObservePane;
					}
				}
				Pages.Pane pane = _pane!;
				_paneIdle = false;
				if (pane.Address != address || !pane.LeaseActive)
				{
					if (pane.LeaseActive) { pane.Park(++_paneLease); }
					await pane.WaitForParkingAsync();
					EnsureOwner(context, machine);
					pane.Prepare(address, ++_paneLease);
					System.Diagnostics.Debug.WriteLine($"[A07] t={System.Diagnostics.Stopwatch.GetTimestamp()} branch=prepared address={address} lease={_paneLease}");
				}
#if DEBUG
				else
				{
					System.Diagnostics.Debug.WriteLine($"[A07] t={System.Diagnostics.Stopwatch.GetTimestamp()} branch=retained address={address} lease={_paneLease}");
				}
#endif
				if (FindPaneNavigation() is null) { await Navigation.PushAsync(pane); EnsureOwner(context, machine); }
				_openingPane = false;
				if (find is not null) { pane.FindOnOpen(find); }
				pane.ActivateLease();
			}
			finally { _openingPane = false; }
		}, expectedMachine: machine, canStart: canStart);
	}

	private INavigation? FindPaneNavigation(bool activate = false)
	{
		if (_pane is null) { return null; }
		if (Navigation.NavigationStack.Contains(_pane)) { return Navigation; }
		foreach (ShellItem item in Items)
			foreach (ShellSection section in item.Items)
				if (section.Navigation.NavigationStack.Contains(_pane))
				{
					if (activate) { CurrentItem = item; item.CurrentItem = section; }
					return section.Navigation;
				}
		return null;
	}

	private void EnsureOwner(IMauiContext? expected = null, string? machine = null)
	{
		if (_destroyed || _ownerWindow is null || (expected is not null && _ownerWindow.Handler?.MauiContext != expected)
			|| (machine is not null && machine != Remote.HerdrSession.MachineKey))
			throw new OperationCanceledException("The intended window is no longer available.");
	}

	private void ObservePane()
	{
		_paneIdle = _pane is not null && !_navigating && !_pane.IsParking && FindPaneNavigation() is null && !_pane.IsHostAttached;
		if (_paneIdle && _evictOnReturn) { ReleasePane(); }
	}

	internal void PaneAppeared(Pages.Pane pane)
	{
		if (_pane != pane || _destroyed || _openingPane) { return; }
		_ = ResumePaneAsync(pane);
	}

	private async Task ResumePaneAsync(Pages.Pane pane)
	{
		try
		{
			await NavigateAsync(async () =>
			{
				if (_pane != pane || FindPaneNavigation() is null) { return; }
				await pane.WaitForParkingAsync();
				EnsureOwner(_paneContext, _paneMachine);
				if (!pane.LeaseActive) { pane.Prepare(pane.Address, ++_paneLease); }
				pane.ActivateLease();
			});
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Pane] returning to terminal failed: {ex}"); }
	}

	internal void PaneDisappeared(Pages.Pane pane)
	{
		if (_pane == pane) { pane.Park(++_paneLease); }
	}

	private async Task RetirePaneCoreAsync()
	{
		if (_pane is not { } pane) { return; }
		pane.Park(++_paneLease);
		if (FindPaneNavigation() is { } navigation)
		{
			if (navigation.NavigationStack[^1] == pane) { await navigation.PopAsync(false); }
			else { navigation.RemovePage(pane); }
		}
		pane.DetachHost();
		await pane.WaitForHostDetachAsync();
		await pane.WaitForParkingAsync();
		ReleasePane();
	}

	private void ReleasePane()
	{
		if (_pane is not { } pane) { return; }
		pane.HostDetached -= ObservePane;
		try { pane.Release(); }
		finally
		{
			_pane = null;
			_paneContext = null;
			_paneMachine = null;
			_paneIdle = false;
			_evictOnReturn = false;
		}
	}

	private void Owner_StateChanged()
	{
		if (App.Herdr.State == Remote.SessionState.DisconnectedByOwner || (_pane is not null && _paneMachine != Remote.HerdrSession.MachineKey)) { _ = RetirePaneAsync(); }
	}

	internal async Task RetirePaneAsync()
	{
		try { await NavigateAsync(RetirePaneCoreAsync); }
		catch (OperationCanceledException) { }
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Pane] retiring terminal failed: {ex}"); }
	}

	internal void TrimRetainedPane(bool pressure)
	{
		ObservePane();
		if (_paneIdle) { ReleasePane(); }
		else if (pressure) { _evictOnReturn = true; }
	}

	private void Owner_Destroying(object? sender, EventArgs e) => DestroyOwner();

	internal void DestroyOwner()
	{
		if (_destroyed) { return; }
		_destroyed = true;
		if (_ownerWindow is { } window) { window.Destroying -= Owner_Destroying; }
		App.Herdr.StateChanged -= Owner_StateChanged;
		App.CancelPendingPane(this);
		try
		{
			if (_pane is { } pane)
			{
				try { pane.Park(++_paneLease); pane.DetachHost(); }
				finally { ReleasePane(); }
			}
		}
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Pane] releasing destroyed window failed: {ex}"); }
		finally { _ownerWindow = null; }
	}

	// Use on-screen navigation on both platforms. At the workspace root with no pushed page or popup, Android Back
	// sends the app to the background without closing it.
	protected override bool OnBackButtonPressed()
	{
#if ANDROID
		if (CurrentPage is Pages.Herdr { Content: Layout layout }
			&& Navigation.NavigationStack.Count <= 1 && Navigation.ModalStack.Count == 0
			&& !layout.Children.Any(c => c is Popups.BottomSheet or Popups.DisplayMsg))
		{
			Platform.CurrentActivity?.MoveTaskToBack(true);
		}
#endif
		return true;
	}
}
