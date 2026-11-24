using CoreGraphics;
using Foundation;
using HerdrRemote.Remote;
using HerdrRemote.Services;
using ObjCRuntime;
using System.Globalization;
using UIKit;

namespace HerdrRemote.Pages;

internal sealed class IosTerminalKeys : UIView, IUITextInput
{
	private sealed class Position(int index) : UITextPosition
	{
		internal int Index { get; } = index;
	}

	private sealed class Range(int start, int end) : UITextRange
	{
		public override UITextPosition Start { get; } = new Position(start);
		public override UITextPosition End { get; } = new Position(end);
		public override bool IsEmpty => start == end;
	}

	private const double Step = 12;
	private readonly TerminalKeys _owner;
	private readonly UITextInputStringTokenizer _tokenizer;
	private readonly HashSet<UIPress> _handled = [];
	private int _cursor, _end, _markedCursor;
	private string? _marked;
	private double? _floatingX;
	private WeakReference<NSObject>? _inputDelegate;

	internal IosTerminalKeys(TerminalKeys owner)
	{
		_owner = owner;
		_tokenizer = new(this);
	}

	public override bool CanBecomeFirstResponder => true;
	// The keyboard's real visibility, from the system (as Android reads the IME insets), tracked for the whole app so the value is
	// right whenever this view enters a window; every path that raises or lowers it (this view, a selection menu, returning from
	// another app, a rotation) is reported, never a guess kept here.
	private static class SystemKeyboard
	{
		internal static bool Visible { get; private set; }
		internal static event Action<bool>? Changed;
		private static NSObject? _shown, _hidden;

		internal static void Start()
		{
			_shown ??= UIKeyboard.Notifications.ObserveWillShow((_, _) => Set(true));
			_hidden ??= UIKeyboard.Notifications.ObserveWillHide((_, _) => Set(false));
		}

		private static void Set(bool visible)
		{
			Visible = visible;
			Changed?.Invoke(visible);
		}
	}

	private void KeyboardChanged(bool visible) => _owner.Active(visible);

	public override void MovedToWindow()
	{
		base.MovedToWindow();
		SystemKeyboard.Start();
		SystemKeyboard.Changed -= KeyboardChanged;
		if (Window is null) { return; }
		SystemKeyboard.Changed += KeyboardChanged;
		_owner.Active(SystemKeyboard.Visible);
	}

	internal void Open() => BecomeFirstResponder();

	// Lowers whatever keyboard the window shows, not only this view's.
	internal void Close()
	{
		ResignFirstResponder();
		Window?.EndEditing(true);
	}

	public override bool ResignFirstResponder()
	{
		bool active = IsFirstResponder;
		if (!base.ResignFirstResponder()) { return false; }
		if (active)
		{
			_cursor = _end = _markedCursor = 0;
			_marked = null;
			_floatingX = null;
			_handled.Clear();
		}
		return true;
	}

	public bool HasText => true;

	public void InsertText(string text)
	{
		if (!IsFirstResponder || text.Length == 0) { return; }
		_marked = null;
		foreach (string element in Graphemes(text))
		{
			if (element is "\r" or "\n" or "\r\n") { _cursor = _end = 0; }
			else { _cursor++; _end++; }
		}
		_owner.Text(text);
	}

	private static IEnumerable<string> Graphemes(string text)
	{
		var elements = StringInfo.GetTextElementEnumerator(text);
		while (elements.MoveNext()) { yield return elements.GetTextElement(); }
	}

	public void DeleteBackward()
	{
		if (!IsFirstResponder) { return; }
		if (_marked is { Length: > 0 } marked)
		{
			_marked = marked[..StringInfo.ParseCombiningCharacters(marked)[^1]];
			_markedCursor = Math.Min(_markedCursor, _marked.Length);
			return;
		}
		if (_cursor > 0) { _cursor--; _end--; }
		_owner.Key("backspace");
	}

	// UIKit sees geometry and provisional IME text, never a second copy of the committed command. The document also reports
	// Filler blank characters before the keyboard's own draft: the terminal line can hold text the keyboard never typed, and with
	// nothing before the caret iOS stops repeating a held Delete (the earlier text field kept sentinel spaces for the same reason).
	private const int Filler = 4096;
	public UITextPosition BeginningOfDocument => new Position(-Filler);
	public UITextPosition EndOfDocument => new Position(End);
	private int End => _end + (_marked?.Length ?? 0);
	private int Selection => _cursor + (_marked is null ? 0 : _markedCursor);
	private static int Index(UITextPosition? position) => (position as Position)?.Index ?? 0;
	private static bool Left(UITextLayoutDirection direction) => direction is UITextLayoutDirection.Left or UITextLayoutDirection.Up;

