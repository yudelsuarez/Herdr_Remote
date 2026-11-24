using HerdrRemote;
using HerdrRemote.Remote;
using Languages;
using Configuration = HerdrRemote.Remote.Shortcuts;

namespace Popups;

public sealed class ShortcutSheet : ContentView
{
	private readonly Entry _label = new() { Style = App.Res<Style>("Fld.Entry"), IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
	private readonly Entry _text = new() { Style = App.Res<Style>("Fld.Entry"), Placeholder = i18n.Translate("Text to type"), IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
	private readonly Picker _action = new(), _ctrl = new();
	private readonly HorizontalStackLayout _glyphChoices = new() { Spacing = 8 };
	private readonly FlexLayout _chips = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
	private readonly VerticalStackLayout _custom = new() { Spacing = 10 };
	private readonly Label _preview = new() { Style = App.Res<Style>("FSheet.Note") };
	private readonly Label _note = new() { Style = App.Res<Style>("FSheet.Note") };
	private readonly Button _save = new() { Text = i18n.Translate("Save"), Style = App.Res<Style>("Btn.Primary"), MinimumHeightRequest = 48 };
	private readonly Button _add = new() { Text = i18n.Translate("Add"), Style = App.Res<Style>("Btn.Text") };
	private readonly Button _remove = new() { Text = i18n.Translate("Remove"), Style = App.Res<Style>("Btn.Text"), IsVisible = false };
	private readonly List<ShortcutItem> _items;
	private readonly List<ShortcutAction?> _actions;
	private readonly string[] _glyphs = ["\uE96E", "\uE95B", "\uE96F", "\uE95A", "\uE959", "\uE916", "\uE934", "\uE94E", ""];
	private readonly KeyBarEntry? _entry;
	private readonly string? _position;
	private readonly Action<bool> _close;
	private int _selected = -1;
	private int _glyphSelected;
	private string _glyph = "\uE96E";

	private ShortcutSheet(KeyBarEntry? entry, string? position, Action<bool> close)
	{
		_entry = entry;
		_position = position;
		_close = close;
		Corner? corner = Configuration.Current.Corners.FirstOrDefault(item => item.Position == position);
		ShortcutAction? initial = position is null ? entry?.Action : corner?.Action;
		_items = initial?.Items?.ToList() ?? [];
		_label.Text = entry?.Label ?? corner?.Label ?? "";
		_label.MaxLength = position is null ? 8 : corner?.Glyph == "" ? 3 : int.MaxValue;
		_actions = position is null
			? [.. Configuration.Defaults.KeyBar.Select(item => item.Action), new("backspace"), new("up"), new("down"), new("left"), new("right"), new(Items: [])]
			: [new("backspace"), new("ctrlc"), new("esc"), new("enter"), new("tab"), new(Items: []), null];
		if (position is null)
		{
			_actions.RemoveAll(action => action?.BuiltIn is "ctrl" or "alt" or "shift" or "dpad"
				&& Configuration.Current.KeyBar.Any(item => item.Id != entry?.Id && item.Action.BuiltIn == action.BuiltIn));
		}
		_action.ItemsSource = _actions.Select(action => action is null ? i18n.Translate("Nothing") : action.BuiltIn is null ? i18n.Translate("Custom") : Summary(action)).ToList();
		_action.SelectedIndex = _actions.FindIndex(action => action?.BuiltIn == initial?.BuiltIn && action?.Character == initial?.Character
			&& (action is null) == (initial is null));
		if (position is null && entry is null) { _action.SelectedIndex = _actions.Count - 1; }
		string initialGlyph = corner?.Glyph ?? "\uE96E";
		_glyphSelected = Math.Max(0, Array.IndexOf(_glyphs, initialGlyph == "\uE926" ? "\uE96F" : initialGlyph));
		_ctrl.ItemsSource = Enumerable.Range('A', 26).Select(letter => "Ctrl+" + (char)letter).ToList();
		_ctrl.Title = "Ctrl+…";
		_ctrl.SelectedIndex = -1;
		foreach (Picker picker in new[] { _action, _ctrl })
		{
			picker.FontFamily = "Geist";
			picker.FontSize = 14;
			picker.MinimumHeightRequest = 50;
			picker.MinimumWidthRequest = 64;
			picker.BackgroundColor = Colors.Transparent;
			picker.SetAppThemeColor(Picker.TextColorProperty, App.Res<Color>("Ink2"), App.Res<Color>("Ink2Dark"));
			picker.HandlerChanged += (_, _) =>
			{
#if IOS
				if (picker.Handler?.PlatformView is UIKit.UITextField field)
				{
					field.BorderStyle = UIKit.UITextBorderStyle.None;
					field.BackgroundColor = UIKit.UIColor.Clear;
					field.TextAlignment = UIKit.UITextAlignment.Right;
				}
#elif ANDROID
				if (picker.Handler?.PlatformView is Android.Widget.EditText field)
				{
					field.Background = null;
					field.BackgroundTintList = null;
					field.SetIncludeFontPadding(false);
					field.SetPadding(0, 0, 0, 0);
					field.SetPaddingRelative(0, 0, 0, 0);
					field.Gravity = Android.Views.GravityFlags.End | Android.Views.GravityFlags.CenterVertical;
				}
#endif
			};
		}
		SemanticProperties.SetDescription(_label, i18n.Translate("Label"));
		SemanticProperties.SetDescription(_text, i18n.Translate("Text to type"));
		SemanticProperties.SetDescription(_action, i18n.Translate("Action"));
		SemanticProperties.SetDescription(_ctrl, "Ctrl+…");
		_action.SelectedIndexChanged += (_, _) =>
		{
			if (position is not null && _action.SelectedIndex >= 0)
			{
				string glyph = _actions[_action.SelectedIndex]?.BuiltIn switch
				{
					"backspace" => "\uE95A", "ctrlc" => "\uE95B", "esc" => "\uE96F", "enter" => "\uE959", "tab" => "\uE916", _ => "\uE96E",
				};
				ChooseGlyph(Array.IndexOf(_glyphs, glyph));
			}
			Refresh();
		};
		_ctrl.SelectedIndexChanged += (_, _) =>
		{
			if (_ctrl.SelectedIndex < 0) { return; }
			int letter = _ctrl.SelectedIndex;
			// Let the native picker finish committing before its palette selection is reset.
			Dispatcher.Dispatch(() =>
			{
				Append(ShortcutItem.Key("ctrl+" + (char)('a' + letter)));
				_ctrl.SelectedIndex = -1;
			});
		};
		_label.TextChanged += (_, _) => Refresh();
		_text.TextChanged += (_, _) => Refresh();
		_add.Clicked += (_, _) =>
		{
			if (string.IsNullOrEmpty(_text.Text)) { return; }
			Append(ShortcutItem.Text(_text.Text));
			_text.Text = "";
		};
		_remove.Clicked += (_, _) =>
		{
			if (_selected < 0) { return; }
			_items.RemoveAt(_selected);
			_selected = -1;
			Refresh(true);
		};
		Grid textRow = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
		textRow.Add(_text);
		textRow.Add(_add, 1);
		HorizontalStackLayout palette = new() { Spacing = 8 };
		foreach (string named in new[] { "esc", "enter", "tab", "shift+tab", "backspace", "up", "down", "left", "right", "home", "end" })
		{
			Button button = new() { Text = KeyName(named), Style = App.Res<Style>("TrayKey") };
			button.Clicked += (_, _) => Append(ShortcutItem.Key(named));
			palette.Add(button);
		}
		palette.Add(_ctrl);
		_custom.Add(_chips);
		_custom.Add(_remove);
		_custom.Add(new Border { Style = App.Res<Style>("Fld"), Content = textRow });
		_custom.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = palette });
		_custom.Add(_preview);
		VerticalStackLayout fields = new() { Style = App.Res<Style>("FSheet") };
		fields.Add(new Label { Text = i18n.Translate("Label"), Style = App.Res<Style>("Fld.Label") });
		fields.Add(new Border { Style = App.Res<Style>("Fld"), Content = _label });
		if (position is not null)
		{
			fields.Add(new Label { Text = i18n.Translate("Glyph"), Style = App.Res<Style>("Fld.Label") });
			for (int index = 0; index < _glyphs.Length; index++)
			{
				int selected = index;
				Button glyph = new() { Text = index == 8 ? i18n.Translate("Text") : _glyphs[index] };
				glyph.Clicked += (_, _) => { ChooseGlyph(selected); Refresh(); };
				_glyphChoices.Add(glyph);
			}
			fields.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _glyphChoices });
		}
		Grid actionRow = new() { Style = App.Res<Style>("Lst.Row"), ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(16)] };
		actionRow.Add(new Label { Text = i18n.Translate("Action"), Style = App.Res<Style>("Lst.Title"), VerticalOptions = LayoutOptions.Center });
		actionRow.Add(_action, 1);
		actionRow.Add(new Label { Text = "\uE907", Style = App.Res<Style>("Lst.Chev"), InputTransparent = true }, 2);
		fields.Add(new Border { Style = App.Res<Style>("Lst"), Margin = 0, Content = actionRow });
		fields.Add(_custom);
		fields.Add(_note);
		Button cancel = new() { Text = i18n.Translate("Cancel"), Style = App.Res<Style>("Btn.Outline"), MinimumHeightRequest = 48 };
		cancel.Clicked += (_, _) => close(false);
		_save.Clicked += (_, _) => Save(false);
		Grid buttons = new() { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 10, Margin = new(16,12,16,4) };
		if (entry is not null)
		{
			Button delete = new() { Text = i18n.Translate("Delete"), Style = App.Res<Style>("Btn.Outline"), MinimumHeightRequest = 48 };
			delete.SetAppThemeColor(Button.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark"));
			delete.Clicked += (_, _) => Save(true);
			buttons.Add(delete);
		}
		buttons.Add(cancel, 1);
		buttons.Add(_save, 2);
		Grid body = new() { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
		body.Add(new Label { Text = position is null ? i18n.Translate("Edit key") : CornerName(position), Style = App.Res<Style>("Sht.Title"), Margin = new(16,0,16,14) });
		body.Add(new ScrollView { Content = fields }, 0, 1);
		body.Add(buttons, 0, 2);
		Content = body;
		ChooseGlyph(_glyphSelected);
		// Preserve a legacy glyph until the user chooses another glyph or action.
		_glyph = initialGlyph;
		Refresh(true);
	}

	public static Task<bool> PopAsync(KeyBarEntry? entry = null, string? position = null)
		=> PopupsHandle.PopBottomSheetAsync<bool>(close => new ShortcutSheet(entry, position, close));

	private void ChooseGlyph(int selected)
	{
		_glyphSelected = selected;
		_glyph = _glyphs[selected];
		_label.MaxLength = _position is null ? 8 : selected == 8 ? 3 : int.MaxValue;
		string[] names = ["Clear", "Ctrl+C", "Esc", "Backspace", "Enter", "Back", "Jump to bottom", "Terminal", "Text"];
		for (int index = 0; index < _glyphChoices.Children.Count; index++)
		{
			Button key = (Button)_glyphChoices.Children[index];
			key.Style = App.Res<Style>(index == 8 ? index == selected ? "TrayKey.On" : "TrayKey" : index == selected ? "TrayKey.IconOn" : "TrayKey.Icon");
			SemanticProperties.SetDescription(key, HerdrRemote.Pages.ChipRow.Spoken(i18n.Translate(names[index]), index == selected));
		}
	}

	private void Append(ShortcutItem item)
	{
		if (_items.Count >= 32 || _items.Where(value => value.Type == "text").Sum(value => value.Value.Length) + (item.Type == "text" ? item.Value.Length : 0) > 1024) { return; }
		_items.Add(item);
		_selected = -1;
		Refresh(true);
	}

	private void Refresh(bool chips = false)
	{
		ShortcutAction? action = _action.SelectedIndex >= 0 ? _actions[_action.SelectedIndex] : null;
		bool custom = action is not null && action.BuiltIn is null;
		_custom.IsVisible = custom;
		bool labelValid = !string.IsNullOrWhiteSpace(_label.Text) && (_position is not null || _label.Text.Length <= 8)
			&& (_position is null || _glyphSelected != 8 || _label.Text.Length <= 3);
		_save.IsEnabled = _action.SelectedIndex >= 0 && labelValid && (!custom || _items.Count > 0);
		_save.Opacity = _save.IsEnabled ? 1 : 0.5;
		_label.FontFamily = _label.Text is [>= '\uE000' and <= '\uF8FF'] ? "icons" : "Geist";
		_note.Style = App.Res<Style>("FSheet.Note");
		_note.Text = !labelValid ? i18n.Translate("Label") + (_position is null ? " · 1–8" : _glyphSelected == 8 ? " · 1–3" : " · ≥1")
			: custom && _items.Count == 0 ? i18n.Translate("Add at least one item")
			: i18n.Translate("Sent to the open pane exactly as shown. Do not save passwords here.");
		_preview.Text = string.Format(i18n.Translate("Sends: {0}"), Summary(new(Items: _items)));
		int remaining = 1024 - _items.Where(item => item.Type == "text").Sum(item => item.Value.Length);
		_text.MaxLength = remaining;
		_add.IsEnabled = _items.Count < 32 && !string.IsNullOrEmpty(_text.Text) && _text.Text.Length <= remaining;
		_remove.IsVisible = _selected >= 0;
		if (chips) { _chips.Clear(); }
		for (int index = 0; chips && index < _items.Count; index++)
		{
			int selected = index;
			ShortcutItem item = _items[index];
			Button chip = new() { Text = item.Type == "text" ? item.Value : KeyName(item.Value), Style = App.Res<Style>("TrayKey"),
				Margin = new(0,0,8,8), BorderWidth = item.Type == "text" || selected == _selected ? 1 : 0 };
			chip.SetAppThemeColor(Button.BorderColorProperty, App.Res<Color>(selected == _selected ? "Acc" : "Line2"), App.Res<Color>(selected == _selected ? "AccDark" : "Line2Dark"));
			if (item.Type == "text") { chip.BackgroundColor = Colors.Transparent; }
			chip.Clicked += (_, _) => { _selected = selected; Refresh(true); };
			_chips.Add(chip);
		}
		Element? host = Parent;
		while (host is not null and not BottomSheet) { host = host.Parent; }
		(host as BottomSheet)?.Refit();
	}

	private void Save(bool delete)
	{
		Element? host = Parent;
		while (host is not null and not BottomSheet) { host = host.Parent; }
		if (host is not BottomSheet { IsClosing: false }) { return; }
		if (!delete && !_save.IsEnabled) { return; }
		try
		{
			Configuration current = Configuration.Current;
			ShortcutAction? action = _actions[_action.SelectedIndex];
			if (action is not null && action.BuiltIn is null) { action = new(Items: _items.ToArray()); }
			if (_position is not null)
			{
				Corner corner = new(_position, _label.Text ?? "", action, _glyph);
				List<Corner> corners = current.Corners.ToList();
				int index = corners.FindIndex(item => item.Position == _position);
				if (index >= 0) { corners[index] = corner; } else { corners.Add(corner); }
				Configuration.Save(current with { Corners = corners });
			}
			else
			{
				List<KeyBarEntry> bar = current.KeyBar.ToList();
				int index = _entry is null ? -1 : bar.FindIndex(item => item.Id == _entry.Id);
				if (delete) { if (index >= 0) { bar.RemoveAt(index); } }
				else
				{
					KeyBarEntry entry = new(_entry?.Id ?? Guid.NewGuid().ToString("N"), _label.Text!, action!);
					if (index >= 0) { bar[index] = entry; } else { bar.Add(entry); }
				}
				Configuration.Save(current with { KeyBar = bar });
			}
			_close(true);
		}
		catch (Exception)
		{
			_note.Style = App.Res<Style>("FSheet.Error");
			_note.Text = i18n.Translate("Something went wrong, please try again");
		}
	}

	internal static string CornerName(string position) => i18n.Translate(position switch
	{
		"tl" => "Top left", "tr" => "Top right", "bl" => "Bottom left", _ => "Bottom right",
	});

	internal static string Summary(ShortcutAction? action) => action is null ? i18n.Translate("Empty")
		: action.BuiltIn is { } builtIn ? builtIn == "char" ? action.Character! : KeyName(builtIn)
		: string.Join(" → ", action.Items!.Select(item => item.Type == "text" ? $"{i18n.Translate("Text")} \"{item.Value}\"" : KeyName(item.Value)));

	private static string KeyName(string named) => named switch
	{
		"esc" => i18n.Translate("Esc"), "enter" => "⏎ " + i18n.Translate("Enter"), "tab" => "Tab", "backtab" or "shift+tab" => "⇧Tab",
		"backspace" => i18n.Translate("Backspace"), "up" => "↑", "down" => "↓", "left" => "←", "right" => "→",
		"home" => "Home", "end" => "End", "ctrl" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "dpad" => "D-pad",
		"ctrlc" => "Ctrl+C", "pgup" => "PgUp", "pgdn" => "PgDn", _ => named.StartsWith("ctrl+", StringComparison.Ordinal) ? "Ctrl+" + named[5..].ToUpperInvariant() : named,
	};
}
