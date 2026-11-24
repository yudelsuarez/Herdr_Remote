using HerdrRemote.Remote;
using Languages;

namespace HerdrRemote.Pages;

// Terminal key bar reports key names and Ctrl / Alt / Shift state; the Pane encodes bytes through TerminalInput.
// Tap a modifier to apply it once; hold for at least 450 ms to lock until tapped again. Measure at release,
// with no timer while held. TrayKey.On marks active modifiers; a line marks locks. Holding ^C sends it twice.
// Decide tap versus hold on Clicked, not Released: scrolling also raises Released, and swipes must not trigger keys.
public partial class KeyBar : Tray
{
	private enum Modifier { Off, Once, Locked }

	private const long HoldMs = 450;

	private Modifier _ctrl;
	private Modifier _alt;
	private Modifier _shift;
	private long _pressedAt;
	private IReadOnlyList<KeyBarEntry>? _entries;
	private readonly Dictionary<string, (Button Key, BoxView Line)> _modifiers = [];
	private Button? _dpadKey;

	public void ResetModifiers()
	{
		_ctrl = _alt = _shift = Modifier.Off;
		_pressedAt = 0;
		ShowModifiers();
	}

	public KeyBar()
	{
		InitializeComponent();
		ApplyConfiguration();
		Loaded += (_, _) => { Shortcuts.Changed += ApplyConfiguration; ApplyConfiguration(); };
		Unloaded += (_, _) => Shortcuts.Changed -= ApplyConfiguration;
	}

	private void ApplyConfiguration()
	{
		IReadOnlyList<KeyBarEntry> entries = Shortcuts.Current.KeyBar;
		if (_entries is not null && _entries.SequenceEqual(entries)) { return; }
		_entries = entries;
		KeyRow.Children.Clear();
		_modifiers.Clear();
		_dpadKey = null;
		foreach (KeyBarEntry entry in entries)
		{
			string? builtIn = entry.Action.BuiltIn;
			Button key = new() { Text = entry.Label, BindingContext = entry,
				Style = App.Res<Style>(entry.Label is [>= '\uE000' and <= '\uF8FF'] ? "TrayKey.Icon" : "TrayKey") };
			switch (builtIn)
			{
				case "ctrl": case "alt": case "shift":
					BoxView line = new() { Style = App.Res<Style>("TrayKey.Lock") };
					_modifiers.Add(builtIn, (key, line));
					key.Pressed += Hold_Pressed;
					key.Clicked += Modifier_Clicked;
					KeyRow.Children.Add(new Grid { Children = { key, line } });
					continue;
				case "ctrlc": key.Pressed += Hold_Pressed; key.Clicked += CtrlC_Clicked; break;
				case "char": key.CommandParameter = entry.Action.Character; key.Clicked += Text_Clicked; break;
				case "pgup": case "pgdn":
					key.CommandParameter = builtIn == "pgup" ? "up" : "down"; key.Clicked += Page_Clicked; break;
				case "dpad": _dpadKey = key; key.Clicked += DPad_Clicked; break;
				case null: key.Clicked += Sequence_Clicked; break;
				default: key.CommandParameter = builtIn == "backtab" ? "shift+tab" : builtIn; key.Clicked += Key_Clicked; break;
			}
			KeyRow.Children.Add(key);
		}
		if (!_modifiers.ContainsKey("ctrl")) { _ctrl = Modifier.Off; }
		if (!_modifiers.ContainsKey("alt")) { _alt = Modifier.Off; }
		if (!_modifiers.ContainsKey("shift")) { _shift = Modifier.Off; }
		if (_dpadKey is null && _dpadShown)
		{
			_dpadShown = false;
			DPadTapped?.Invoke();
		}
		ApplyLanguage();
		System.Diagnostics.Debug.WriteLine("[Herdr] key bar rebuilt");
	}

	// Named keys, already with the active modifiers ("esc", "ctrl+c", "alt+up").
	public event Action<string[]>? KeysSent;

	// Plain text keys (| / ~ -) while no modifier is on.
	public event Action<string>? TextSent;

