using System.Globalization;
using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Only the visible host subscribes; a sheet temporarily replaces its page's bar, then restores it.
public partial class ReadingBar : ContentView
{
	private static ReadingBar? _owner;
	private ReadingBar? _covered;
	private readonly ReadingSession _session = App.Reading;
	private Window? _window;
	private bool _attached, _foreground = true;
	private long _interactionEpoch;
	private bool _openingSource;
	private (long, string, int, int, float, float, bool, string)? _shown;
	internal Func<bool>? CanInteract { get; set; }
	internal Func<ContentPage, Task>? BeforeOpenSource { get; set; }

	public ReadingBar()
	{
		InitializeComponent();
		Loaded += (_, _) => { if (_attached && _owner == this) { Attach(); } };
		Unloaded += (_, _) => Detach();
	}

	public void Attach()
	{
		_attached = true;
		if (_owner == this)
		{
			if (ReferenceEquals(_window, Window)) { return; }
			Suspend();
		}
		else
		{
			_covered = _owner;
			_owner?.Suspend();
			_owner = this;
		}
		_interactionEpoch++;
		_window = Window;
		_foreground = App.Herdr.Agents.Foreground;
		if (_window is { } window) { window.Stopped += Window_Stopped; window.Resumed += Window_Resumed; }
		if (_foreground) { _session.Changed += Show; }
		_shown = null;
		Show();
	}

	public void Detach()
	{
		_interactionEpoch++;
		_shown = null;
		_attached = false;
		if (_owner != this) { return; }
		Suspend();
		_owner = null;
		ReadingBar? covered = _covered;
		_covered = null;
		if (covered?._attached == true) { covered.Attach(); }
	}

	private void Suspend()
	{
		_interactionEpoch++;
		_shown = null;
		_session.Changed -= Show;
		if (_window is { } window) { window.Stopped -= Window_Stopped; window.Resumed -= Window_Resumed; }
		_window = null;
		IsVisible = false;
	}

	private void Window_Stopped(object? sender, EventArgs e)
	{
		_interactionEpoch++;
		_shown = null;
		_foreground = false;
		_session.Changed -= Show;
	}

	private void Window_Resumed(object? sender, EventArgs e)
	{
		_foreground = true;
		if (_owner != this || !_attached) { return; }
		_session.Changed -= Show;
		_session.Changed += Show;
		_shown = null;
		Show();
	}

	private bool Live => _owner == this && _attached && _foreground && App.Herdr.Agents.Foreground
		&& _window is not null && ReferenceEquals(Window, _window) && CanInteract?.Invoke() != false;

	private bool TryAdmit(out long epoch, out long generation)
	{
		epoch = _interactionEpoch;
		generation = _session.Generation;
		return Live && _shown?.Item1 == generation;
	}

	private void Show()
	{
		if (!Live) { return; }
		if (!_session.IsPreview && _session.TakeError() is { } error) { _ = BusyRunner.Show("Read", error); }
		if (_session.IsPreview || (!_session.IsReading && !_session.CanContinue)) { IsVisible = false; _shown = null; return; }
		var state = (_session.Generation, _session.Phase, _session.Sentence, _session.SentenceCount,
			_session.RequestedRate, _session.AppliedRate, _session.CanPause, _session.SourceName);
		if (_shown == state) { return; }
		_shown = state;
		IsVisible = true;
		Lbl_Source.Text = _session.SourceName;
		Lbl_Count.Text = $"{_session.Sentence + 1}/{_session.SentenceCount}";
		SemanticProperties.SetDescription(Lbl_Count, string.Format(i18n.Translate("Sentence {0} of {1}"), _session.Sentence + 1, _session.SentenceCount));
		SemanticProperties.SetDescription(Source, string.Format(i18n.Translate("Return to {0}"), _session.SourceName));
		SemanticProperties.SetHint(Source, i18n.Translate("Reads from the place you chose, and goes on as the terminal writes."));
		Btn_Pause.IsVisible = _session.CanPause || _session.CanContinue;
		Lbl_Pause.Text = _session.Paused || _session.CanContinue ? "\uE950" : "\uE951";
		SemanticProperties.SetDescription(Btn_Pause, i18n.Translate(_session.CanContinue ? "Continue from this sentence" : _session.Paused ? "Resume" : "Pause"));
		Btn_Previous.IsEnabled = _session.CanPrevious;
		Btn_Next.IsEnabled = _session.CanNext;
		Btn_Rate.IsEnabled = _session.IsReading;
		string rate = _session.AppliedRate.ToString("0.##", CultureInfo.InvariantCulture) + "x";
		Lbl_Rate.Text = rate + (_session.RatePending ? "*" : "");
		SemanticProperties.SetDescription(Btn_Rate, i18n.Translate("Playback speed") + " " + rate);
		SemanticProperties.SetHint(Btn_Rate, _session.RateNotice ?? "");
		SemanticProperties.SetDescription(Btn_Previous, i18n.Translate("Previous sentence"));
		SemanticProperties.SetDescription(Btn_Next, i18n.Translate("Next sentence"));
		SemanticProperties.SetDescription(Btn_Close, i18n.Translate("Stop reading"));
	}

