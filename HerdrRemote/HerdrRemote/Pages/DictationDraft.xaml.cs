using HerdrRemote.Services;
using Languages;

namespace HerdrRemote.Pages;

// Render DictationSession and raise user actions: send, discard, or editor focus.
public partial class DictationDraft : ContentView
{
	private const int LiveTail = 280;
	private bool _setting;
	private DictationState _state = DictationState.Closed;

	public DictationDraft()
	{
		InitializeComponent();
		ApplyLanguage();
#if ANDROID
		// No full-screen landscape editor (it would cover the terminal), as the Find field.
		Field.HandlerChanged += (_, _) =>
		{
			if (Field.Handler?.PlatformView is Android.Widget.EditText edit)
			{
				edit.ViewAttachedToWindow += (_, _) => edit.ImeOptions |= (Android.Views.InputMethods.ImeAction)Android.Views.InputMethods.ImeFlags.NoExtractUi;
			}
		};
#endif
	}

	// Send the draft (the paste), then Enter when asked; while listening, listening stops first.
	public event Action<bool>? SendTapped;

	public event Action? DiscardTapped;

	public event Action? LanguageTapped;

	public event Action? RetryTapped;

	// A tap on the text while it listens: stop listening, then the editor takes focus.
	public event Action? LiveTapped;

	public string Text => Field.Text ?? "";

	public bool IsFieldFocused => Field.IsFocused;

	public int Caret => Math.Clamp(Field.CursorPosition, 0, Text.Length);

	public void ApplyLanguage()
	{
		Field.Placeholder = i18n.Translate("Speak or type here");
		Btn_Send.Text = i18n.Translate("Send");
		Btn_SendEnter.Text = i18n.Translate("Send and Enter");
		Btn_Retry.Text = i18n.Translate("Retry");
		SemanticProperties.SetDescription(Btn_Discard, i18n.Translate("Discard"));
		SemanticProperties.SetDescription(Btn_Language, i18n.Translate("Dictation language"));
		SemanticProperties.SetDescription(Field, i18n.Translate("Dictation"));
	}

	// Render only when displayed session values change.
	internal void Render(DictationSession session)
	{
		DictationState state = session.State;
		_state = state;
		IsVisible = state != DictationState.Closed;
		if (state == DictationState.Closed) { return; }
		bool listening = state == DictationState.Listening;
		bool transcribing = state == DictationState.Transcribing;
		Lbl_State.Text = listening ? i18n.Translate("Listening…") : transcribing ? i18n.Translate("Transcribing…") : "";
		Meter.IsVisible = listening;
		Lbl_Time.IsVisible = listening;
		if (listening)
		{
			TimeSpan elapsed = session.Elapsed;
			Lbl_Time.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
			Lbl_Time.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>(session.NearLimit ? "Work" : "Ink2"), App.Res<Color>(session.NearLimit ? "WorkDark" : "Ink2Dark"));
		}
		Btn_Language.Text = session.LanguageChip;
		Lbl_Provider.Text = session.ProviderName;
		StatusRow.IsVisible = session.Status.Length > 0;
		Lbl_Status.Text = session.Status;
		Lbl_Status.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>(session.StatusIsError ? "Need" : "Ink2"), App.Res<Color>(session.StatusIsError ? "NeedDark" : "Ink2Dark"));
		Btn_Retry.IsVisible = session.CanRetry;
		string text = session.Text;
		LiveBox.IsVisible = listening || transcribing;
		Field.IsVisible = !LiveBox.IsVisible;
		if (listening) { ShowLive(text, session.LiveSpan); }
		else if (!transcribing && Field.Text != text)
		{
			_setting = true;
			Field.Text = text;
			_setting = false;
		}
		bool any = text.Trim().Length > 0;
		Btn_Send.IsEnabled = Btn_SendEnter.IsEnabled = any && !transcribing;
		if (_focusWhenEditing && state == DictationState.Editing)
		{
			_focusWhenEditing = false;
			Field.Focus();
			// The caret at the end of the text (the editor would put it first), so the next dictation or typing goes on from there.
			Field.CursorPosition = Field.Text?.Length ?? 0;
		}
	}

	// The level meter: five bars lit by the input level, the lowest first.
	public void ShowLevel(float level)
	{
		if (!Meter.IsVisible) { return; }
		for (int i = 0; i < Meter.Children.Count; i++)
		{
			if (Meter.Children[i] is BoxView bar) { bar.Opacity = level * 5 > i + 0.4 ? 1 : 0.25; }
		}
	}

	// Show only the last LiveTail characters, with committed text in the normal ink and live text lighter.
	private void ShowLive(string text, (int Start, int Length) live)
	{
		int from = Math.Max(0, text.Length - LiveTail);
		FormattedString formatted = new();
		if (from > 0) { formatted.Spans.Add(new Span { Text = "…" }); }
		int split = Math.Clamp(live.Length == 0 ? text.Length : live.Start, from, text.Length);
		if (split > from) { formatted.Spans.Add(new Span { Text = text[from..split] }); }
		if (split < text.Length)
		{
			Span light = new() { Text = text[split..] };
			light.SetAppThemeColor(Span.TextColorProperty, App.Res<Color>("Ink3"), App.Res<Color>("Ink3Dark"));
			formatted.Spans.Add(light);
		}
		Lbl_Live.FormattedText = formatted;
	}

	// A tap on the live text: once listening has stopped and the editor shows, it takes focus (the system keyboard comes up for it).
	public void FocusWhenEditing() => _focusWhenEditing = true;

	private bool _focusWhenEditing;

	private void Field_TextChanged(object? sender, TextChangedEventArgs e)
	{
		if (_setting || _state != DictationState.Editing) { return; }
		App.Dictation.SetText(e.NewTextValue ?? "");
		bool any = (e.NewTextValue ?? "").Trim().Length > 0;
		Btn_Send.IsEnabled = Btn_SendEnter.IsEnabled = any;
	}

	private void Live_Tapped(object? sender, TappedEventArgs e) => LiveTapped?.Invoke();
	private void Send_Clicked(object? sender, EventArgs e) => SendTapped?.Invoke(false);
	private void SendEnter_Clicked(object? sender, EventArgs e) => SendTapped?.Invoke(true);
	private void Discard_Clicked(object? sender, EventArgs e) => DiscardTapped?.Invoke();
	private void Language_Clicked(object? sender, EventArgs e) => LanguageTapped?.Invoke();
	private void Retry_Clicked(object? sender, EventArgs e) => RetryTapped?.Invoke();
}
