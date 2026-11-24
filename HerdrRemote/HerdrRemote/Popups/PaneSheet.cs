using HerdrRemote;
using HerdrRemote.Pages;
using HerdrRemote.Remote;
using Languages;
using Arrangement = HerdrRemote.Pages.Arrange;

namespace Popups;

// Opened from the pane pill: mirror herdr-app's header with the mark, name, folder and menu, followed
// by Split right, Split down, Find and Close pane, then the space name and numbered tabs with marks and menus.
// The open tab includes a layout map whose live pieces open their panes; a tab row opens its first terminal.
// Close the sheet before the Pane screen handles its result or opens another sheet.
// Arrange.CanSplit excludes panes not yet placed in the layout, including pending tabs;
// Arrange.CanClosePane controls whether Close pane is available.
public sealed class PaneSheet : ContentView
{
	public enum Act { Open, Find, Close, More, Tab, SplitRight, SplitDown }

	// Open: Value = the pane to show; Tab: Value = the tab whose menu was asked; Find, Close, More, SplitRight, SplitDown: this pane.
	public sealed record Pick(Act Act, string Value);

	private readonly Action<Pick?> _close;

	private PaneSheet(AgentSpace space, string address, string? home, bool canClose, Action<Pick?> close)
	{
		_close = close;
		AgentTerminal pane = space.Terminals.First(t => t.Address == address);
		IReadOnlySet<string> live = App.Herdr.Agents.Terminals.Keys.ToHashSet();
		VerticalStackLayout body = new() { Children = { Block(pane, home, canClose) } };
		body.Add(new Label { Style = App.Res<Style>("PSheet.Space"), Text = space.Name });
		int digits = space.Tabs.Select(tab => tab.Number).DefaultIfEmpty(1).Max().ToString(System.Globalization.CultureInfo.CurrentCulture).Length;
		foreach (AgentTab tab in space.Tabs)
		{
			bool open = tab.Terminals.Exists(t => t.Address == address);
			body.Add(TabRow(tab, open, digits));
			if (open && tab.Root is { } root)
			{
				body.Add(new LayoutMap(root, live, address, piece => App.Herdr.Agents.Terminals.GetValueOrDefault(piece) is { } t ? Arrangement.PaneName(t) : piece,
					piece => _close(new Pick(Act.Open, piece))));
			}
		}
		Content = body;
	}

	// Opens the sheet on the pane at address of space; the answer, or null on the dimmed page or a drag down.
	internal static Task<Pick?> PopAsync(AgentSpace space, string address, string? home, bool canClose, Func<ContentPage, Layout, bool>? CanInsert = null)
		=> PopupsHandle.PopBottomSheetAsync<Pick?>(close => new PaneSheet(space, address, home, canClose, close), CanInsert: CanInsert);

	// herdr-app's pane header: mark, name, folder ("~" for home), "⋯"; under a hairline the buttons, divided by hairlines.
	private View Block(AgentTerminal pane, string? home, bool canClose)
	{
		Grid head = new() { Style = App.Res<Style>("PSheet.Head") };
		head.Add(Arrangement.Mark(pane.Mark));
		head.Add(new Label { Style = App.Res<Style>("PSheet.Name"), Text = Arrangement.PaneName(pane) }, 1);
		string? folder = App.Herdr.Agents.FolderOf(pane.Address);
		head.Add(new Label { Style = App.Res<Style>("PSheet.Folder"), Text = folder is null ? "" : home is null ? folder : ListSheet.Tilde(folder, home) }, 2);
		head.Add(MoreKey(i18n.Translate("More"), () => _close(new Pick(Act.More, pane.Address))), 3);
		List<(string Glyph, string Text, bool Need, Act Act)> acts = [];
		if (Arrangement.CanSplit(pane.Address))
		{
			acts.Add(("", i18n.Translate("Split right"), false, Act.SplitRight));
			acts.Add(("", i18n.Translate("Split down"), false, Act.SplitDown));
		}
		acts.Add(("", i18n.Translate("Find"), false, Act.Find));
		if (canClose) { acts.Add(("", i18n.Translate("Close pane"), true, Act.Close)); }
		Grid buttons = new() { Style = App.Res<Style>("PSheet.Acts") };
		for (int i = 0; i < acts.Count; i++)
		{
			buttons.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
			(string glyph, string text, bool need, Act act) = acts[i];
			VerticalStackLayout button = new() { Style = App.Res<Style>("PSheet.Act") };
			Label glyphLabel = new() { Style = App.Res<Style>("PSheet.ActGlyph"), Text = glyph };
			Label textLabel = new() { Style = App.Res<Style>("PSheet.ActText"), Text = text };
			if (need)
			{
				glyphLabel.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark"));
				textLabel.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>("Need"), App.Res<Color>("NeedDark"));
			}
			AutomationProperties.SetIsInAccessibleTree(glyphLabel, false);
			button.Add(glyphLabel);
			button.Add(textLabel);
			SemanticProperties.SetDescription(button, text);
			TapGestureRecognizer tap = new();
			tap.Tapped += (_, _) => _close(new Pick(act, pane.Address));
			button.GestureRecognizers.Add(tap);
			buttons.Add(button, i);
			if (i > 0) { buttons.Add(new BoxView { Style = App.Res<Style>("PSheet.ActLine") }, i); }
		}
		return new Border
		{
			Style = App.Res<Style>("PSheet.Block"),
			Content = new VerticalStackLayout { Children = { head, new BoxView { Style = App.Res<Style>("Lst.Divider") }, buttons } },
		};
	}

	// Use Arrange.TabName with the tab number, agent mark or terminal glyph, and menu.
	// The open tab is semibold; tapping a row opens its first terminal.
	private View TabRow(AgentTab tab, bool open, int digits)
	{
		Grid row = new() { Style = App.Res<Style>("PSheet.Tab") };
		Grid named = new() { Style = App.Res<Style>("PSheet.TabOpen") };
		named.Add(new Label { Style = App.Res<Style>("Row.PaneIndex"), Text = tab.Number.ToString(System.Globalization.CultureInfo.CurrentCulture).PadLeft(digits), HorizontalTextAlignment = TextAlignment.End });
		named.Add(Arrangement.Mark(Arrangement.TabMark(tab)), 1);
		string name = Arrangement.TabName(tab);
		named.Add(new Label { Style = App.Res<Style>(open ? "PSheet.TabNameOn" : "PSheet.TabName"), Text = name }, 2);
		SemanticProperties.SetDescription(named, $"{tab.Number} {name}");
		TapGestureRecognizer tap = new();
		tap.Tapped += (_, _) => _close(new Pick(Act.Open, tab.Terminals[0].Address));
		named.GestureRecognizers.Add(tap);
		row.Add(named);
		row.Add(MoreKey(i18n.Translate("More"), () => _close(new Pick(Act.Tab, tab.Id))), 1);
		return row;
	}

	// The "⋯" key (the list's own door glyph, E905), 40x40, spoken "More".
	private static View MoreKey(string spoken, Action tapped)
	{
		Grid key = new() { Style = App.Res<Style>("Hdr.Button") };
		Label glyph = new() { Style = App.Res<Style>("Row.ActMore"), Text = "" };
		AutomationProperties.SetIsInAccessibleTree(glyph, false);
		key.Add(glyph);
		SemanticProperties.SetDescription(key, spoken);
		TapGestureRecognizer tap = new();
		tap.Tapped += (_, _) => tapped();
		key.GestureRecognizers.Add(tap);
		return key;
	}
}