	public event Action<IReadOnlyList<ShortcutItem>>? SequenceSent;

	// PgUp / PgDn: "up" or "down" (the terminal view's page scroll).
	public event Action<string>? PageScrolled;

	// Attach when idle, or Stop while Reading is active.
	public event Action? AttachTapped;

	public event Action? KeyboardTapped;

	// The dictation key: a tap opens the draft and listens, stops listening, or listens again (DictationSession decides).
	public event Action? MicTapped;

	// Jump to bottom: a tap, shown only while scrolled back (ScrolledBack).
	public event Action? BottomTapped;

	// The D-pad key: a tap opens or closes the floating D-pad; DPadShown says which and lights the key.
	public event Action? DPadTapped;

	public bool DPadShown
	{
		get => _dpadShown;
		set
		{
			_dpadShown = value;
			ShowDPad();
		}
	}

	private bool _dpadShown;

	private void ShowDPad()
	{
		if (_dpadKey is not { } key) { return; }
		bool icon = key.Text is [>= '\uE000' and <= '\uF8FF'];
		key.Style = App.Res<Style>(icon ? (_dpadShown ? "TrayKey.IconOn" : "TrayKey.Icon") : (_dpadShown ? "TrayKey.On" : "TrayKey"));
		SemanticProperties.SetDescription(key, ChipRow.Spoken(i18n.Translate("Arrow keys"), _dpadShown));
	}

	public void ApplyLanguage()
	{
		foreach (View child in KeyRow.Children)
		{
			if (child is not Button { BindingContext: KeyBarEntry entry } key) { continue; }
			string? spoken = entry.Action.BuiltIn switch
			{
				"esc" => "Escape", "tab" => "Tab", "enter" => "Enter", "ctrlc" => "Control C",
				"pgup" => "Page up", "pgdn" => "Page down", "home" => "Home", "end" => "End",
				_ => null,
			};
			SemanticProperties.SetDescription(key, entry.Action.BuiltIn is null
					? $"{i18n.Translate("Shortcut")}, {entry.Label}" : spoken is null ? entry.Label : i18n.Translate(spoken));
		}
		SemanticProperties.SetDescription(Btn_Bottom, i18n.Translate("Jump to bottom"));
		ShowDictation(_dictationOpen, _dictationListening);
		ShowDPad();
		ShowModifiers();
	}

	private bool _dictationOpen, _dictationListening;

	// The mic key is lit while the draft is open; while it listens, a tap stops.
	public void ShowDictation(bool open, bool listening)
	{
		_dictationOpen = open;
		_dictationListening = listening;
		Btn_Mic.Style = App.Res<Style>(open ? "TrayKey.IconOn" : "TrayKey.Icon");
		SemanticProperties.SetDescription(Btn_Mic, i18n.Translate(listening ? "Stop dictation" : "Dictate"));
	}

	// The keyboard icon; while the keyboard is up it shows the keyboard with the down chevron and hides it.
	public void Show(bool keyboardUp)
	{
		Btn_Keyboard.Text = keyboardUp ? "" : "";
		SemanticProperties.SetDescription(Btn_Keyboard, i18n.Translate(keyboardUp ? "Hide keyboard" : "Show keyboard"));
	}

	// Jump to bottom shows while the terminal is scrolled back; End grows by it and the keys' area shrinks.
	public bool ScrolledBack { set => Btn_Bottom.IsVisible = value; }


	// The last key: the paperclip (Attach), or Stop while a read runs.
	public bool Reading
	{
		set
		{
			Btn_Attach.Text = value ? "\uE92E" : "\uE92C";
			SemanticProperties.SetDescription(Btn_Attach, i18n.Translate(value ? "Stop reading" : "Attach"));
		}
	}

