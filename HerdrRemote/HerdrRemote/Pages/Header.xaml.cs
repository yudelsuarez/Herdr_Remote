using System.Globalization;
using Microsoft.Maui.Controls.Shapes;

namespace HerdrRemote.Pages;

// The tone of the header action's state dot: None hides it.
public enum HeaderTone { None, Ok, Warn, Late }

// Shared header with optional Back, title or Lead view, subtitle hidden when empty,
// Accessory view, and an optional 40x40 action with a status dot.
public partial class Header : ContentView
{
	public static readonly BindableProperty AccessoryProperty = BindableProperty.Create(nameof(Accessory), typeof(View), typeof(Header), null,
		propertyChanged: (bindable, _, value) => ((Header)bindable).AccessorySlot.Content = (View?)value);

	public static readonly BindableProperty LeadProperty = BindableProperty.Create(nameof(Lead), typeof(View), typeof(Header), null,
		propertyChanged: (bindable, _, value) => ((Header)bindable).ShowLead((View?)value));

	// The whole subtitle text; Lbl_Sub shows it or its fitted prefix with "…" (FitSubtitle).
	private string _sub = "";

	// Cache FitSubtitle's text, available width, and status width; null before the first fit.
	private (string Sub, double Width, double StatusWidth)? _fitted;

	public Header()
	{
		InitializeComponent();
	}

	public void AttachReading() => Reading.Attach();
	public void DetachReading() => Reading.Detach();

	internal ReadingBar TakePaneReading()
	{
		SubLine.IsVisible = false;
		SubRow.IsVisible = false;
		((Grid)Reading.Parent).Remove(Reading);
		return Reading;
	}

	public bool ChromeVisible { set => Chrome.IsVisible = value; }

	public string TitleText { set => Lbl_Title.Text = value; }

	// A title in parts (the viewer's "Image" + " 1 of 1").
	public FormattedString TitleSpans { set => Lbl_Title.FormattedText = value; }

	public string SubtitleText { set => Subtitle(value); }

	// The view between the title and the action (the Reader's voice pill).
	public View? Accessory
	{
		get => (View?)GetValue(AccessoryProperty);
		set => SetValue(AccessoryProperty, value);
	}

	// A view in the title's place (the Pane's pane pill); the subtitle line then runs in its own row under the whole header.
	public View? Lead
	{
		get => (View?)GetValue(LeadProperty);
		set => SetValue(LeadProperty, value);
	}

	// The width the Lead view may take (the room between the back key and the Accessory), on every change.
	public event Action<double>? LeadRoomChanged;

	// Back precedes the title on pushed pages; the app provides navigation instead of using system Back.
	public bool BackVisible
	{
		get => Btn_Back.IsVisible;
		set
		{
			Btn_Back.IsVisible = value;
			SemanticProperties.SetDescription(Btn_Back, Languages.i18n.Translate("Back"));
		}
	}

	public event EventHandler? BackTapped;

	private void Back_Clicked(object? sender, EventArgs e) => BackTapped?.Invoke(this, EventArgs.Empty);

	private void ShowLead(View? lead)
	{
		LeadSlot.Content = lead;
		LeadSlot.IsVisible = lead is not null;
		TitleColumn.IsVisible = lead is null;
		if (lead is null || SubLine.Parent == SubRow) { return; }
		TitleColumn.Remove(SubLine);
		SubLine.Style = App.Res<Style>("Hdr.SubRow");
		SubRow.Content = SubLine;
		SubRow.IsVisible = true;
	}

	private void LeadSlot_SizeChanged(object? sender, EventArgs e) => LeadRoomChanged?.Invoke(LeadSlot.Width);

