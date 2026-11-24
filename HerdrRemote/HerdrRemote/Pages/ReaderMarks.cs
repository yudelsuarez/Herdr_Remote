using Popups;
using Microsoft.Maui.Platform;
#if ANDROID
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
#elif IOS
using Foundation;
using UIKit;
#endif

namespace HerdrRemote.Pages;

public partial class Reader
{
	private sealed record ReaderPlan(object Source, string Text, (int Start, int Length)?[] Ranges);
	private ReaderPlan? _readerPlan;
	private int _markedFrom = -1, _markedStrong = -1;

#if ANDROID
	private sealed class ReaderLayoutListener(Action check) : Java.Lang.Object,
		ViewTreeObserver.IOnGlobalLayoutListener, ViewTreeObserver.IOnPreDrawListener
	{
		public void OnGlobalLayout() => check();
		public bool OnPreDraw() { check(); return true; }
	}
#endif

	private async Task<bool> PrepareReaderStartAsync(object? view, string text, (int Start, int Length)? range)
	{
		var pageWindow = _visibleWindow;
		bool IsCurrent() => pageWindow is not null && _visibleWindow == pageWindow && ReferenceEquals(Ed_Text.Handler?.PlatformView, view) && Ed_Text.Text == text;
		if (view is null || range is null)
		{
			await PopupsHandle.HideKeyboardAsync();
			return IsCurrent();
		}

		var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		void Cancel(object? sender, EventArgs e) => settled.TrySetResult(false);
		void HandlerChanging(object? sender, HandlerChangingEventArgs e) => settled.TrySetResult(false);
		Disappearing += Cancel;
		Ed_Text.HandlerChanging += HandlerChanging;
		if (pageWindow is not null) { pageWindow.Stopped += Cancel; }
#if ANDROID
		var editor = (EditText)view;
		ViewTreeObserver? registered = null;
		void CheckLayout()
		{
			if (settled.Task.IsCompleted) { return; }
			if (!IsCurrent() || !editor.IsAttachedToWindow) { settled.TrySetResult(false); return; }
			if (Tabs.IsVisible && editor.IsLaidOut && editor.Layout is not null && !editor.IsLayoutRequested &&
				editor.RootView is { IsLayoutRequested: false } &&
				ViewCompat.GetRootWindowInsets(editor)?.IsVisible(WindowInsetsCompat.Type.Ime()) == false)
			{
				settled.TrySetResult(true);
			}
		}
		using var listener = new ReaderLayoutListener(CheckLayout);
		void RemoveListener(ViewTreeObserver? observer)
		{
			if (observer is not { IsAlive: true }) { return; }
			observer.RemoveOnGlobalLayoutListener(listener);
			observer.RemoveOnPreDrawListener(listener);
		}
		editor.ViewDetachedFromWindow += Cancel;
#elif IOS
		var editor = (UITextView)view;
		var window = editor.Window;
		var keyboardGuide = window?.KeyboardLayoutGuide;
		// A first responder can use a hardware keyboard. Only the area above the bottom safe area is occupied.
		bool KeyboardVisible() => window is not null && keyboardGuide is not null && keyboardGuide.LayoutFrame.Height > window.SafeAreaInsets.Bottom;
		window?.LayoutIfNeeded();
		bool keyboardHiding = KeyboardVisible();
		bool prepared = false;
		void CheckLayout()
		{
			if (!prepared || settled.Task.IsCompleted) { return; }
			if (!IsCurrent() || window is null || editor.Window != window) { settled.TrySetResult(false); return; }
			window.LayoutIfNeeded();
			if (Tabs.IsVisible && editor.Bounds.Height > 0 && (!keyboardHiding || !KeyboardVisible())) { settled.TrySetResult(true); }
		}
		void LayoutChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(CheckLayout);
		// The layout can change during dismissal, before the keyboard animation ends.
		using var keyboard = keyboardHiding ? UIKeyboard.Notifications.ObserveDidHide((_, _) =>
		{
			keyboardHiding = false;
			Dispatcher.Dispatch(CheckLayout);
		}) : null;
		Ed_Text.SizeChanged += LayoutChanged;
#endif
		try
		{
			await PopupsHandle.HideKeyboardAsync();
			if (!IsCurrent() || settled.Task.IsCompleted) { return false; }
			SetReaderReadOnly(view, true);
#if ANDROID
			CheckLayout();
			if (!settled.Task.IsCompleted)
			{
				if (editor.ViewTreeObserver is not { IsAlive: true } observer) { return false; }
				registered = observer;
				observer.AddOnGlobalLayoutListener(listener);
				observer.AddOnPreDrawListener(listener);
				editor.Post(CheckLayout);
				editor.Invalidate();
			}
#elif IOS
			prepared = true;
			Dispatcher.Dispatch(CheckLayout);
#else
			settled.TrySetResult(IsCurrent());
#endif
			if (!await settled.Task || !IsCurrent()) { return false; }
#if ANDROID
			if (!editor.IsAttachedToWindow) { return false; }
#elif IOS
			if (editor.Window != window) { return false; }
#endif
			RevealReaderStart(view, range.Value);
			return true;
		}
		finally
		{
			Disappearing -= Cancel;
			Ed_Text.HandlerChanging -= HandlerChanging;
			if (pageWindow is not null) { pageWindow.Stopped -= Cancel; }
#if ANDROID
			editor.ViewDetachedFromWindow -= Cancel;
			if (registered is not null)
			{
				var current = editor.ViewTreeObserver;
				RemoveListener(current);
				if (!ReferenceEquals(current, registered)) { RemoveListener(registered); }
			}
#elif IOS
			Ed_Text.SizeChanged -= LayoutChanged;
			if (keyboard is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(keyboard); }
#endif
		}
	}