	// The key with the active modifiers in one combined name, ctrl+alt+shift+key ("ctrl+x", "shift+left", "ctrl+shift+c"), null when
	// none is on. A one-shot modifier is used up here.
	public string? Modify(string key)
	{
		if (_ctrl == Modifier.Off && _alt == Modifier.Off && _shift == Modifier.Off) { return null; }
		string named = (_ctrl == Modifier.Off ? "" : "ctrl+") + (_alt == Modifier.Off ? "" : "alt+") + (_shift == Modifier.Off ? "" : "shift+") + key;
		if (_ctrl == Modifier.Once) { _ctrl = Modifier.Off; }
		if (_alt == Modifier.Once) { _alt = Modifier.Off; }
		if (_shift == Modifier.Once) { _shift = Modifier.Off; }
		ShowModifiers();
		return named;
	}

	private void ShowModifiers()
	{
		if (_modifiers.TryGetValue("ctrl", out var ctrl)) { ShowModifier(ctrl.Key, ctrl.Line, _ctrl, i18n.Translate("Control")); }
		if (_modifiers.TryGetValue("alt", out var alt)) { ShowModifier(alt.Key, alt.Line, _alt, "Alt"); }
		if (_modifiers.TryGetValue("shift", out var shift)) { ShowModifier(shift.Key, shift.Line, _shift, i18n.Translate("Shift")); }
	}

	// Off: a plain key; once: the lit key; locked: the lit key with its lock line.
	private static void ShowModifier(Button key, BoxView line, Modifier state, string spoken)
	{
		key.Style = App.Res<Style>(state == Modifier.Off ? "TrayKey" : "TrayKey.On");
		line.IsVisible = state == Modifier.Locked;
		SemanticProperties.SetDescription(key, ChipRow.Spoken(spoken, state != Modifier.Off));
	}

	private void Key_Clicked(object? sender, EventArgs e)
	{
		string key = (string)((Button)sender!).CommandParameter;
		KeysSent?.Invoke([Modify(key) ?? key]);
	}

	// ^C is already a modified key: the modifiers stay for the next one. Held: ^C twice in one send (what Claude Code and others need to
	// exit), with the long-press haptic.
	private void CtrlC_Clicked(object? sender, EventArgs e)
	{
		bool held = Environment.TickCount64 - _pressedAt >= HoldMs;
		if (held && HapticFeedback.Default.IsSupported) { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); }
		KeysSent?.Invoke(held ? ["ctrl+c", "ctrl+c"] : ["ctrl+c"]);
	}

	private void Text_Clicked(object? sender, EventArgs e)
	{
		string text = (string)((Button)sender!).CommandParameter;
		if (Modify(text) is { } named) { KeysSent?.Invoke([named]); }
		else { TextSent?.Invoke(text); }
	}

	private void Sequence_Clicked(object? sender, EventArgs e) => SequenceSent?.Invoke(((KeyBarEntry)((Button)sender!).BindingContext).Action.Items!);

	private void Page_Clicked(object? sender, EventArgs e) => PageScrolled?.Invoke((string)((Button)sender!).CommandParameter);

	private void Attach_Clicked(object? sender, EventArgs e) => AttachTapped?.Invoke();

	private void Mic_Clicked(object? sender, EventArgs e) => MicTapped?.Invoke();

	private void Keyboard_Clicked(object? sender, EventArgs e) => KeyboardTapped?.Invoke();

	private void Bottom_Clicked(object? sender, EventArgs e) => BottomTapped?.Invoke();

	private void DPad_Clicked(object? sender, EventArgs e) => DPadTapped?.Invoke();

	private void Hold_Pressed(object? sender, EventArgs e) => _pressedAt = Environment.TickCount64;

	// Held: locked, with the long-press haptic at release. Tapped: off -> next key only; once or locked -> off.
	private void Modifier_Clicked(object? sender, EventArgs e)
	{
		bool held = Environment.TickCount64 - _pressedAt >= HoldMs;
		string? builtIn = ((KeyBarEntry)((Button)sender!).BindingContext).Action.BuiltIn;
		ref Modifier state = ref builtIn == "ctrl" ? ref _ctrl : ref builtIn == "alt" ? ref _alt : ref _shift;
		state = held ? Modifier.Locked : state == Modifier.Off ? Modifier.Once : Modifier.Off;
		if (held && HapticFeedback.Default.IsSupported) { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); }
		ShowModifiers();
	}
}
