namespace Popups;

// Shared slide animation for bottom sheets hosted by PopupsHandle.PopBottomSheetAsync on both platforms.
public partial class BottomSheet : ContentView
{
	private const double MaxHeightRatio = 0.92;
	private bool _sized;
	private bool _shown;
	private bool _closing;
	private bool _dragging;
	private int _panGeneration;
	private readonly Thickness _padding;   // the Sht style's padding
	private double _band;   // the bottom band the panel's padding holds now

	public event EventHandler? Closed;
	public event EventHandler? Closing;
	public bool IsClosing => _closing;
	// Raised once the slide-up has finished (not for a sheet closed before it did): a field takes the keyboard then.
	public event EventHandler? Shown;

	public BottomSheet(ContentView body)
	{
		InitializeComponent();
		_padding = Panel.Padding;
		Panel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Y)) { AlignDragZone(); } };
		Slot.Content = body;
	}

	// Keep the panel below full-page height and follow keyboard resizing. SafeAreaEdges=All leaves a bottom
	// band for the home indicator or Android 15+ navigation bar; extend the panel into it and pad its content above it.
	// Layouts allow overflow, and PaintHostPage disables Android padding clipping while the sheet is open.
	// Measure against the window because pages fill it without Shell chrome and Host fills the page content.
	private void Host_SizeChanged(object? sender, EventArgs e)
	{
		if (Host.Height <= 0) { return; }
		double band = Window is { Height: > 0 } window && Parent is View root ? Math.Max(0, window.Height - root.Frame.Bottom) : 0;
		Panel.Margin = new Thickness(0, 0, 0, -band);
		// Keep the body's bottom clearance while the physical band changes.
		Panel.Padding = new Thickness(_padding.Left, _padding.Top, _padding.Right, Panel.Padding.Bottom - _band + band);
		Panel.MaximumHeightRequest = Host.Height * MaxHeightRatio + band;
		// A height fitted to the content (Panel_SizeChanged) holds the band of its time: it follows the band's change (rotation, navigation bar).
		if (Panel.HeightRequest >= 0) { Panel.HeightRequest += band - _band; }
		_band = band;
	}

	// The panel takes its content's height (never more than the maximum: then the body scrolls). Once it has that size it slides up from the bottom while the page dims.
	private async void Panel_SizeChanged(object? sender, EventArgs e)
	{
		if (_closing || _shown || Panel.Height <= 0 || Panel.Content is not View content) { return; }
		if (!_sized)
		{
			_sized = true;
			double wanted = content.Measure(Panel.Width, double.PositiveInfinity).Height + Panel.Padding.VerticalThickness;
			if (wanted < Panel.Height - 1) { Panel.HeightRequest = wanted; return; }
		}
		_shown = true;
		Panel.TranslationY = Panel.Height;
		Panel.Opacity = 1;
		await Task.WhenAll(Panel.TranslateToAsync(0, 0, 400, Easing.CubicOut), Scrim.FadeToAsync(1, 400, Easing.CubicOut));
		if (_closing) { return; }
		AlignDragZone();
		DragZone.IsVisible = true;
		Shown?.Invoke(this, EventArgs.Empty);
	}

	private void AlignDragZone()
	{
		if (!_shown || _closing || _dragging) { return; }
		if (DragZone.TranslationY != Panel.Y) { DragZone.TranslationY = Panel.Y; }
	}

	// The panel fitted again to its content, after the body changed it while open (rows arriving, a note or an error line shown or hidden).
	// Before the first fit there is nothing to do: that fit measures the content as it is then.
	// Explicit search focus/blur releases the keyboard-time fit; other callers still fit their content.
	public void Refit(bool? fill = null)
	{
		if (!_sized || Panel.Width <= 0 || Panel.Content is not View content) { return; }
		Panel.Padding = new Thickness(_padding.Left, _padding.Top, _padding.Right, (fill == true ? 0 : _padding.Bottom) + _band);
		Panel.HeightRequest = fill.HasValue ? -1 : content.Measure(Panel.Width, double.PositiveInfinity).Height + Panel.Padding.VerticalThickness;
	}

	// Dragging down moves only the panel. Released past a quarter of its height the sheet closes, otherwise it slides back.
	private async void DragZone_PanUpdated(object? sender, PanUpdatedEventArgs e)
	{
		if (_closing) { return; }
		switch (e.StatusType)
		{
			case GestureStatus.Started:
				_panGeneration++;
				_dragging = true;
				break;
			case GestureStatus.Running:
				Panel.TranslationY = Math.Max(0, e.TotalY);
				break;
			case GestureStatus.Completed when Panel.TranslationY > Panel.Height * 0.25:
				Close();
				break;
			case GestureStatus.Completed or GestureStatus.Canceled:
				int mine = _panGeneration;
				try { await Panel.TranslateToAsync(0, 0, 200, Easing.CubicOut); }
				finally
				{
					// A newer pan owns the stationary origin while this snap-back completes.
					if (mine == _panGeneration)
					{
						_dragging = false;
						AlignDragZone();
					}
				}
				break;
		}
	}

	private void Scrim_Tapped(object? sender, TappedEventArgs e) => Close();

	// Slides down, then tells the host. Idempotent: a second tap during the slide does nothing.
	public void Close() => Close(null);

	internal async void Close(Action? accept)
	{
		if (_closing) { return; }
		_closing = true;
		accept?.Invoke();
		// Start hiding while the field still owns native focus; Closing/body transparency can transfer it on Android.
		double height = Host.Height;
		Task<bool>? hideKeyboard = this.GetVisualTreeDescendants().OfType<InputView>()
			.FirstOrDefault(entry => entry is Entry or Editor && entry.IsFocused)?.HideSoftInputAsync(CancellationToken.None);
		Closing?.Invoke(this, EventArgs.Empty);
		Slot.InputTransparent = true;
		_dragging = false;
		DragZone.IsVisible = false;
		// Hide the keyboard and wait for the page to regain its size before removing the sheet; Android can otherwise keep the shrunken layout.
		// Do not unfocus: that can focus the page's first field and reopen the keyboard.
		if (hideKeyboard is not null)
		{
			// HideSoftInputAsync can return before layout recovers, observed about 430 ms later on Android.
			// Wait for SizeChanged; bound the wait for hardware keyboards, where the page never resizes.
			TaskCompletionSource grown = new(TaskCreationOptions.RunContinuationsAsynchronously);
			void OnResized(object? s, EventArgs e) { if (Host.Height > height) { grown.TrySetResult(); } }
			Host.SizeChanged += OnResized;
			try
			{
				await hideKeyboard;
				if (Host.Height <= height) { await grown.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true); }
			}
			catch (TimeoutException) { }
			finally { Host.SizeChanged -= OnResized; }
		}
		await Task.WhenAll(Panel.TranslateToAsync(0, Panel.Height, 300, Easing.CubicIn), Scrim.FadeToAsync(0, 300, Easing.CubicIn));
		Closed?.Invoke(this, EventArgs.Empty);
	}
}
