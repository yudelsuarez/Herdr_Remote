using HerdrRemote;
using HerdrRemote.Pages;
using Languages;

namespace Popups;

// Render herdr-app-style menus consistently on iOS and Android: title and Cancel, then ordered rows,
// section labels, notes and separators. Rows use an icon or agent mark; destructive rows use Need colors.
// Selection returns the row; Cancel or a scrim tap returns null. Show updates entries and refits the open sheet.
public sealed class MenuSheet : ContentView
{
	public abstract record Entry;

	// Glyph: an icons-font code point; Agent: an agent's program name, drawn with its mark (PaneStrip.AgentMark) in place of a glyph.
	public sealed record Row(string Text, string? Glyph = null, string? Agent = null, bool Need = false) : Entry;

	public sealed record Section(string Text) : Entry;

	public sealed record Note(string Text) : Entry;

	public sealed record Separator : Entry;

	private readonly Grid _entries = new() { RowSpacing = 0, ColumnSpacing = 0 };
	private readonly KeyedRows<Entry> _rows;
	private Entry[] _snapshot;
	private readonly Action<Row?> _close;

	private MenuSheet(string title, IReadOnlyList<Entry> entries, Action<Row?> close, View? lead, string? subtitle)
	{
		_close = close;
		_snapshot = entries.ToArray();
		_rows = new KeyedRows<Entry>(_entries, Identity, BuildEntry, (view, entry) => ((EntryView)view).Rebind(entry), view => ((EntryView)view).Live = false);
		Grid head = new() { Style = App.Res<Style>("Sht.Head") };
		Label titleLabel = new() { Style = App.Res<Style>("Sht.Title"), Text = title };
		if (lead is null && subtitle is null) { head.Add(titleLabel); }
		else
		{
			// Keep the mark, title and subtitle left of Cancel. Middle truncation preserves both
			// the pane address and the end of its folder without overlapping the button.
			titleLabel.MaxLines = 1;
			titleLabel.LineBreakMode = LineBreakMode.TailTruncation;
			VerticalStackLayout titles = new() { VerticalOptions = LayoutOptions.Center, Children = { titleLabel } };
			if (subtitle is not null) { titles.Add(new Label { Style = App.Res<Style>("Lst.Sub"), Text = subtitle, MaxLines = 1, LineBreakMode = LineBreakMode.MiddleTruncation }); }
			Grid named = new() { Style = App.Res<Style>(lead is null ? "Menu.NamedBare" : "Menu.Named"), ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)] };
			if (lead is not null) { named.Add(lead); }
			named.Add(titles, 1);
			head.Add(named);
		}
		Button cancel = new() { Style = App.Res<Style>("Sht.Action"), Text = i18n.Translate("Cancel") };
		cancel.Clicked += (_, _) => _close(null);
		head.Add(cancel, 1);
		Fill(_snapshot);
		Content = new ScrollView { Content = new VerticalStackLayout { Children = { head, _entries } } };
	}

	// Opens the menu; the result is the tapped row, null on Cancel or the dimmed page. opened receives the sheet, for a
	// caller that fills it later (Show). lead = a mark or tile before the title; subtitle = a line under it.
	public static Task<Row?> PopAsync(string title, IReadOnlyList<Entry> entries, Action<MenuSheet>? opened = null, View? lead = null, string? subtitle = null, Func<ContentPage, Layout, bool>? CanInsert = null)
		=> PopupsHandle.PopBottomSheetAsync<Row?>(close =>
		{
			MenuSheet sheet = new(title, entries, close, lead, subtitle);
			opened?.Invoke(sheet);
			return sheet;
		}, CanInsert: CanInsert);

	// Update the open sheet's entries and refit its height.
	public void Show(IReadOnlyList<Entry> entries)
	{
		Entry[] snapshot = entries.ToArray();
		if (snapshot.SequenceEqual(_snapshot)) { return; }
		_snapshot = snapshot;
		Fill(snapshot);
		Element? host = Parent;
		while (host is not null and not BottomSheet) { host = host.Parent; }
		(host as BottomSheet)?.Refit();
	}

	private void Fill(IReadOnlyList<Entry> entries) => _rows.Show(entries);

	private static (string Kind, string Key) Identity(Entry entry) => entry switch
	{
		Row row => ("row", row.Agent is not null ? $"agent:{row.Agent}" : $"action:{row.Text}\0{row.Glyph}"),
		Section => ("section", ""),
		Note => ("note", ""),
		_ => ("separator", ""),
	};

	private sealed class EntryView(Entry entry) : Grid
	{
		public Entry Entry { get; private set; } = entry;
		public bool Live = true;
		public Action<Entry, Entry>? Update;

		public void Rebind(Entry entry)
		{
			Entry previous = Entry;
			Entry = entry;
			Update?.Invoke(previous, entry);
		}
	}

	private View BuildEntry(Entry entry)
	{
		EntryView grid = new(entry);
		switch (entry)
		{
			case Section section:
				Label heading = new() { Style = App.Res<Style>("Menu.Section"), Text = section.Text };
				grid.Add(heading);
				grid.Update = (_, next) => heading.Text = ((Section)next).Text;
				return grid;
			case Note note:
				grid.Style = App.Res<Style>("Lst.Row");
				Label hint = new() { Style = App.Res<Style>("Lst.Note"), Text = note.Text };
				grid.Add(hint, 1);
				grid.Update = (_, next) => hint.Text = ((Note)next).Text;
				return grid;
			case Row row:
				grid.Style = App.Res<Style>("Lst.Row");
				ContentView mark = new() { InputTransparent = true };
				AutomationProperties.SetExcludedWithChildren(mark, true);
				View? Mark(Row value)
				{
					if (value.Agent is not null) { return PaneStrip.AgentMark(value.Agent, true); }
					if (value.Glyph is null) { return null; }
					Label glyph = new() { Style = App.Res<Style>("Lst.Glyph"), Text = value.Glyph };
					if (value.Need) { glyph.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark")); }
					AutomationProperties.SetIsInAccessibleTree(glyph, false);
					return glyph;
				}
				mark.Content = Mark(row);
				grid.Add(mark);
				Label text = new() { Style = App.Res<Style>("Lst.Title"), Text = row.Text, VerticalOptions = LayoutOptions.Center };
				if (row.Need) { text.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark")); }
				grid.Add(text, 1);
				SemanticProperties.SetDescription(grid, row.Text);
				TapGestureRecognizer tap = new();
				tap.Tapped += (_, _) => { if (grid.Live && grid.Entry is Row current) { _close(current); } };
				grid.GestureRecognizers.Add(tap);
				grid.Update = (before, after) =>
				{
					Row old = (Row)before, next = (Row)after;
					if (old.Text != next.Text) { text.Text = next.Text; SemanticProperties.SetDescription(grid, next.Text); }
					if (old.Agent != next.Agent || (old.Glyph is null) != (next.Glyph is null)) { mark.Content = Mark(next); }
					else if (next.Agent is null && mark.Content is Label glyph)
					{
						if (old.Glyph != next.Glyph) { glyph.Text = next.Glyph; }
						if (old.Need != next.Need)
						{
							if (next.Need) { glyph.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark")); }
							else { glyph.RemoveBinding(Label.TextColorProperty); glyph.ClearValue(Label.TextColorProperty); }
						}
					}
					if (old.Need != next.Need)
					{
						if (next.Need) { text.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark")); }
						else { text.RemoveBinding(Label.TextColorProperty); text.ClearValue(Label.TextColorProperty); }
					}
				};
				return grid;
			default:
				grid.Add(new BoxView { Style = App.Res<Style>("Menu.Gap") });
				return grid;
		}
	}
}
