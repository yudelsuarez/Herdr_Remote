using System.Diagnostics;
using HerdrRemote;
using HerdrRemote.Remote;
using Languages;

namespace Popups;

// Nonmodal attention on the current page; never goes through the keyboard-hiding popup host.
internal sealed class AgentToast : Grid
{
	private static AgentToast? _current;
	private static long _generation;
	private readonly AppShell _owner;
	private readonly Window _window;
	private readonly ContentPage _page;
	private readonly Grid _layout;
	private readonly string _machine;
	private readonly string _pane;
	private readonly long _id;
	private readonly CancellationTokenSource _timeout = new();
	private bool _dismissed;
	private Action? _removed;

	internal static void Show(string machine, string pane, string title, string body)
	{
		DismissInApp();
		if (App.CurrentShell is not AppShell owner || owner.Window is not { } window
			|| PopupsHandle.GetCurrentPage() is not { Content: Grid layout, Handler: not null } page
			|| !App.Herdr.Agents.Foreground || App.Herdr.State != SessionState.Connected
			|| machine != HerdrSession.MachineKey || !App.Herdr.Agents.Terminals.ContainsKey(pane)) { return; }
		AgentToast? toast = null;
		try
		{
			layout = page is HerdrRemote.Pages.Pane panePage ? panePage.NoticeHost : layout;
			toast = new AgentToast(owner, window, page, layout, machine, pane, title, body);
			_current = toast;
			Grid.SetRowSpan(toast, Math.Max(1, layout.RowDefinitions.Count));
			Grid.SetColumnSpan(toast, Math.Max(1, layout.ColumnDefinitions.Count));
			layout.Add(toast);
			page.Disappearing += toast.Departed;
			owner.Navigating += toast.Navigating;
			window.Stopped += toast.Departed;
			window.Destroying += toast.Departed;
			page.Unloaded += toast.Departed;
			page.HandlerChanged += toast.OnHandlerChanged;
			toast.Unloaded += toast.Departed;
			toast.HandlerChanged += toast.OnHandlerChanged;
			App.Herdr.StateChanged += toast.SessionChanged;
			App.Herdr.Agents.TerminalRetired += toast.Retired;
			AgentToast shown = toast;
			_ = Task.Delay(TimeSpan.FromSeconds(4), shown._timeout.Token).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() =>
			{
				if (_current == shown && !shown._dismissed && shown._id == _generation) { shown.Dismiss(); }
			}), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[AgentToast] show failed: {ex}");
			toast?.Dismiss();
		}
	}

	internal static void DismissInApp() => _current?.Dismiss();
	internal static void DismissForPane(string machine, string pane)
	{
		if (_current is { } toast && toast._machine == machine && toast._pane == pane) { toast.Dismiss(); }
	}

	private AgentToast(AppShell owner, Window window, ContentPage page, Grid layout, string machine, string pane, string title, string body)
	{
		_owner = owner;
		_window = window;
		_page = page;
		_layout = layout;
		_machine = machine;
		_pane = pane;
		_id = ++_generation;
		InputTransparent = true;
		CascadeInputTransparent = false;
		ZIndex = int.MaxValue;
		AutomationId = "AgentToast";
		Label heading = new() { Style = App.Res<Style>("Card.Title"), Text = title };
		Label detail = new() { Style = App.Res<Style>("Card.Body"), Text = body };
		AutomationProperties.SetIsInAccessibleTree(heading, false);
		AutomationProperties.SetIsInAccessibleTree(detail, false);
		VerticalStackLayout text = new() { Children = { heading, detail }, VerticalOptions = LayoutOptions.Center };
		SemanticProperties.SetDescription(text, $"{title}. {body}");
		SemanticProperties.SetHint(text, i18n.Translate("Open pane"));
		TapGestureRecognizer tap = new();
		tap.Tapped += (_, _) =>
		{
			if (_dismissed) { return; }
			Dismiss(() =>
			{
				if (_id == _generation && CurrentHost) { _ = App.OpenPaneFromNotification(_pane, _machine); }
			});
		};
		text.GestureRecognizers.Add(tap);
		Button close = new() { Style = App.Res<Style>("TrayKey.Icon"), Text = "\xE91D" };
		SemanticProperties.SetDescription(close, i18n.Translate("Close"));
		close.Clicked += (_, _) => Dismiss();
		Grid contents = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
		contents.Add(text);
		contents.Add(close, 1);
		Add(new Border
		{
			Style = App.Res<Style>("Badge"),
			HorizontalOptions = LayoutOptions.Fill,
			VerticalOptions = LayoutOptions.Start,
			Margin = App.Res<Thickness>("Gutter"),
			Content = contents,
		});
	}

	private bool CurrentHost => App.CurrentShell == _owner && _owner.Window == _window && _window.Page == _owner
		&& PopupsHandle.GetCurrentPage() == _page && _page.Handler is not null
		&& App.Herdr.Agents.Foreground && App.Herdr.State == SessionState.Connected
		&& _machine == HerdrSession.MachineKey && App.Herdr.Agents.Terminals.ContainsKey(_pane);

	private void Departed(object? sender, EventArgs e) => Dismiss();
	private void Navigating(object? sender, ShellNavigatingEventArgs e) => Dismiss();
	private void OnHandlerChanged(object? sender, EventArgs e) { if (_page.Handler is null || Handler is null) { Dismiss(); } }
	private void SessionChanged() { if (!CurrentHost) { Dismiss(); } }
	private void Retired(string pane) { if (pane == _pane) { Dismiss(); } }

	private void Dismiss(Action? removed = null)
	{
		if (_dismissed) { _removed = null; return; }
		_dismissed = true;
		_removed = removed;
		_timeout.Cancel();
		_timeout.Dispose();
		// Queue removal after native touch dispatch, just like PopupsHandle.RemoveQueued, without painting a modal host.
		void Remove()
		{
			try
			{
				_page.Disappearing -= Departed;
				_owner.Navigating -= Navigating;
				_window.Stopped -= Departed;
				_window.Destroying -= Departed;
				_page.Unloaded -= Departed;
				_page.HandlerChanged -= OnHandlerChanged;
				Unloaded -= Departed;
				HandlerChanged -= OnHandlerChanged;
				App.Herdr.StateChanged -= SessionChanged;
				App.Herdr.Agents.TerminalRetired -= Retired;
				if (_current == this) { _current = null; }
				Action? callback = _removed;
				_removed = null;
				_layout.Remove(this);
				callback?.Invoke();
			}
			catch (Exception ex) { Debug.WriteLine($"[AgentToast] remove failed: {ex}"); }
		}
		if (!_layout.Dispatcher.Dispatch(Remove)) { Remove(); }
	}
}
