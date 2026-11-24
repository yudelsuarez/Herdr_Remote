using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using HerdrRemote.Remote;
using NativeView = Android.Views.View;

namespace HerdrRemote.Pages;

internal sealed class AndroidTerminalKeys(Context context, TerminalKeys owner) : NativeView(context)
{
	private Connection? _connection;
	private InputMethodManager? Ime => Context?.GetSystemService(Context.InputMethodService) as InputMethodManager;

	internal void Open()
	{
		FocusableInTouchMode = true;
		if (RequestFocus() && Ime is { } ime)
		{
			// The retained pane can keep Android's old connection after Close invalidates it.
			ime.RestartInput(this);
			ime.ShowSoftInput(this, ShowFlags.Implicit);
		}
	}

	internal void Close()
	{
		_connection = null;
		if (!HasFocus) { return; }
		Ime?.HideSoftInputFromWindow(WindowToken, HideSoftInputFlags.None);
		Focusable = false;
	}

	protected override void OnFocusChanged(bool gainFocus, FocusSearchDirection direction, global::Android.Graphics.Rect? previouslyFocusedRect)
	{
		if (!gainFocus) { _connection = null; }
		base.OnFocusChanged(gainFocus, direction, previouslyFocusedRect);
	}

	internal void ResetComposition() => _connection?.Reset();

	public override bool OnCheckIsTextEditor() => true;

	public override IInputConnection OnCreateInputConnection(EditorInfo? outAttrs)
	{
		if (outAttrs is not null)
		{
			outAttrs.InputType = InputTypes.ClassText;
			outAttrs.ImeOptions = (ImeFlags)ImeAction.None | ImeFlags.NoExtractUi | ImeFlags.NoPersonalizedLearning;
			outAttrs.InitialSelStart = outAttrs.InitialSelEnd = 0;
		}
		return _connection = new Connection(this);
	}

	// Gboard can consume navigation before OnKeyDown; terminal shortcuts belong to the terminal.
	public override bool OnKeyPreIme(Keycode keyCode, KeyEvent? e)
	{
		if (HasFocus && e is not null && (NamedKey(keyCode) is "left" or "right" or "up" or "down" or "home" or "end" or "esc" or "tab"
			|| e.IsCtrlPressed || e.IsAltPressed || e.IsMetaPressed))
		{
			if (e.Action == KeyEventActions.Down && Deliver(keyCode, e)) { return true; }
		}
		return base.OnKeyPreIme(keyCode, e);
	}

	public override bool OnKeyDown(Keycode keyCode, KeyEvent? e) =>
		e is not null && Deliver(keyCode, e) || base.OnKeyDown(keyCode, e);

	private bool Deliver(Keycode code, KeyEvent key)
	{
		if (!HasFocus) { return false; }
		bool control = key.IsCtrlPressed, alt = key.IsAltPressed, shift = key.IsShiftPressed;
		if (code == Keycode.V && (control || key.IsMetaPressed))
		{
			if (key.RepeatCount == 0) { Paste(); }
			return true;
		}
		string? named = NamedKey(code);
		if (named is not null) { ResetComposition(); owner.HardwareKey(named, control, alt, shift); return true; }
		int scalar = control || alt ? key.GetUnicodeChar((MetaKeyStates)0) : key.UnicodeChar;
		if (!System.Text.Rune.TryCreate(scalar, out var rune) || scalar < ' ') { return false; }
		ResetComposition();
		if (control || alt) { owner.HardwareKey(rune.ToString(), control, alt, shift); }
		else { owner.Text(rune.ToString()); }
		return true;
	}

	private static string? NamedKey(Keycode code) => code switch
	{
		Keycode.DpadLeft => "left", Keycode.DpadRight => "right", Keycode.DpadUp => "up", Keycode.DpadDown => "down",
		Keycode.MoveHome => "home", Keycode.MoveEnd => "end",
		Keycode.Escape => "esc", Keycode.Tab => "tab", Keycode.Enter or Keycode.NumpadEnter => "enter",
		Keycode.Del => "backspace", Keycode.ForwardDel => "delete", _ => null,
	};

