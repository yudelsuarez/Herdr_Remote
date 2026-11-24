using System.Diagnostics;
using HerdrRemote;
using Languages;

namespace Popups;

// Bottom-sheet text form for renaming and provider values: optional label, hint and initial text,
// a status note, Cancel and a primary action. While the async action runs, show "Working…" and dim the primary.
// Errors wrap in Need colors and keep the sheet open; success closes it with the result. FocusField selects all text.
public sealed class FormSheet : ContentView
{
	// Error keeps the sheet open; otherwise return Result and close it.
	public sealed record Outcome(string? Error, string? Result = null);

	// Primary labels the button; Act handles its text. Label appears above the field and Hint below it.
	// A null Text preserves the field's current value.
	public sealed record Form(string Title, string Primary, Func<string, Task<Outcome>> Act,
		string? Label = null, string? Hint = null, string? Text = null);

	private readonly Action<string?> _close;
	private readonly Label _title = new() { Style = App.Res<Style>("Sht.Title"), LineBreakMode = LineBreakMode.WordWrap };
	// Disable spell check and prediction so iOS cannot replace a name or provider value when Save is tapped,
	// consistent with connection, search and terminal input fields.
	private readonly Entry _entry = new() { Style = App.Res<Style>("Fld.Entry"), ReturnType = ReturnType.Go, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
	private readonly Border _field;
	private readonly Label _label = new() { Style = App.Res<Style>("Fld.Label") };
	private readonly Label _hint = new() { Style = App.Res<Style>("Fld.Hint") };
	private readonly Label _note = new() { Style = App.Res<Style>("FSheet.Note"), IsVisible = false };
	private readonly Button _cancel = new() { Style = App.Res<Style>("Btn.Outline"), Text = i18n.Translate("Cancel") };
	private readonly Button _primary = new() { Style = App.Res<Style>("Btn.Primary") };
	private readonly Form _form;
	private bool _busy;
	private bool _closed;
	private BottomSheet? _host;

	private FormSheet(Form form, Action<string?> close)
	{
		_close = result =>
		{
			if (_closed) { return; }
			_closed = true;
			close(result);
		};
		_form = form;
		_field = new Border { Style = App.Res<Style>("Fld"), Content = _entry };
		_entry.Completed += (_, _) => Go();
		_entry.TextChanged += (_, _) =>
		{
			// Match herdr-app: clear the error when the user edits the text.
			if (!_closed && !_busy && _note.IsVisible) { Note(null, false); }
		};
		// Bind the host after parenting and relay Shown only after its slide-up animation finishes.
		ParentChanged += (_, _) =>
		{
			if (_host is not null || Host() is not { } host) { return; }
			_host = host;
			host.Closing += (_, _) => _closed = true;
			if (host.IsClosing) { _closed = true; }
			host.Shown += (_, _) =>
			{
				if (!_closed) { Shown?.Invoke(); }
			};
		};
		_cancel.Clicked += (_, _) => _close(null);
		_primary.Clicked += (_, _) => Go();
		HorizontalStackLayout buttons = new() { Style = App.Res<Style>("FSheet.Buttons"), Children = { _cancel, _primary } };
		Content = new VerticalStackLayout
		{
			Style = App.Res<Style>("FSheet"),
			Children = { _title, _label, _field, _hint, _note, buttons },
		};
		Apply();
	}

	// Opens the form; the result is the action's Result, null on Cancel or the dimmed page. opened receives the
	// sheet so Rename can focus its field on Shown.
	public static Task<string?> PopAsync(Form form, Action<FormSheet>? opened = null, Func<ContentPage, Layout, bool>? CanInsert = null)
		=> PopupsHandle.PopBottomSheetAsync<string?>(close =>
		{
			FormSheet sheet = new(form, close);
			opened?.Invoke(sheet);
			return sheet;
		}, CanInsert: CanInsert);

	// Raised once the sheet has slid up on screen, not after it was closed (Rename focuses its field then).
	public event Action? Shown;

	// Focus and select all text, matching herdr-app's rename prompt.
	public void FocusField()
	{
		if (_closed) { return; }
		_entry.Focus();
		// Defer selection until native focus settles; setting it in the same pass loses the selection.
		Dispatcher.Dispatch(() =>
		{
			if (_closed) { return; }
			_entry.CursorPosition = 0;
			_entry.SelectionLength = _entry.Text?.Length ?? 0;
		});
	}

	private void Apply()
	{
		_title.Text = _form.Title;
		if (_form.Text is not null) { _entry.Text = _form.Text; }
		_label.Text = _form.Label ?? "";
		_label.IsVisible = _form.Label is not null;
		_hint.Text = _form.Hint ?? "";
		_hint.IsVisible = _form.Hint is not null;
		SemanticProperties.SetDescription(_entry, _form.Label ?? "");
		_primary.Text = _form.Primary;
		Busy(false);
		Note(null, false);
	}

	private async void Go()
	{
		if (_busy || _closed) { return; }
		Busy(true);
		Note(i18n.Translate("Working…"), false);
		Outcome outcome;
		try
		{
			outcome = await _form.Act(_entry.Text ?? "");
		}
		catch (Exception ex)
		{
			// The caller turns its failures into an Outcome; this is a last guard so the sheet never stays on "Working…".
			Debug.WriteLine($"[FormSheet] action failed: {ex}");
			outcome = new Outcome(i18n.Translate("Something went wrong, please try again"));
		}
		if (_closed) { return; }
		Busy(false);
		if (outcome.Error is not null)
		{
			Note(outcome.Error, true);
			return;
		}
		Note(null, false);
		_close(outcome.Result);
	}

	private void Busy(bool busy)
	{
		_busy = busy;
		_primary.Opacity = busy ? 0.5 : 1;
		_entry.IsEnabled = !busy;
	}

	private void Note(string? text, bool need)
	{
		_note.IsVisible = text is not null;
		_note.Text = text ?? "";
		_note.Style = App.Res<Style>(need ? "FSheet.Error" : "FSheet.Note");
		Refit();
	}

	// Refit the bottom sheet after its visible content changes.
	private void Refit() => Host()?.Refit();

	// Find the containing sheet; return null before the form is hosted.
	private BottomSheet? Host()
	{
		Element? host = Parent;
		while (host is not null and not BottomSheet) { host = host.Parent; }
		return host as BottomSheet;
	}
}
