using HerdrRemote.Remote;
using Languages;

namespace HerdrRemote.Pages;

// Backspace and arrows send immediately, then after RepeatDelay and every RepeatEvery until released.
// Enter and Ctrl+C send once per tap. Drag the pad within its parent; RemoteSettings.DPadAt stores its position
// as fractions of available movement, keeping it visible after rotation or keyboard changes.
public partial class DPad : VerticalStackLayout
{
	private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
	private static readonly TimeSpan RepeatEvery = TimeSpan.FromMilliseconds(70);

	private IDispatcherTimer? _repeat;
	private string? _held;
	private View? _room;
	private double _startX;
	private double _startY;
	private IReadOnlyList<Corner>? _corners;
	private readonly Thickness _normalPadding;
	private readonly GridLength[] _normalRows, _normalColumns;
	private readonly (Button Key, int Row, int Column)[] _keys;
	private bool _compact;

	public DPad()
	{
		InitializeComponent();
		_normalPadding = Padding;
		_normalRows = Keys.RowDefinitions.Select(row => row.Height).ToArray();
		_normalColumns = Keys.ColumnDefinitions.Select(column => column.Width).ToArray();
		_keys = Keys.Children.Cast<Button>().Select(key => (key, Grid.GetRow(key), Grid.GetColumn(key))).ToArray();
		ApplyConfiguration();
		Loaded += (_, _) => { Shortcuts.Changed += ApplyConfiguration; ApplyConfiguration(); };
		Unloaded += (_, _) => { Shortcuts.Changed -= ApplyConfiguration; Repeat_Released(this, EventArgs.Empty); };
		PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsVisible) && !IsVisible) { Repeat_Released(this, EventArgs.Empty); } };
		SizeChanged += (_, _) => Place();
	}

	private void ApplyConfiguration()
	{
		IReadOnlyList<Corner> corners = Shortcuts.Current.Corners;
		if (_corners is not null && _corners.SequenceEqual(corners)) { return; }
		_corners = corners;
		Repeat_Released(this, EventArgs.Empty);
		foreach ((string position, Button key) in new[] { ("tl", Corner_TL), ("tr", Corner_TR), ("bl", Corner_BL), ("br", Corner_BR) })
		{
			Corner? corner = corners.FirstOrDefault(corner => corner.Position == position);
			key.BindingContext = corner;
			key.IsVisible = corner?.Action is not null;
			key.Text = corner is null ? "" : corner.Glyph.Length > 0 ? corner.Glyph : corner.Label[..Math.Min(3, corner.Label.Length)];
			key.FontFamily = corner?.Glyph.Length > 0 ? "icons" : "GeistMonoMedium";
			key.FontSize = corner?.Glyph.Length > 0 ? 22 : 13.5;
			key.CommandParameter = corner?.Action?.BuiltIn switch { "ctrlc" => "ctrl+c", "backtab" => "shift+tab", var named => named };
		}
		ApplyLanguage();
		System.Diagnostics.Debug.WriteLine("[Herdr] D-pad corners rebuilt");
	}

	// A key by name, as TerminalInput.Key takes it ("up", "backspace", "enter", "ctrl+c").
	public event Action<string>? KeySent;

	public event Action<IReadOnlyList<ShortcutItem>>? SequenceSent;

	public void ApplyLanguage()
	{
		foreach (Button key in new[] { Corner_TL, Corner_TR, Corner_BL, Corner_BR })
		{
			if (key.BindingContext is not Corner corner) { continue; }
			string? spoken = (corner.Action?.BuiltIn, corner.Label) switch
			{
				("backspace", "Backspace") => "Backspace", ("ctrlc", "Ctrl+C") => "Control C", ("esc", "Esc") => "Escape",
				_ => null,
			};
			SemanticProperties.SetDescription(key, corner.Action?.BuiltIn is null
					? $"{i18n.Translate("Shortcut")}, {corner.Label}" : spoken is null ? corner.Label : i18n.Translate(spoken));
		}
		SemanticProperties.SetDescription(Btn_Up, i18n.Translate("Up"));
		SemanticProperties.SetDescription(Btn_Left, i18n.Translate("Left"));
		SemanticProperties.SetDescription(Btn_Enter, i18n.Translate("Enter"));
		SemanticProperties.SetDescription(Btn_Right, i18n.Translate("Right"));
		SemanticProperties.SetDescription(Btn_Down, i18n.Translate("Down"));
	}

	// Put where it was left (centered until first dragged), inside the view it floats over.
	public void Place()
	{
		if (!IsVisible || _room is not { Width: > 0, Height: > 0 } room) { return; }
		bool compact = room.Height < _normalRows.Sum(row => row.Value) + Keys.RowSpacing * (_normalRows.Length - 1)
			+ Grab.HeightRequest + Spacing + _normalPadding.VerticalThickness
			|| room.Width < _normalColumns.Sum(column => column.Value) + Keys.ColumnSpacing * (_normalColumns.Length - 1)
			+ _normalPadding.HorizontalThickness;
		if (compact != _compact) { SetCompact(compact); }
		if (compact)
		{
			double width = Math.Min(room.Width, CompactWidth());
			if (CompactBody.WidthRequest != width) { CompactBody.WidthRequest = width; }
		}
		if (Width <= 0) { return; }
		(double x, double y) = RemoteSettings.DPadAt ?? (0.5, 0.5);
		TranslationX = Math.Round(x * Math.Max(0, room.Width - Width));
		TranslationY = Math.Round(y * Math.Max(0, room.Height - Height));
	}

	private const double CompactKey = 44;

	private double CompactWidth() => Keys.ColumnDefinitions.Sum(column => column.Width.Value)
		+ Keys.ColumnSpacing * (Keys.ColumnDefinitions.Count - 1);

	private void SetCompact(bool compact)
	{
		_compact = compact;
		Repeat_Released(this, EventArgs.Empty);
		Grab.IsVisible = !compact;
		Padding = compact ? Thickness.Zero : _normalPadding;
		if (compact) { Children.Remove(Keys); CompactBody.Content = Keys; }
		else { CompactBody.Content = null; Children.Add(Keys); }
		Keys.RowDefinitions = new RowDefinitionCollection((compact ? [new GridLength(CompactKey)] : _normalRows)
			.Select(height => new RowDefinition(height)).ToArray());
		Keys.ColumnDefinitions = new ColumnDefinitionCollection((compact ? _keys.Select(_ => new GridLength(CompactKey)) : _normalColumns)
			.Select(width => new ColumnDefinition(width)).ToArray());
		for (int i = 0; i < _keys.Length; i++)
		{
			(Button key, int row, int column) = _keys[i];
			Grid.SetRow(key, compact ? 0 : row);
			Grid.SetColumn(key, compact ? i : column);
			if (compact) { key.WidthRequest = key.HeightRequest = CompactKey; }
			else { key.ClearValue(WidthRequestProperty); key.ClearValue(HeightRequestProperty); }
		}
		CompactBody.HeightRequest = CompactKey;
		CompactBody.IsVisible = compact;
	}

	// The view it floats over is its parent: it is placed again whenever that one changes size.
	protected override void OnParentSet()
	{
		base.OnParentSet();
		if (_room is not null) { _room.SizeChanged -= Room_SizeChanged; }
		_room = Parent as View;
		if (_room is not null) { _room.SizeChanged += Room_SizeChanged; }
	}

	private void Room_SizeChanged(object? sender, EventArgs e) => Place();

	// The grab bar: the pad follows the finger, kept inside its room; its place is saved when the finger lifts.
	private void Grab_PanUpdated(object? sender, PanUpdatedEventArgs e)
	{
		if (_room is not { } room) { return; }
		double roomX = Math.Max(0, room.Width - Width);
		double roomY = Math.Max(0, room.Height - Height);
		switch (e.StatusType)
		{
			case GestureStatus.Started:
				_startX = TranslationX;
				_startY = TranslationY;
				break;
			case GestureStatus.Running:
				TranslationX = Math.Clamp(_startX + e.TotalX, 0, roomX);
				TranslationY = Math.Clamp(_startY + e.TotalY, 0, roomY);
				break;
			case GestureStatus.Completed:
				RemoteSettings.SaveDPadAt(roomX > 0 ? TranslationX / roomX : 0.5, roomY > 0 ? TranslationY / roomY : 0.5);
				break;
		}
	}

	private void Key_Clicked(object? sender, EventArgs e) => KeySent?.Invoke((string)((Button)sender!).CommandParameter);

	private void Corner_Pressed(object? sender, EventArgs e)
	{
		if (((Button)sender!).CommandParameter is "backspace" or "up" or "down" or "left" or "right") { Repeat_Pressed(sender, e); }
	}

	private void Corner_Clicked(object? sender, EventArgs e)
	{
		if (((Button)sender!).BindingContext is not Corner { Action: { } action }) { return; }
		if (action.Items is { } items) { SequenceSent?.Invoke(items); }
		else if (action.BuiltIn is not ("backspace" or "up" or "down" or "left" or "right")) { Key_Clicked(sender, e); }
	}

	// A repeating key: sent at once, then repeated while held.
	private void Repeat_Pressed(object? sender, EventArgs e)
	{
		_held = (string)((Button)sender!).CommandParameter;
		KeySent?.Invoke(_held);
		if (_repeat is null)
		{
			_repeat = Dispatcher.CreateTimer();
			_repeat.IsRepeating = true;
			_repeat.Tick += Repeat_Tick;
		}
		_repeat.Stop();
		_repeat.Interval = RepeatDelay;
		_repeat.Start();
	}

	private void Repeat_Tick(object? sender, EventArgs e)
	{
		if (_held is null)
		{
			_repeat?.Stop();
			return;
		}
		KeySent?.Invoke(_held);
		if (_repeat is { } repeat && repeat.Interval != RepeatEvery)
		{
			repeat.Stop();
			repeat.Interval = RepeatEvery;
			repeat.Start();
		}
	}

	private void Repeat_Released(object? sender, EventArgs e)
	{
		_held = null;
		_repeat?.Stop();
	}
}