	private sealed class Connection(AndroidTerminalKeys view) : BaseInputConnection(view, true)
	{
		private readonly TerminalImeWord _word = new(view.DeliverText, view.DeliverCorrectionBackspace);
		private int _batch;
		private bool Current => ReferenceEquals(view._connection, this) && view.HasFocus;

		internal void Reset()
		{
			if (_word.Text.Length == 0) { return; }
			_word.Reset();
			Sync();
		}

		public override bool SetComposingText(Java.Lang.ICharSequence? text, int newCursorPosition) =>
			Replace(text, newCursorPosition, true);

		public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition) =>
			Replace(text, newCursorPosition, false);

		// Android 14+ can replace a range in one call; the base implementation would only edit the shadow buffer.
		public override bool ReplaceText(int start, int end, Java.Lang.ICharSequence text, int newCursorPosition, TextAttribute? textAttribute)
		{
			if (!Current || newCursorPosition != 1 || !_word.Mark(start, end)) { return false; }
			return Replace(text, newCursorPosition, false);
		}

		private bool Replace(Java.Lang.ICharSequence? text, int cursor, bool composing)
		{
			if (!Current || cursor != 1 || !_word.Replace(text?.ToString() ?? "", composing)) { return false; }
			Sync();
			return true;
		}

		public override bool SetComposingRegion(int start, int end)
		{
			if (!Current || !_word.Mark(start, end)) { return false; }
			Sync();
			return true;
		}

		public override bool FinishComposingText()
		{
			if (!Current) { return false; }
			_word.Finish();
			Sync();
			return true;
		}

		public override bool SetSelection(int start, int end) =>
			Current && start == end && end == _word.Text.Length;

		public override bool BeginBatchEdit()
		{
			if (!Current) { return false; }
			_batch++;
			return true;
		}

		public override bool EndBatchEdit()
		{
			if (!Current || _batch == 0) { return false; }
			if (--_batch == 0) { ReportSelection(); }
			return _batch != 0;
		}

		private void Sync()
		{
			if (Editable is not { } editable) { return; }
			base.FinishComposingText();
			using var text = new Java.Lang.String(_word.Text);
			editable.Replace(0, editable.Length(), text);
			global::Android.Text.Selection.SetSelection(editable, _word.Text.Length);
			if (_word.Start >= 0) { base.SetComposingRegion(_word.Start, _word.Text.Length); }
			if (_batch == 0) { ReportSelection(); }
		}

		private void ReportSelection()
		{
			int end = _word.Text.Length;
			view.Ime?.UpdateSelection(view, end, end, _word.Start, _word.Start < 0 ? -1 : end);
		}

		public override bool DeleteSurroundingText(int beforeLength, int afterLength) =>
			Delete(beforeLength, afterLength, false);

		public override bool DeleteSurroundingTextInCodePoints(int beforeLength, int afterLength) =>
			Delete(beforeLength, afterLength, true);

		private bool Delete(int before, int after, bool codePoints)
		{
			if (!Current || !_word.Delete(before, after, codePoints)) { return false; }
			Sync();
			return true;
		}

		public override bool SendKeyEvent(KeyEvent? e) =>
			Current && e is not null && (e.Action != KeyEventActions.Down || view.Deliver(e.KeyCode, e));

		public override bool PerformEditorAction(ImeAction action)
		{
			if (!Current) { return false; }
			Reset();
			view.Key("enter");
			return true;
		}

		public override bool PerformContextMenuAction(int id)
		{
			if (!Current || id != global::Android.Resource.Id.Paste) { return false; }
			view.Paste();
			return true;
		}
	}

	private void DeliverText(string value) => owner.Text(value);
	private void DeliverCorrectionBackspace() => owner.CorrectionBackspace();
	private void Key(string value) => owner.Key(value);
	private void Paste() { ResetComposition(); owner.Paste(); }
}
