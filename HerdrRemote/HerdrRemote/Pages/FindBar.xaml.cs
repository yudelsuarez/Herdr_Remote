using System.Globalization;
using Languages;

namespace HerdrRemote.Pages;

// Find in terminal's bar: the Pane screen floats it below the header (Pane.OpenFind) and runs the search on its
// terminal (TerminalView.Find, FindStep, FindClose); the terminal's answer comes back through ShowFound. The field owns the system
// keyboard while it is focused; a tap on the terminal gives the keyboard back to the terminal's hidden field (Pane.Screen_Tapped).
public partial class FindBar : ContentView
{
	private string? _fromSearchAll;

	public FindBar()
	{
		InitializeComponent();
		ApplyLanguage();
#if ANDROID
		// No full-screen landscape editor (it would cover the terminal and its marks), as the terminal's own field (Pane). MAUI sets
		// ImeOptions each time the field attaches to the window, so the flag is added after it.
		Field.HandlerChanged += (_, _) =>
		{
			if (Field.Handler?.PlatformView is Android.Widget.EditText edit)
			{
				edit.ViewAttachedToWindow += (_, _) => edit.ImeOptions |= (Android.Views.InputMethods.ImeAction)Android.Views.InputMethods.ImeFlags.NoExtractUi;
			}
		};
#endif
	}

	// The text to find, on every change of the field ("" while empty).
	public event Action<string>? Searched;

	// -1 previous (older), +1 next (newer).
	public event Action<int>? Stepped;

	public event Action? Closed;

	public string Text => Field.Text ?? "";

	public bool IsFieldFocused => Field.IsFocused;

	public void ApplyLanguage()
	{
		Field.Placeholder = i18n.Translate("Find in terminal");
		SemanticProperties.SetDescription(Btn_Previous, i18n.Translate("Previous"));
		SemanticProperties.SetDescription(Btn_Next, i18n.Translate("Next"));
		SemanticProperties.SetDescription(Btn_Close, i18n.Translate("Close"));
	}

	// An empty field and count, the field focused (the system keyboard comes up for it).
	public void Start()
	{
		Clear();
		Field.Focus();
	}

	// The bar open with a text already in it (Search all panes), not focused: the field's change starts the search.
	public void Show(string text)
	{
		Clear();
		_fromSearchAll = text;
		Field.Text = text;
	}

	// Lease cleanup must not bring the keyboard back up.
	public void Clear()
	{
		_fromSearchAll = null;
		Field.Text = "";
		Lbl_Count.Text = "";
	}

	// The terminal's answer: "i / n", "No matches", or nothing while the field is empty.
	public void ShowFound(string text, int index, int total)
	{
		if (text != Text) { return; }
		Lbl_Count.Text = total > 0 ? $"{index.ToString(CultureInfo.CurrentCulture)} / {total.ToString(CultureInfo.CurrentCulture)}"
			: text.Length == 0 ? "" : i18n.Translate(text == _fromSearchAll ? "Not shown here" : "No matches");
	}

	private void Field_TextChanged(object? sender, TextChangedEventArgs e)
	{
		if (e.NewTextValue != _fromSearchAll) { _fromSearchAll = null; }
		Lbl_Count.Text = "";
		Searched?.Invoke(e.NewTextValue ?? "");
	}

	private void Previous_Clicked(object? sender, EventArgs e) => Stepped?.Invoke(-1);

	private void Next_Clicked(object? sender, EventArgs e) => Stepped?.Invoke(1);

	private void Close_Clicked(object? sender, EventArgs e) => Closed?.Invoke();
}