	// The subtitle line: text (it alone ends in "…" when the line is too narrow), then " · " and the status word in its color key, in
	// the part that is never cut.
	public void Subtitle(string text, string? status = null, string? statusColorKey = null)
	{
		_sub = text;
		SemanticProperties.SetDescription(Lbl_Sub, text);
		FormattedString parts = new();
		if (!string.IsNullOrEmpty(status))
		{
			if (text.Length > 0) { parts.Spans.Add(new Span { Text = " · " }); }
			Span word = new() { Text = status };
			if (statusColorKey is not null) { word.SetAppThemeColor(Span.TextColorProperty, App.Res<Color>(statusColorKey), App.Res<Color>(statusColorKey + "Dark")); }
			parts.Spans.Add(word);
		}
		Lbl_SubStatus.FormattedText = parts;
		Lbl_SubStatus.IsVisible = parts.Spans.Count > 0;
		SubLine.IsVisible = text.Length > 0 || parts.Spans.Count > 0;
		FitSubtitle();
	}

	private void TitleColumn_SizeChanged(object? sender, EventArgs e) => FitSubtitle();

	// Reserve the full status width. Fit subtitle text into the remaining title-column width using a binary
	// search over text elements, then size the label to its drawn text so the status immediately follows "…".
	// A width cap alone can leave a one-glyph gap because platform truncation only draws whole glyphs.
	private void FitSubtitle()
	{
		double width = SubLine.Parent == SubRow ? SubRow.Width : TitleColumn.Width;
		if (width <= 0) { return; }
		double statusWidth = Lbl_SubStatus.IsVisible ? Lbl_SubStatus.Measure(double.PositiveInfinity, double.PositiveInfinity).Width : 0;
		// Reuse the fit for identical text, available width, and status width; avoid measuring again.
		if (_fitted == (_sub, width, statusWidth)) { return; }
		_fitted = (_sub, width, statusWidth);
		double room = width - statusWidth;
		double Drawn() => Lbl_Sub.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
		Lbl_Sub.Text = _sub;
		// An empty text has nothing to cut, even when the status part alone is wider than the column.
		if (_sub.Length == 0 || Drawn() <= room) { return; }
		// starts[k] is where text element k begins; a prefix of k elements never splits a surrogate pair or a combining mark.
		// A prefix that ends in a space drops it, so the "…" follows the last word ("Claude…", not "Claude …").
		int[] starts = StringInfo.ParseCombiningCharacters(_sub);
		string Cut(int elements) => _sub[..starts[elements]].TrimEnd() + "…";
		int fits = 0, over = starts.Length;
		while (over - fits > 1)
		{
			int mid = (fits + over) / 2;
			Lbl_Sub.Text = Cut(mid);
			if (Drawn() <= room) { fits = mid; } else { over = mid; }
		}
		Lbl_Sub.Text = Cut(fits);
	}

	// Icons-font glyph of the action; empty means no action.
	public string ActionGlyph
	{
		set
		{
			Lbl_Action.Text = value;
			Action.IsVisible = !string.IsNullOrEmpty(value);
		}
	}

	public string ActionDescription { set => SemanticProperties.SetDescription(Action, value); }

	// The last tone painted, null before the first: an equal tone keeps its dot (its colors follow the theme by themselves).
	private HeaderTone? _tone;

	public HeaderTone ActionTone
	{
		set
		{
			if (_tone == value) { return; }
			_tone = value;
			PaintDot(Dot, value);
		}
	}

	// The one tone → color mapping of the action's state dot.
	private static void PaintDot(Ellipse dot, HeaderTone tone)
	{
		string? ink = tone switch { HeaderTone.Ok => "Ok", HeaderTone.Warn => "Work", HeaderTone.Late => "Need", _ => null };
		dot.IsVisible = ink is not null;
		if (ink is not null) { dot.SetAppTheme<Brush>(Shape.FillProperty, new SolidColorBrush(App.Res<Color>(ink)), new SolidColorBrush(App.Res<Color>(ink + "Dark"))); }
	}

	public event EventHandler? ActionTapped;

	private void Action_Tapped(object? sender, TappedEventArgs e) => ActionTapped?.Invoke(this, EventArgs.Empty);
}