	public UITextRange? SelectedTextRange
	{
		get => new Range(Selection, Selection);
		set
		{
			if (value is null || !value.IsEmpty || !IsFirstResponder) { return; }
			int target = Index(value.Start);
			if (_marked is not null) { _markedCursor = Math.Clamp(target - _cursor, 0, _marked.Length); }
			else if (_floatingX is null) { Move(target); }
		}
	}

	private void Move(int target)
	{
		target = Math.Clamp(target, 0, _end);
		while (_cursor != target)
		{
			bool left = target < _cursor;
			_cursor += left ? -1 : 1;
			_owner.Key(left ? "left" : "right");
		}
	}

	[Export("beginFloatingCursorAtPoint:")]
	public void BeginFloatingCursor(CGPoint point) => _floatingX = point.X;

	[Export("updateFloatingCursorAtPoint:")]
	public void UpdateFloatingCursor(CGPoint point)
	{
		if (_floatingX is not double previous || _marked is not null) { return; }
		int steps = (int)((point.X - previous) / Step);
		_floatingX = previous + steps * Step;
		// The terminal may contain history or a draft the keyboard did not type.
		_cursor = Math.Clamp(_cursor + steps, 0, _end);
		TerminalKeyEvents.Move(steps, _owner.Key);
	}

	[Export("endFloatingCursor")]
	public void EndFloatingCursor() => _floatingX = null;

	// UIKit accepts nil for these ranges and positions despite the managed interface annotations.
	public UITextRange MarkedTextRange => _marked is null ? null! : new Range(_cursor, _cursor + _marked.Length);
	public NSDictionary? MarkedTextStyle { get; set; }
	public NSObject WeakTokenizer => _tokenizer;
	public NSObject? WeakInputDelegate
	{
		get => _inputDelegate is not null && _inputDelegate.TryGetTarget(out var value) ? value : null;
		set => _inputDelegate = value is null ? null : new(value);
	}

	public void SetMarkedText(string? markedText, NSRange selectedRange)
	{
		_marked = markedText;
		_markedCursor = (int)Math.Clamp((long)selectedRange.Location, 0, markedText?.Length ?? 0);
	}

	public void UnmarkText()
	{
		string? text = _marked;
		_marked = null;
		if (text is not null) { InsertText(text); }
	}

	public string TextInRange(UITextRange range)
	{
		if (_marked is null)
		{
			// Before the caret: blanks standing for the filler and the line the keyboard cannot see.
			int from = Math.Max(Index(range.Start), -Filler), to = Math.Min(Index(range.End), _cursor);
			return to > from ? new string(' ', to - from) : "";
		}
		int start = Math.Clamp(Index(range.Start) - _cursor, 0, _marked.Length);
		int end = Math.Clamp(Index(range.End) - _cursor, start, _marked.Length);
		return _marked[start..end];
	}

	public void ReplaceText(UITextRange range, string text)
	{
		// Do not treat a system rewrite of committed terminal input as newly typed text.
		if (_marked is not null || (range.IsEmpty && Index(range.Start) == _cursor)) { InsertText(text); }
	}

	public UITextRange GetTextRange(UITextPosition fromPosition, UITextPosition toPosition) =>
		new Range(Math.Min(Index(fromPosition), Index(toPosition)), Math.Max(Index(fromPosition), Index(toPosition)));

	public UITextPosition GetPosition(UITextPosition fromPosition, nint offset) =>
		Index(fromPosition) + offset is var next && next >= -Filler && next <= End ? new Position((int)next) : null!;

	public UITextPosition GetPosition(UITextPosition fromPosition, UITextLayoutDirection inDirection, nint offset) =>
		GetPosition(fromPosition, Left(inDirection) ? -offset : offset);

	public NSComparisonResult ComparePosition(UITextPosition first, UITextPosition second) =>
		(NSComparisonResult)Index(first).CompareTo(Index(second));

	public nint GetOffsetFromPosition(UITextPosition from, UITextPosition to) => Index(to) - Index(from);
	public UITextPosition GetPositionWithinRange(UITextRange range, UITextLayoutDirection direction) =>
		Left(direction) ? range.Start : range.End;

	public UITextRange GetCharacterRange(UITextPosition position, UITextLayoutDirection direction) =>
		GetPosition(position, direction, 1) is { } next ? GetTextRange(position, next) : null!;

	public NSWritingDirection GetBaseWritingDirection(UITextPosition position, UITextStorageDirection direction) =>
		NSWritingDirection.LeftToRight;