	private static double? ReaderScrollTarget(double top, double bottom, double origin, double height, double contentHeight)
	{
		if (height <= 0 || (top >= origin && bottom <= origin + height)) { return null; }
		double target = top - height / 3;
		if (bottom - top <= height) { target = Math.Max(target, bottom - height); }
		return Math.Clamp(target, 0, Math.Max(0, contentHeight - height));
	}

	private static void RevealReaderStart(object view, (int Start, int Length) range)
	{
#if ANDROID
		if (view is not EditText { Layout: { } layout } editor) { return; }
		int top = layout.GetLineTop(layout.GetLineForOffset(range.Start));
		int bottom = layout.GetLineBottom(layout.GetLineForOffset(range.Start + range.Length - 1));
		int height = editor.Height - editor.TotalPaddingTop - editor.TotalPaddingBottom;
		if (ReaderScrollTarget(top, bottom, editor.ScrollY, height, layout.Height) is { } target)
		{
			editor.ScrollTo(editor.ScrollX, (int)Math.Ceiling(target));
		}
#elif IOS
		if (view is not UITextView editor ||
			editor.GetPosition(editor.BeginningOfDocument, range.Start) is not { } start ||
			editor.GetPosition(start, range.Length) is not { } end ||
			editor.GetTextRange(start, end) is not { } span) { return; }
		double top = double.PositiveInfinity, bottom = double.NegativeInfinity;
		foreach (var selection in editor.GetSelectionRects(span))
		{
			var rect = selection.Rect;
			if (rect.Height <= 0) { continue; }
			top = Math.Min(top, rect.Top);
			bottom = Math.Max(bottom, rect.Bottom);
		}
		if (top > bottom) { return; }
		var inset = editor.AdjustedContentInset;
		double height = editor.Bounds.Height - inset.Top - inset.Bottom;
		if (ReaderScrollTarget(top, bottom, editor.ContentOffset.Y + inset.Top, height, editor.ContentSize.Height) is { } target)
		{
			editor.SetContentOffset(new CoreGraphics.CGPoint(editor.ContentOffset.X, target - inset.Top), false);
		}
#endif
	}

#if ANDROID
	private readonly Dictionary<int, BackgroundColorSpan> _nativeMarks = [];
	private Action? _restoreEditorInput;

	private sealed class SuppressSpellMarks : Java.Lang.Object, ISpanWatcher
	{
		public void OnSpanAdded(ISpannable? text, Java.Lang.Object? what, int start, int end)
		{
			if (what is SuggestionSpan) { text?.RemoveSpan(what); }
		}

		public void OnSpanRemoved(ISpannable? text, Java.Lang.Object? what, int start, int end) { }
		public void OnSpanChanged(ISpannable? text, Java.Lang.Object? what, int oldStart, int oldEnd, int newStart, int newEnd) { }
	}
#endif

