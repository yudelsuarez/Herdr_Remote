namespace HerdrRemote.Pages;

// Hosts supply Start, Keys, and End as property elements. Keep one row at every width to preserve terminal space:
// [Start] * [End] without Keys, or Start · separator · Keys · separator · End with them.
// Rearrange on every window-size change.
public partial class Tray : Border
{
	public static readonly BindableProperty StartProperty = BindableProperty.Create(nameof(Start), typeof(View), typeof(Tray), null,
		propertyChanged: (bindable, _, value) => ((Tray)bindable).Fill(((Tray)bindable).StartSlot, (View?)value));

	public static readonly BindableProperty KeysProperty = BindableProperty.Create(nameof(Keys), typeof(View), typeof(Tray), null,
		propertyChanged: (bindable, _, value) =>
		{
			Tray tray = (Tray)bindable;
			tray.KeysSlot.Content = (View?)value;
			tray.Arrange();
		});

	public static readonly BindableProperty EndProperty = BindableProperty.Create(nameof(End), typeof(View), typeof(Tray), null,
		propertyChanged: (bindable, _, value) => ((Tray)bindable).Fill(((Tray)bindable).EndSlot, (View?)value));

	public Tray()
	{
		InitializeComponent();
	}

	// The exit first (the image viewer's close).
	public View? Start
	{
		get => (View?)GetValue(StartProperty);
		set => SetValue(StartProperty, value);
	}

	// The keys row (its own sideways scroll).
	public View? Keys
	{
		get => (View?)GetValue(KeysProperty);
		set => SetValue(KeysProperty, value);
	}

	// The controls, the screen's primary action last.
	public View? End
	{
		get => (View?)GetValue(EndProperty);
		set => SetValue(EndProperty, value);
	}

	private void Fill(HorizontalStackLayout slot, View? view)
	{
		slot.Children.Clear();
		if (view is not null) { slot.Children.Add(view); }
		Arrange();
	}

	private void Tray_SizeChanged(object? sender, EventArgs e) => Arrange();

	private void Arrange()
	{
		if (Window is not { Width: > 0 } window) { return; }
		bool keys = Keys is not null;
		KeysSlot.IsVisible = keys;
		StartSep.IsVisible = keys && Start is not null;
		EndSep.IsVisible = keys && End is not null;
		// Use the style's 8 dp bottom margin in portrait, or TrayGap (12 dp) in landscape.
		double gap = App.Res<double>("TrayGap");
		if (window.Width <= window.Height) { ClearValue(MarginProperty); }
		else if (Margin.Bottom != gap) { Margin = new Thickness(Margin.Left, Margin.Top, Margin.Right, gap); }
	}
}