	public void SetBaseWritingDirectionforRange(NSWritingDirection writingDirection, UITextRange range) { }
	public CGRect GetFirstRectForRange(UITextRange range) => GetCaretRectForPosition(range.Start);
	public CGRect GetCaretRectForPosition(UITextPosition? position) => new(20 + (Index(position) - Selection) * Step, 0, 1, 30);
	public UITextSelectionRect[] GetSelectionRects(UITextRange range) => [];
	public UITextPosition GetClosestPositionToPoint(CGPoint point) => GetClosestPositionToPoint(point, new Range(0, End));
	public UITextPosition GetClosestPositionToPoint(CGPoint point, UITextRange range) =>
		new Position(Math.Clamp(Selection + (int)Math.Round((point.X - 20) / Step), Index(range.Start), Index(range.End)));
	public UITextRange GetCharacterRangeAtPoint(CGPoint point) =>
		GetCharacterRange(GetClosestPositionToPoint(point), UITextLayoutDirection.Right) ?? new Range(End, End);

	[Export("autocapitalizationType")]
	public UITextAutocapitalizationType AutocapitalizationType => UITextAutocapitalizationType.None;
	[Export("autocorrectionType")]
	public UITextAutocorrectionType AutocorrectionType => UITextAutocorrectionType.No;
	[Export("spellCheckingType")]
	public UITextSpellCheckingType SpellCheckingType => UITextSpellCheckingType.No;
	[Export("inlinePredictionType")]
	public UITextInlinePredictionType InlinePredictionType => OperatingSystem.IsIOSVersionAtLeast(17) ? UITextInlinePredictionType.No : default;
	[Export("smartQuotesType")]
	public UITextSmartQuotesType SmartQuotesType => UITextSmartQuotesType.No;
	[Export("smartDashesType")]
	public UITextSmartDashesType SmartDashesType => UITextSmartDashesType.No;
	[Export("smartInsertDeleteType")]
	public UITextSmartInsertDeleteType SmartInsertDeleteType => UITextSmartInsertDeleteType.No;
	[Export("textContentType")]
	public NSString TextContentType => new("");

	public override bool CanPerform(Selector action, NSObject? sender) => action.Name == "paste:"
		? ClipboardImage.Kinds() is var kinds && (kinds.Text || kinds.Image) : base.CanPerform(action, sender);

	public override void Paste(NSObject? sender) => _owner.Paste();

	public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
	{
		foreach (var press in presses)
		{
			if (press.Key is { } key && Deliver(key)) { _handled.Add(press); }
			else { base.PressesBegan(new NSSet<UIPress>(press), evt); }
		}
	}

	public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
	{
		foreach (var press in presses)
		{
			if (!_handled.Remove(press)) { base.PressesEnded(new NSSet<UIPress>(press), evt); }
		}
	}

	public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
	{
		foreach (var press in presses)
		{
			if (!_handled.Remove(press)) { base.PressesCancelled(new NSSet<UIPress>(press), evt); }
		}
	}

	private bool Deliver(UIKey key)
	{
		if (!IsFirstResponder) { return false; }
		bool control = key.ModifierFlags.HasFlag(UIKeyModifierFlags.Control);
		bool alt = key.ModifierFlags.HasFlag(UIKeyModifierFlags.Alternate);
		bool shift = key.ModifierFlags.HasFlag(UIKeyModifierFlags.Shift);
		if (key.ModifierFlags.HasFlag(UIKeyModifierFlags.Command))
		{
			if (!string.Equals(key.CharactersIgnoringModifiers, "v", StringComparison.OrdinalIgnoreCase)) { return false; }
			_owner.Paste();
			return true;
		}
		string? named = (int)key.KeyCode switch
		{
			79 => "right", 80 => "left", 81 => "down", 82 => "up",
			74 => "home", 77 => "end", 41 => "esc", 43 => "tab", 76 => "delete",
			40 or 88 => "enter", 42 => "backspace", _ => null,
		};
		if (named == "backspace" && !control && !alt && !shift) { DeleteBackward(); return true; }
		if (named == "enter") { _cursor = _end = 0; }
		if (named == "left") { _cursor = Math.Max(0, _cursor - 1); }
		if (named == "right") { _cursor = Math.Min(_end, _cursor + 1); }
		if (named is null && (control || alt) && key.CharactersIgnoringModifiers is { Length: 1 } character && !char.IsControl(character[0]))
		{
			named = character;
		}
		if (named is null) { return false; }
		_owner.HardwareKey(named, control, alt, shift);
		return true;
	}
}