	private void RefreshReaderMarks(bool repaint = false)
	{
		if (_readerPlan is not { } plan) { return; }
		if (plan.Source != _reader.ReaderSource || Ed_Text.Text != plan.Text || !(_reader.IsReading || _reader.CanContinue))
		{
			ClearReaderMarks();
			_readerPlan = null;
			return;
		}
		if (!_reader.IsReading || !_reader.ReaderSourceReady)
		{
			ClearReaderMarks();
			return;
		}
		if (Ed_Text.Handler?.PlatformView is not { } view) { return; }

		if (_markedFrom < 0 && !Ed_Text.IsReadOnly)
		{
			Ed_Text.Unfocus();
			if (_readerPlan != plan) { return; }
			SetReaderReadOnly(view, true);
		}
		int from = _reader.Sentence;
		int strong = _reader.Phase is "playing" or "paused" ? from : -1;
		if (!repaint && from == _markedFrom && strong == _markedStrong) { return; }
		string suffix = Application.Current?.RequestedTheme == AppTheme.Dark ? "Dark" : "";
		Color pending = App.Res<Color>("ReaderPending" + suffix);
		Color speaking = App.Res<Color>("ReaderSpeaking" + suffix);
		void Paint(int i)
		{
			if ((uint)i < (uint)plan.Ranges.Length && plan.Ranges[i] is { } range)
			{
				SetReaderMark(view, range, i, i < from ? null : i == strong ? speaking : pending);
			}
		}

		if (_markedFrom < 0 || repaint)
		{
			int start = _markedFrom < 0 ? from : Math.Min(from, _markedFrom);
			for (int i = start; i < plan.Ranges.Length; i++) { Paint(i); }
		}
		else
		{
			int unchanged = Math.Max(from, _markedFrom);
			for (int i = Math.Min(from, _markedFrom); i < unchanged; i++) { Paint(i); }
			if (strong != _markedStrong)
			{
				if (_markedStrong >= unchanged) { Paint(_markedStrong); }
				if (strong >= unchanged) { Paint(strong); }
			}
		}
		_markedFrom = from;
		_markedStrong = strong;
	}

	private void ClearReaderMarks()
	{
		if (_markedFrom < 0 || _readerPlan is not { } plan) { return; }
		if (Ed_Text.Handler?.PlatformView is { } view)
		{
			for (int i = _markedFrom; i < plan.Ranges.Length; i++)
			{
				if (plan.Ranges[i] is { } range) { SetReaderMark(view, range, i, null); }
			}
			SetReaderReadOnly(view, false);
		}
		_markedFrom = _markedStrong = -1;
	}

	// Retain the native editor and its viewport. Only the background attributes change during playback.
	private void SetReaderReadOnly(object view, bool readOnly)
	{
#if ANDROID
		if (view is EditText editor)
		{
			if (readOnly)
			{
				// MAUI makes the Editor unfocusable, but does not remove its Android input listener.
				var listener = editor.KeyListener;
				var input = editor.InputType;
				editor.KeyListener = null;
				editor.SetRawInputType(InputTypes.Null);
				var text = editor.EditableText;
				SuppressSpellMarks? spellMarks = null;
				if (text is not null)
				{
					// Disabling input leaves existing underlines and does not cancel queued spell-check results.
					spellMarks = new();
					text.SetSpan(spellMarks, 0, text.Length(), SpanTypes.InclusiveInclusive);
					foreach (var span in text.GetSpans(0, text.Length(), Java.Lang.Class.FromType(typeof(SuggestionSpan))) ?? [])
					{
						text.RemoveSpan(span);
					}
				}
				_restoreEditorInput = () =>
				{
					if (spellMarks is not null) { text?.RemoveSpan(spellMarks); spellMarks.Dispose(); }
					editor.KeyListener = listener;
					editor.SetRawInputType(input);
				};
			}
			else { _restoreEditorInput?.Invoke(); _restoreEditorInput = null; }
		}
#elif IOS
		Ed_Text.IsSpellCheckEnabled = !readOnly;
#endif
		Ed_Text.IsReadOnly = readOnly;
	}

	private void SetReaderMark(object view, (int Start, int Length) range, int index, Color? color)
	{
		int end = range.Start + range.Length;
#if ANDROID
		if (view is not EditText { EditableText: { } text }) { return; }
		if (_nativeMarks.Remove(index, out var old)) { text.RemoveSpan(old); old.Dispose(); }
		if (color is not null && end <= text.Length())
		{
			BackgroundColorSpan span = new(color.ToPlatform());
			text.SetSpan(span, range.Start, end, SpanTypes.ExclusiveExclusive);
			_nativeMarks.Add(index, span);
		}
#elif IOS
		if (view is not UITextView { TextStorage: { } text } || end > text.Length) { return; }
		NSRange span = new(range.Start, range.Length);
		if (color is null) { text.RemoveAttribute(UIStringAttributeKey.BackgroundColor, span); }
		else { text.AddAttribute(UIStringAttributeKey.BackgroundColor, color.ToPlatform(), span); }
#endif
	}
}