	private async void Source_Tapped(object? sender, TappedEventArgs e)
	{
		if (_openingSource || !TryAdmit(out _, out long generation) || App.CurrentShell is not AppShell owner
			|| owner.CurrentPage is not ContentPage { Content: Layout layout } page || _window is not { } window
			|| !ReferenceEquals(owner.Window, window)) { return; }
		string? paneId = _session.PaneId, machine = _session.Machine;
		Func<ContentPage, Layout, bool>? paneCurrent = (page as Pane)?.CaptureInsertionGuard();
		bool invalidated = false;
		void Invalidate(object? s, EventArgs e) => invalidated = true;
		// The admitted Source may outlive its own sheet, but not a change of origin or reading.
		bool IntentCurrent() => !invalidated && _foreground && App.Herdr.Agents.Foreground
			&& ReferenceEquals(App.CurrentShell, owner) && ReferenceEquals(owner.Window, window) && ReferenceEquals(window.Page, owner)
			&& ReferenceEquals(owner.CurrentPage, page) && ReferenceEquals(page.Content, layout) && ReferenceEquals(page.Window, window)
			&& generation == _session.Generation && paneId == _session.PaneId && machine == _session.Machine
			&& !_session.IsPreview && (_session.IsReading || _session.CanContinue) && paneCurrent?.Invoke(page, layout) != false;
		_openingSource = true;
		window.Stopped += Invalidate;
		window.Destroying += Invalidate;
		page.Disappearing += Invalidate;
		owner.Navigating += Invalidate;
		try
		{
			if (BeforeOpenSource is { } dismiss) { await dismiss(page); }
			await App.OpenReadingSourceAsync(generation, owner, IntentCurrent);
		}
		finally
		{
			window.Stopped -= Invalidate;
			window.Destroying -= Invalidate;
			page.Disappearing -= Invalidate;
			owner.Navigating -= Invalidate;
			_openingSource = false;
		}
	}

	private async void Pause_Clicked(object? sender, EventArgs e)
	{
		if (!TryAdmit(out long epoch, out long generation)) { return; }
		if (_session.CanContinue)
		{
			string confirm = i18n.Translate("Continue from this sentence");
			if (await PopupsHandle.PopDisplayAsync(confirm, i18n.Translate("This sentence may repeat and use provider credits."), [confirm, i18n.Translate("Cancel")]) == confirm
				&& TryAdmit(out long currentEpoch, out long currentGeneration) && epoch == currentEpoch && generation == currentGeneration) { await _session.ContinueAsync(); }
		}
		else { _session.TogglePause(); }
	}
	private void Rate_Clicked(object? sender, EventArgs e) { if (TryAdmit(out _, out _)) { _session.CycleRate(); } }
	private void Previous_Clicked(object? sender, EventArgs e) { if (TryAdmit(out _, out _)) { _session.Previous(); } }
	private void Next_Clicked(object? sender, EventArgs e) { if (TryAdmit(out _, out _)) { _session.Next(); } }
	private void Close_Clicked(object? sender, EventArgs e) { if (TryAdmit(out _, out _)) { _session.Stop(); } }
}
