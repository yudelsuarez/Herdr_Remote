namespace HerdrRemote.Pages;

// The color of a row's value: the default ink, or a state's color.
public enum ValueTone { None, Ok, Work, Need }

// One row: glyph (icons font), title, optional line under it, value and chevron; Key names the row for the host's tap handler.
// MonoValue draws the value in mono (a fingerprint).
public sealed record InfoRow(string Glyph, string Title, string Value, bool Divider, string Sub = "", bool Chevron = false, string Key = "", ValueTone ValueTone = ValueTone.None, bool MonoValue = false)
{
	public bool HasSub => Sub.Length > 0;
	public bool HasValue => Value.Length > 0;
	public Style RowStyle => App.Res<Style>(HasSub ? "Lst.RowSub" : "Lst.Row");
	public Style ValueStyle => App.Res<Style>(MonoValue ? "Lst.ValueMono" : ValueTone switch
	{
		ValueTone.Ok => "Lst.ValueOk",
		ValueTone.Work => "Lst.ValueWork",
		ValueTone.Need => "Lst.ValueNeed",
		_ => "Lst.Value",
	});
}

public partial class InfoList : ContentView
{
	// Raised with the row view as the sender; its BindingContext is the InfoRow.
	public event EventHandler<TappedEventArgs>? Tapped;

	public InfoList()
	{
		InitializeComponent();
	}

	// Identical rows need no rebinding on a same-page reload.
	public void SetItems(IReadOnlyList<InfoRow> rows)
	{
		if (BindableLayout.GetItemsSource(Stack) is IEnumerable<InfoRow> shown && shown.SequenceEqual(rows)) { return; }
		BindableLayout.SetItemsSource(Stack, rows);
	}

	// An Auto value column can consume the row and squeeze the title to one letter per line.
	// Reserve up to TitleShare for the title and wrap the value in the remaining width.
	private const double TitleShare = 0.5;

	private void Row_SizeChanged(object? sender, EventArgs e)
	{
		// A row whose style is being swapped by a rebind (Lst.Row <-> Lst.RowSub) has no columns for a moment: the next layout measures it.
		if (sender is not Grid { Width: > 0, ColumnDefinitions.Count: > 0 } row || row.FindByName<View>("Title_Stack") is not { } title || row.FindByName<HorizontalStackLayout>("Value_Stack") is not { } stack
			|| stack.Children.Count < 2 || stack.Children[0] is not Label value || stack.Children[1] is not View chevron) { return; }
		double free = row.Width - row.Padding.HorizontalThickness - row.ColumnDefinitions[0].Width.Value - 2 * row.ColumnSpacing;
		// Add 1 dp for pixel rounding: at density 2.8125, a 65.78 dp title can receive only 65.56 dp
		// when the value column rounds up, clipping the final letter.
		double titleWidth = Math.Min(title.Measure(double.PositiveInfinity, double.PositiveInfinity).Width + 1, free * TitleShare);
		double chevronWidth = chevron.IsVisible ? stack.Spacing + chevron.Measure(double.PositiveInfinity, double.PositiveInfinity).Width : 0;
		// At large text the title is on its own line, so the value line has the whole row.
		value.MaximumWidthRequest = Math.Max(1, free - (App.LargeText ? 0 : titleWidth) - chevronWidth);
		// Unconstrained measurements cache one-line heights. Invalidate them before arranging at a narrower width,
		// or the second title line is clipped.
		((IView)title).InvalidateMeasure();
		((IView)chevron).InvalidateMeasure();
		// iOS can arrange immediately using measurements from before the width limit changed, without another measure
		// when row size is unchanged. Measure now at the final width to avoid a one-line value height and clipped title.
		row.Measure(row.Width, double.PositiveInfinity);
	}

	// A language rebind retains views, so unchanged row sizes do not raise SizeChanged.
	// Recalculate the split after new text reaches the row's children.
	private void Row_Rebound(object? sender, EventArgs e)
	{
		if (sender is Grid { Width: > 0 } row) { Dispatcher.Dispatch(() => Row_SizeChanged(row, EventArgs.Empty)); }
	}

	private void Row_Tapped(object? sender, TappedEventArgs e) => Tapped?.Invoke(sender, e);
}
