using System.Globalization;
using HerdrRemote.Remote;
using Languages;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace HerdrRemote.Pages;

// Sidebar matching herdr-app: spaces (placed, pending, and ungrouped terminals), expandable tabs, then agents
// sorted by priority or space. Hosts place two sections: Spaces contains its heading, "+", expanded rows,
// and the Agents heading with sort buttons; Agents contains group headings and agent rows.
// The phone scrolls both sections together; the wide sidebar fixes Spaces and scrolls only Agents.
internal sealed class AgentList
{
	private enum RowKind { Section, Group, Space, Tab, Pane, Actions, Agent, Note }

	// Immutable row value; unchanged values do not redraw. The view depends only on this value, Compact, and language.
	// Key identifies a section ("spaces", "agents"), space, or terminal address; Title is the displayed text.
	// Status is AgentTerminal.Word or a group's color key (empty means dim).
	// Detail holds agent detail, group count, 1-based pane index, tab number, agent sort, or "more" for a space menu.
	// Agent selects AgentTerminal.Mark or Arrange.TabMark; null means a tab has no program mark. Time is status duration.
	// Color is the space's document position, or -1 for ungrouped Mac terminals. Selected applies the Sel background.
	// Space and Tab identify the tab's long-press menu. Extra holds its address or map tree key and live terminals,
	// so layout changes redraw it; "menu" enables a space's long-press menu. Open exposes a space's tabs.
	private sealed record Row(RowKind Kind, string Key, string Title, string Status = "", string Detail = "", string? Agent = null, string Time = "", int Color = -1, bool Selected = false,
		string Space = "", string Tab = "", string Extra = "", bool Open = false,
		bool Split = false, int Depth = 0, string? Parent = null, bool Shortcut = false);

	// herdr-app's space avatar colors (its spaces mod, MARK in palette order: ansi6, ansi3, ansi4, ansi5, ansi2, ansi1), the same on light
	// and dark. herdr-app gives each new space the least used one and keeps that choice on the Mac only, so the phone takes them in the
	// document's order: the same colors as long as no space was closed in between.
	private static readonly Color[] SpaceColors = [Color.FromArgb("#22A99A"), Color.FromArgb("#E3814F"), Color.FromArgb("#5B8DEF"), Color.FromArgb("#8B7CF6"), Color.FromArgb("#3FAE6A"), Color.FromArgb("#D9649A")];

	// Cached visible rows and expanded spaces. Scroll events do not change rows; null forces the next Show to redraw.
	private List<Row>? _rows;
	private readonly HashSet<string> _expanded = [];

	// Changing language rebuilds all row labels.
	private string? _language;

	// Spinners belong to row views. Stop them on status change, row removal, or Stop.
	private readonly Dictionary<View, Spinner> _spinners = [];

	// Show restarts retained spinners after Stop.
	private bool _stopped;

	// Hosts place these sections independently; KeyedRows retains their row identities.
	public Grid Spaces { get; } = new() { RowSpacing = 0, ColumnSpacing = 0 };
	public Grid Agents { get; } = new() { RowSpacing = 0, ColumnSpacing = 0 };
	private readonly KeyedRows<Row> _spaceRows;
	private readonly KeyedRows<Row> _agentRows;
	private int _indexDigits = 1;

	public AgentList()
	{
		_spaceRows = new KeyedRows<Row>(Spaces, Identity, BuildRow, (view, row) => ((RowView)view).Rebind(row), Detached);
		_agentRows = new KeyedRows<Row>(Agents, Identity, BuildRow, (view, row) => ((RowView)view).Rebind(row), Detached);
	}

	private static (string Kind, string Key) Identity(Row row) => (row.Shortcut ? "AgentPin" : row.Kind.ToString(), row.Key);

	private void Detached(View view)
	{
		((RowView)view).Live = false;
		HoldTap.Cancel((Grid)view);
		if (_spinners.Remove(view, out Spinner? spinner)) { spinner.Stop(); }
	}

	// Use shorter rows in the wide sidebar.
	public bool Compact { get; init; }

	// The address of the pane open beside the list; its agent row is drawn with the Sel background.
	public string? Selected { get; set; }

	// A pane, tab or agent row was tapped: the terminal's address.
	public event Action<string>? Opened;

	// The Agents header's search key: the Search all panes page.
	public event Action? Searched;

	// A "+", a "⋯" or a hold asked for a sheet or a menu (Arrange does it for both hosts).
	public event Action<ArrangeAsk>? Asked;

	// Stop off-screen animations but retain views during navigation. The next Show reconciles rows and times
	// and restarts retained working spinners.
	public void Stop()
	{
		foreach (Spinner spinner in _spinners.Values) { spinner.Stop(); }
		foreach (Grid row in Spaces.Children.Concat(Agents.Children).OfType<Grid>()) { HoldTap.Cancel(row); }
		_stopped = true;
		// Use null, not an empty list: Disconnect can empty the model off screen, and the next Show must clear old rows.
		_rows = null;
	}

	// The attention order of the shown statuses (herdr-app's agents mod, RANK): blocked (and error) > done > working > idle.
	private static int Priority(string? status) => status switch
	{
		"blocked" => 4,
		"done" => 3,
		"working" => 2,
		"idle" => 1,
		_ => 0,
	};

	private sealed record Branch(AgentTerminal Root, int Rank, List<(AgentTerminal Agent, AgentTerminal? Parent, int Depth)> Members, bool Shortcut = false);

	private static List<Branch> Branches(IEnumerable<AgentTerminal> terminals)
	{
		List<AgentTerminal> reporting = [];
		Dictionary<string, List<AgentTerminal>> children = [];
		foreach (AgentTerminal terminal in terminals)
		{
			if (terminal.Word is not null) { reporting.Add(terminal); }
			if (terminal.Parent is not { } parent || parent == terminal.Address) { continue; }
			if (!children.TryGetValue(parent, out List<AgentTerminal>? siblings)) { children[parent] = siblings = []; }
			siblings.Add(terminal);
		}

		// Reachability from reporting terminals, including through live non-reporting intermediates.
		HashSet<string> nested = [];
		Stack<AgentTerminal> pending = new(reporting);
		while (pending.TryPop(out AgentTerminal? terminal))
		{
			if (!children.TryGetValue(terminal.Address, out List<AgentTerminal>? kids)) { continue; }
			foreach (AgentTerminal child in kids) { if (nested.Add(child.Address)) { pending.Push(child); } }
		}

		List<Branch> branches = [];
		HashSet<string> visited = [];
		// Start with real roots; the first reporting terminal left in each cycle becomes its root.
		foreach (AgentTerminal root in reporting.Where(agent => !nested.Contains(agent.Address)).Concat(reporting))
		{
			if (visited.Contains(root.Address)) { continue; }
			List<(AgentTerminal Agent, AgentTerminal? Parent, int Depth)> members = [];
			Stack<(AgentTerminal Agent, AgentTerminal? Parent, int Depth)> walk = new();
			walk.Push((root, null, 0));
			int rank = Priority(root.Word);
			while (walk.TryPop(out var member))
			{
				(AgentTerminal agent, AgentTerminal? parent, int depth) = member;
				if (!visited.Add(agent.Address)) { continue; }
				members.Add(member);
				if (parent is not null)
				{
					rank = Math.Max(rank, Priority(agent.Word == "done" ? "idle" : agent.Word));
					if (agent.Pinned && agent.Word is not null) { branches.Add(new(agent, Priority(agent.Word), [(agent, null, 0)], Shortcut: true)); }
				}
				if (!children.TryGetValue(agent.Address, out List<AgentTerminal>? kids)) { continue; }
				for (int i = kids.Count - 1; i >= 0; i--) { walk.Push((kids[i], agent, depth + 1)); }
			}
			branches.Add(new(root, rank, members));
		}
		return branches;
	}

	// Space avatar with its color and initial, also used in the space menu heading.
	internal static Border Tile(string name, int order) => new()
	{
		Style = App.Res<Style>("Tile"),
		BackgroundColor = SpaceColors[Math.Max(0, order) % SpaceColors.Length],
		Content = new Label { Style = App.Res<Style>("Tile.Initial"), Text = name.Length == 0 ? "?" : StringInfo.GetNextTextElement(name).ToUpperInvariant() },
	};

	// The list as herdr-app's sidebar lays it out (its spaces and agents mods). Spaces: one row per space (its tile and name), a tap
	// opens or closes its tabs, with one row per live pane, followed by "+" and the space menu. Agents: reporting terminals and their
	// live descendants, pinned roots or shortcuts first, then branches by urgency or grouped by their root's space.
	// Rows equal to the ones shown redraw nothing.
	public void Show()
	{
		List<AgentSpace>? spaces = App.Herdr.State == SessionState.Connected ? App.Herdr.Agents.Arranged : null;
		List<Row> rows = [];
		int digits = 1;
		string Index(int value)
		{
			string text = value.ToString(CultureInfo.CurrentCulture);
			digits = Math.Max(digits, text.Length);
			return text;
		}
		if (spaces is not null)
		{
			rows.Add(new Row(RowKind.Section, "spaces", i18n.Translate("Spaces")));
			if (spaces.Count == 0) { rows.Add(new Row(RowKind.Note, "", i18n.Translate("No spaces in herdr."))); }
			foreach (AgentSpace space in spaces)
			{
				bool open = _expanded.Contains(space.Id);
				rows.Add(new Row(RowKind.Space, space.Id, space.Name, Color: space.Order, Extra: Arrange.HasSpaceMenu(space) ? "menu" : "", Open: open));
				if (!open) { continue; }
				if (space.Tabs.Count == 0)
				{
					rows.AddRange(space.Terminals.Select((terminal, index) => new Row(RowKind.Pane, terminal.Address, terminal.Name, Detail: Index(index + 1))));
				}
				foreach (AgentTab tab in space.Tabs)
				{
					bool splitTab = tab.Terminals.Count > 1;
					if (tab.Root is null && !splitTab)
					{
						// A pending tab: its terminals as pane rows, numbered by their place in the space.
						rows.AddRange(tab.Terminals.Select(terminal => new Row(RowKind.Pane, terminal.Address, terminal.Name,
							Detail: Index(space.Terminals.FindIndex(t => t.Address == terminal.Address) + 1))));
						continue;
					}
					string? map = tab.Root is { } root && (splitTab || LayoutMap.Pieces(root) > 1)
						? $"map\n{tab.Key}\n{string.Join(",", tab.Terminals.Select(t => t.Address))}" : null;
					string index = Index(tab.Number);
					foreach (AgentTerminal terminal in tab.Terminals)
					{
						rows.Add(new Row(RowKind.Tab, terminal.Address, splitTab ? terminal.Name : Arrange.TabName(tab), Detail: index,
							Agent: splitTab ? terminal.Mark : Arrange.TabMark(tab), Space: space.Id, Tab: tab.Id, Extra: map ?? terminal.Address, Split: splitTab));
						index = "";
					}
				}
				if (space.Id.Length > 0) { rows.Add(new Row(RowKind.Actions, space.Id, space.Name, Detail: Arrange.HasSpaceMenu(space) ? "more" : "")); }
			}

			bool byPriority = RemoteSettings.AgentSort == "priority";
			rows.Add(new Row(RowKind.Section, "agents", i18n.Translate("Agents"), Detail: RemoteSettings.AgentSort));
			List<Branch> branches = Branches(spaces.SelectMany(space => space.Terminals));
			if (branches.Count == 0) { rows.Add(new Row(RowKind.Note, "", i18n.Translate("No agent status yet. Agents show up here when they report their status. Older versions and agents inside tmux may not."))); }
			Dictionary<string, AgentSpace> spaceById = spaces.ToDictionary(space => space.Id);
			Dictionary<(string Space, string Tab), AgentTab> tabById = spaces
				.SelectMany(space => space.Tabs.Select(tab => (space.Id, Tab: tab))).ToDictionary(pair => (pair.Id, pair.Tab.Id), pair => pair.Tab);
			IEnumerable<Branch> ordered = byPriority
				? branches.OrderByDescending(branch => branch.Rank).ThenByDescending(branch => branch.Root.ChangedMs).ThenByDescending(branch => branch.Root.Seq)
				: branches.OrderBy(branch => spaceById[branch.Root.SpaceId].Order < 0 ? int.MaxValue : spaceById[branch.Root.SpaceId].Order);
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			foreach (var group in ordered.OrderByDescending(branch => branch.Root.Pinned).GroupBy(branch => branch.Root.Pinned ? ""
				: byPriority ? branch.Rank switch { 4 => "blocked", 3 => "done", 2 => "working", _ => "idle" } : $"space:{branch.Root.SpaceId}"))
			{
				string title = group.Key.Length == 0 ? i18n.Translate("Pinned")
					: !byPriority ? spaceById[group.First().Root.SpaceId].Name
					: i18n.Translate(group.Key switch { "blocked" => "Needs you", "done" => "Done", "working" => "Working", _ => "Idle" });
				// Priority group headings use their status color and count, matching herdr-app; idle, pinned, and space headings stay dim.
				string color = group.Key is "blocked" or "done" or "working" ? PaneStrip.StatusColorKey(group.Key) : "";
				int header = rows.Count, blocked = 0;
				rows.Add(new Row(RowKind.Group, group.Key, title, color, byPriority && group.Key != "blocked" ? group.Count().ToString(CultureInfo.CurrentCulture) : ""));
				foreach (Branch branch in group)
				foreach ((AgentTerminal agent, AgentTerminal? parent, int depth) in branch.Members)
				{
					// Priority and pinned rows include the space and optional detail. Grouped rows use Title because the heading names the space.
					bool named = (byPriority || agent.Pinned) && agent.Space.Length > 0;
					// herdr-app's duration: under a minute "<1m", then minutes, hours, days (status_ms is the Mac's clock, close to the phone's).
					long minutes = (now - agent.ChangedMs) / 60000;
					string time = agent.ChangedMs == 0 ? ""
						: minutes < 1 ? i18n.Translate("<1m")
						: minutes < 60 ? string.Format(CultureInfo.CurrentCulture, i18n.Translate("{0}m"), minutes)
						: minutes < 60 * 24 ? string.Format(CultureInfo.CurrentCulture, i18n.Translate("{0}h"), minutes / 60)
						: string.Format(CultureInfo.CurrentCulture, i18n.Translate("{0}d"), minutes / (60 * 24));
					string titleText = named ? agent.Space : agent.Title;
					string detail = named ? agent.Detail ?? "" : "";
					if (parent is not null)
					{
						titleText = agent.Name;
						detail = "";
						if (agent.SpaceId != branch.Root.SpaceId || agent.TabId != branch.Root.TabId)
						{
							detail = spaceById[agent.SpaceId].Name;
							if (agent.TabId is { } tabId && tabById.TryGetValue((agent.SpaceId, tabId), out AgentTab? tab))
							{
								detail += $" / {(tab.Name.Length > 0 ? tab.Name : tab.Number.ToString(CultureInfo.CurrentCulture))}";
							}
						}
					}
					rows.Add(new Row(RowKind.Agent, agent.Address, titleText, agent.Word ?? "", detail, agent.Mark, time,
						Selected: agent.Address == Selected, Depth: Math.Min(depth, 3), Parent: parent?.Name, Shortcut: branch.Shortcut));
					if (agent.Word == "blocked") { blocked++; }
				}
				if (group.Key == "blocked") { rows[header] = rows[header] with { Detail = blocked.ToString(CultureInfo.CurrentCulture) }; }
			}
		}
		if (_rows is not null && rows.SequenceEqual(_rows) && _language == i18n.defaultLanguageCode) { return; }
		bool repad = digits != _indexDigits;
		_indexDigits = digits;
		_rows = rows;
		// A language change invalidates every row's labels.
		if (_language != i18n.defaultLanguageCode)
		{
			_language = i18n.defaultLanguageCode;
			_spaceRows.Clear();
			_agentRows.Clear();
		}
		// Everything up to the Agents label goes in Spaces, the rest in Agents; new identities build; changed identities update in place.
		int split = rows.FindIndex(row => row is { Kind: RowKind.Section, Key: "agents" }) + 1;
		if (split == 0) { split = rows.Count; }
		_spaceRows.Show(rows.GetRange(0, split));
		_agentRows.Show(rows.GetRange(split, rows.Count - split));
		if (repad)
		{
			// GeistMono has no figure space; its regular space has the same advance as every digit.
			foreach (RowView row in Spaces.Children.OfType<RowView>())
			{
				if (row.Row.Kind is RowKind.Pane or RowKind.Tab) { ((Label)row.Children[0]).Text = row.Row.Detail.PadLeft(_indexDigits); }
			}
		}
		// Restart retained working spinners after Stop; status changes and removed rows already discard their spinners.
		if (_stopped)
		{
			_stopped = false;
			foreach (Spinner spinner in _spinners.Values) { spinner.Start(); }
		}
	}

	// A 32x32 key of the list (the Spaces "+", a space's "+" and "⋯"): its glyph, spoken name and tap.
	private static Border Key(string style, string glyphStyle, string glyph, string spoken, Action tapped)
	{
		Label icon = new() { Style = App.Res<Style>(glyphStyle), Text = glyph };
		AutomationProperties.SetIsInAccessibleTree(icon, false);
		Border key = new() { Style = App.Res<Style>(style), Content = icon };
		SemanticProperties.SetDescription(key, spoken);
		TapGestureRecognizer tap = new();
		tap.Tapped += (_, _) => tapped();
		key.GestureRecognizers.Add(tap);
		return key;
	}

	// One row of the list. A space row opens or closes its tabs; pane, tab and agent rows raise Opened; the sort buttons set the
	// agents' sort, priority or grouped by space (RemoteSettings keeps the choice across restarts); the keys and holds raise Asked.
	private sealed class RowView(Row row) : Grid
	{
		public Row Row { get; private set; } = row;
		public bool Live = true;
		public Action<Row, Row>? Update;

		public void Rebind(Row row)
		{
			Row previous = Row;
			Row = row;
			Update?.Invoke(previous, row);
		}
	}

	private View BuildRow(Row row)
	{
		RowView view = new(row);
		switch (row.Kind)
		{
			case RowKind.Note:
				Label note = new() { Style = App.Res<Style>("Sec.Note"), Text = row.Title };
				view.Add(note);
				view.Update = (old, next) => { if (old.Title != next.Title) { note.Text = next.Title; } };
				return view;
			case RowKind.Section:
				if (row.Key != "agents")
				{
					// Spaces heading with herdr-app's new-space "+" at the right.
					RowView head = view;
					head.Style = App.Res<Style>("Sec");
					Label title = new() { Style = App.Res<Style>("Sec.Title"), Text = row.Title };
					head.Add(title);
					head.Update = (old, next) => { if (old.Title != next.Title) { title.Text = next.Title; } };
					head.Add(Key("Sec.Key", "Sec.Plus", "\uE902", i18n.Translate("New space"), () => { if (view.Live) { Asked?.Invoke(new ArrangeAsk(AskKind.NewSpace)); } }), 1);
					return head;
				}
				// Agents heading below its divider, with herdr-app's Lucide layers and clock sort icons.
				Grid agentsHead = new() { Style = App.Res<Style>("Sec") };
				Label agentsTitle = new() { Style = App.Res<Style>("Sec.Title"), Text = row.Title };
				agentsHead.Add(agentsTitle);
				List<(Grid View, string Sort, string Label)> sortButtons = [];
				HorizontalStackLayout sorts = new() { Style = App.Res<Style>("Row.Gap4") };
				// Search all panes (lucide search), beside the two sorts.
				Grid searchButton = new() { Style = App.Res<Style>("Sec.Button") };
				searchButton.Add(new ShapePath { Style = App.Res<Style>("Sec.Path"), Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M19,11 A8,8 0 1 1 3,11 A8,8 0 1 1 19,11 M21,21 L16.65,16.65")! });
				SemanticProperties.SetDescription(searchButton, i18n.Translate("Search all panes"));
				TapGestureRecognizer searchTap = new();
				searchTap.Tapped += (_, _) => { if (view.Live) { Searched?.Invoke(); } };
				searchButton.GestureRecognizers.Add(searchTap);
				sorts.Add(searchButton);
				foreach ((string sort, string label, string path) in new[]
				{
					("grouped", "Grouped by space", "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12 M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17"),
					("priority", "Priority", "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,6 L12,12 L16,14"),
				})
				{
					// Each button is spoken as its sort with its selected state; a tap sets that sort.
					Grid button = new() { Style = App.Res<Style>("Sec.Button") };
					button.Add(new ShapePath { Style = App.Res<Style>("Sec.Path"), Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(path)! });
					SemanticProperties.SetDescription(button, ChipRow.Spoken(string.Format(CultureInfo.CurrentCulture, i18n.Translate("Sort agents: {0}"), i18n.Translate(label)), sort == row.Detail));
					TapGestureRecognizer sortTap = new();
					sortTap.Tapped += (_, _) =>
					{
						if (!view.Live) { return; }
						RemoteSettings.AgentSort = sort;
						Show();
					};
					button.GestureRecognizers.Add(sortTap);
					sorts.Add(button);
					sortButtons.Add((button, sort, label));
				}
				agentsHead.Add(sorts, 1);
				RowView agentsBlock = view;
				agentsBlock.Style = App.Res<Style>("Sec.Divided");
				agentsBlock.Update = (old, next) =>
				{
					if (old.Title != next.Title) { agentsTitle.Text = next.Title; }
					if (old.Detail != next.Detail)
					{
						foreach (var sort in sortButtons) { SemanticProperties.SetDescription(sort.View, ChipRow.Spoken(string.Format(CultureInfo.CurrentCulture, i18n.Translate("Sort agents: {0}"), i18n.Translate(sort.Label)), sort.Sort == next.Detail)); }
					}
				};
				agentsBlock.Add(new BoxView { Style = App.Res<Style>("Lst.Divider") });
				agentsBlock.Add(agentsHead, 0, 1);
				return agentsBlock;
			case RowKind.Group:
				HorizontalStackLayout groupHead = new() { Style = App.Res<Style>("Grp") };
				Label groupTitle = new() { Style = App.Res<Style>("Grp.Title"), Text = row.Title };
				if (row.Status.Length > 0) { groupTitle.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>(row.Status), App.Res<Color>(row.Status + "Dark")); }
				groupHead.Add(groupTitle);
				Label count = new() { Style = App.Res<Style>("Grp.Count"), Text = row.Detail, IsVisible = row.Detail.Length > 0 };
				groupHead.Add(count);
				view.Add(groupHead);
				view.Update = (old, next) =>
				{
					if (old.Title != next.Title) { groupTitle.Text = next.Title; }
					if (old.Detail != next.Detail) { count.Text = next.Detail; count.IsVisible = next.Detail.Length > 0; }
					if (old.Status != next.Status)
					{
						string color = next.Status.Length > 0 ? next.Status : "Ink3";
						groupTitle.SetAppThemeColor(Label.TextColorProperty, App.Res<Color>(color), App.Res<Color>(color + "Dark"));
					}
				};
				return view;
			case RowKind.Actions:
				// An expanded space ends with "+" for its new menu and "⋯" when a space menu is available.
				RowView actions = view;
				actions.Style = App.Res<Style>("Row.Actions");
				actions.Add(Key("Row.ActKey", "Row.ActPlus", "\uE902", i18n.Translate("New"), () => { if (view.Live) { Asked?.Invoke(new ArrangeAsk(AskKind.New, view.Row.Key)); } }));
				Border more = Key("Row.ActKey", "Row.ActMore", "\uE905", i18n.Translate("More"), () => { if (view.Live && view.Row.Detail == "more") { Asked?.Invoke(new ArrangeAsk(AskKind.Space, view.Row.Key)); } });
				more.IsVisible = row.Detail == "more";
				actions.Add(more, 2);
				view.Update = (old, next) => { if (old.Detail != next.Detail) { more.IsVisible = next.Detail == "more"; } };
				return actions;
		}

		RowView grid = view;
		grid.Style = App.Res<Style>(row.Kind switch { RowKind.Pane => "Row.Pane", RowKind.Tab => "Row.Tab", RowKind.Space => Compact ? "Row.SpaceCompact" : "Row.Space", _ => Compact ? "Row.AgentCompact" : "Row.Agent" });
		if (row.Kind == RowKind.Space)
		{
			// The Mac's own terminals are in no herdr-app space: an outlined tile with the Mac glyph and the connected dot at its corner
			// (the list shows only while Connected). The dot sits outside the tile's clip, so both share a cell.
			View SpaceMark(Row value) => value.Color < 0
				? new Grid
				{
					VerticalOptions = LayoutOptions.Center,
					Children =
					{
						new Border { Style = App.Res<Style>("Tile.Mac"), Content = new Label { Style = App.Res<Style>("Tile.MacIcon"), Text = "\xE930" } },
						new Ellipse { Style = App.Res<Style>("Tile.MacDot") },
					},
				}
				: Tile(value.Title, value.Color);
			ContentView tile = new() { Content = SpaceMark(row), InputTransparent = true };
			Label name = new() { Style = App.Res<Style>("Row.Name"), Text = row.Title };
			Label chevron = new() { Style = App.Res<Style>("Row.Chev"), Text = row.Open ? "\xE909" : "\xE907" };
			grid.Add(tile);
			grid.Add(name, 1);
			grid.Add(chevron, 2);
			void Toggle()
			{
				if (!view.Live) { return; }
				if (!_expanded.Add(view.Row.Key)) { _expanded.Remove(view.Row.Key); }
				Show();
			}
			Action held = () => { if (view.Live) { Asked?.Invoke(new ArrangeAsk(AskKind.Space, view.Row.Key)); } };
			HoldTap.HoldButton button = (HoldTap.HoldButton)HoldTap.Add(grid, row.Title, Toggle, held);
			button.Held = row.Extra == "menu" ? held : null;
			view.Update = (old, next) =>
			{
				if ((old.Color < 0) != (next.Color < 0)) { tile.Content = SpaceMark(next); }
				else if (tile.Content is Border border)
				{
					if (old.Color != next.Color) { border.BackgroundColor = SpaceColors[Math.Max(0, next.Color) % SpaceColors.Length]; }
					if (old.Title != next.Title && border.Content is Label initial) { initial.Text = next.Title.Length == 0 ? "?" : StringInfo.GetNextTextElement(next.Title).ToUpperInvariant(); }
				}
				if (old.Title != next.Title) { name.Text = next.Title; SemanticProperties.SetDescription(button, next.Title); }
				if (old.Open != next.Open) { chevron.Text = next.Open ? "\xE909" : "\xE907"; }
				if (old.Extra != next.Extra) { button.Held = next.Extra == "menu" ? held : null; }
			};
			return grid;
		}
		if (row.Kind == RowKind.Pane)
		{
			// Pane index, name, and address; status marks appear in Agents instead.
			Label index = new() { Style = App.Res<Style>("Row.PaneIndex"), Text = row.Detail.PadLeft(_indexDigits), HorizontalTextAlignment = TextAlignment.End };
			Label name = new() { Style = App.Res<Style>("Row.PaneName"), Text = row.Title };
			grid.Add(index);
			grid.Add(name, 1);
			grid.Add(new Label { Style = App.Res<Style>("Row.PaneAddr"), Text = row.Key }, 2);
			TapGestureRecognizer tap = new();
			tap.Tapped += (_, _) => { if (view.Live) { Opened?.Invoke(view.Row.Key); } };
			grid.GestureRecognizers.Add(tap);
			view.Update = (old, next) =>
			{
				if (old.Detail != next.Detail) { index.Text = next.Detail.PadLeft(_indexDigits); }
				if (old.Title != next.Title) { name.Text = next.Title; }
			};
			return grid;
		}
		if (row.Kind == RowKind.Tab)
		{
			// Keep the tab's input target while its address, program mark or miniature layout changes.
			Label index = new() { Style = App.Res<Style>("Row.PaneIndex"), Text = row.Detail.PadLeft(_indexDigits), HorizontalTextAlignment = TextAlignment.End };
			ContentView mark = new() { Content = Arrange.Mark(row.Agent), InputTransparent = true };
			Label name = new() { Style = App.Res<Style>("Row.TabName"), Text = row.Title };
			View Map(Row value)
			{
				if (value.Extra.StartsWith("map\n", StringComparison.Ordinal)
					&& App.Herdr.Agents.Arranged.Find(space => space.Id == value.Space)?.Tabs.Find(t => t.Id == value.Tab) is { Root: { } root } tab)
				{
					HashSet<string> live = [.. tab.Terminals.Select(t => t.Address)];
					if (!value.Split) { return new LayoutMap(root, live, null); }
					List<LayoutCell> cells = HerdrLayout.MapForPane(root, value.Key);
					if (cells.Exists(cell => cell.Terminal == value.Key)) { return new LayoutMap(cells, live, value.Key); }
				}
				return new Label { Style = App.Res<Style>("Row.PaneAddr"), Text = value.Key };
			}
			static string SpokenTab(Row value) => value.Detail.Length > 0 ? $"{value.Detail} {value.Title}" : value.Title;
			ContentView map = new() { Content = Map(row), InputTransparent = true };
			grid.Add(index);
			grid.Add(mark, 1);
			grid.Add(name, 2);
			grid.Add(map, 3);
			Button button = HoldTap.Add(grid, SpokenTab(row), () => { if (view.Live) { Opened?.Invoke(view.Row.Key); } }, () => { if (view.Live) { Asked?.Invoke(new ArrangeAsk(AskKind.Tab, view.Row.Space, view.Row.Tab)); } });
			view.Update = (old, next) =>
			{
				if (old.Detail != next.Detail) { index.Text = next.Detail.PadLeft(_indexDigits); }
				if (old.Title != next.Title) { name.Text = next.Title; }
				if (old.Agent != next.Agent) { mark.Content = Arrange.Mark(next.Agent); }
				if (old.Extra != next.Extra || old.Split != next.Split || old.Space != next.Space || old.Tab != next.Tab) { map.Content = Map(next); }
				if (old.Detail != next.Detail || old.Title != next.Title) { SemanticProperties.SetDescription(button, SpokenTab(next)); }
			};
			return grid;
		}

		if (row.Selected) { grid.SetAppThemeColor(VisualElement.BackgroundColorProperty, App.Res<Color>("Sel"), App.Res<Color>("SelDark")); }
		ContentView agentMark = new() { Content = PaneStrip.AgentMark(row.Agent ?? "", Compact), InputTransparent = true };
		grid.Add(agentMark);
		// First line: space (or Title below a space heading), then " · " and detail.
		// Second line: terminal address and time in its current status.
		Span branchCue = new() { Style = App.Res<Style>("Agent.SepSpan") };
		Span titleSpan = new() { Text = row.Title };
		Span separator = new() { Style = App.Res<Style>("Agent.SepSpan"), Text = row.Detail.Length > 0 ? " · " : "" };
		Span detailSpan = new() { Style = App.Res<Style>("Agent.TitleSpan"), Text = row.Detail };
		Label line1 = new() { Style = App.Res<Style>("Agent.L1"), FormattedText = new FormattedString { Spans = { branchCue, titleSpan, separator, detailSpan } } };
		Span addressSpan = new() { Style = App.Res<Style>("Agent.AddressSpan"), Text = row.Key };
		Span timeSpan = new() { Text = row.Time.Length > 0 ? $" · {row.Time}" : "" };
		Label line2 = new() { Style = App.Res<Style>("Agent.L2"), FormattedText = new FormattedString { Spans = { addressSpan, timeSpan } } };
		grid.Add(new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { line1, line2 } }, 1);
		void ShowDepth(int depth)
		{
			int inset = depth * 10;
			grid.ColumnDefinitions = new(new ColumnDefinition(32 + inset), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto));
			agentMark.Margin = new Thickness(inset, 0, 0, 0);
			branchCue.Text = depth > 0 ? "↳ " : "";
		}
		if (row.Depth > 0) { ShowDepth(row.Depth); }

		// Status marks match herdr-app: "Needs you" chip for blocked, spinning ring for working,
		// solid dot for done, and hollow ring for idle.
		ContentView status = new() { InputTransparent = true };
		grid.Add(status, 2);
		void ShowStatus(Row value)
		{
			if (_spinners.Remove(grid, out Spinner? oldSpinner)) { oldSpinner.Stop(); }
			switch (value.Status)
			{
				case "":
					status.Content = null;
					break;
				case "blocked":
					status.Content = new Border { Style = App.Res<Style>("Chip.Need"), Content = new Label { Style = App.Res<Style>("Chip.NeedText"), Text = i18n.Translate("Needs you") } };
					break;
				case "working":
					Grid spin = new() { Style = App.Res<Style>("Agent.Spin") };
					spin.Add(new Ellipse { Style = App.Res<Style>("Agent.SpinTrack") });
					spin.Add(new ShapePath { Style = App.Res<Style>("Agent.SpinArc"), Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M8,2 A6,6 0 0 1 14,8")! });
					// herdr-app's spinner turns once every 0.9 s, on the compositor (Spinner); it belongs to this row's view.
					Spinner spinner = new(spin);
					if (!_stopped) { spinner.Start(); }
					_spinners[grid] = spinner;
					status.Content = spin;
					break;
				case "done":
					string done = PaneStrip.StatusColorKey(value.Status);
					Ellipse dot = new() { Style = App.Res<Style>("Agent.Dot") };
					dot.SetAppTheme<Brush>(Shape.FillProperty, new SolidColorBrush(App.Res<Color>(done)), new SolidColorBrush(App.Res<Color>(done + "Dark")));
					status.Content = dot;
					break;
				default:
					status.Content = new Ellipse { Style = App.Res<Style>("Agent.Ring") };
					break;
			}
		}
		ShowStatus(row);
		// The row opens its pane; a hold opens its menu (Pin / Unpin).
		static string SpokenAgent(Row value)
		{
			string label = value.Detail.Length > 0 ? $"{value.Title} · {value.Detail}" : value.Title;
			return value.Parent is { } parent
				? $"{label}. {string.Format(CultureInfo.CurrentCulture, i18n.Translate("Opened by {0}"), parent)}" : label;
		}
		Button agentButton = HoldTap.Add(grid, SpokenAgent(row), () => { if (view.Live) { Opened?.Invoke(view.Row.Key); } }, () => { if (view.Live) { Asked?.Invoke(new ArrangeAsk(AskKind.Agent, Address: view.Row.Key)); } });
		view.Update = (old, next) =>
		{
			if (old.Title != next.Title) { titleSpan.Text = next.Title; }
			if (old.Detail != next.Detail) { separator.Text = next.Detail.Length > 0 ? " · " : ""; detailSpan.Text = next.Detail; }
			if (old.Time != next.Time) { timeSpan.Text = next.Time.Length > 0 ? $" · {next.Time}" : ""; }
			if (old.Agent != next.Agent) { agentMark.Content = PaneStrip.AgentMark(next.Agent ?? "", Compact); }
			if (old.Status != next.Status) { ShowStatus(next); }
			if (old.Depth != next.Depth) { ShowDepth(next.Depth); }
			if (old.Selected != next.Selected)
			{
				if (next.Selected) { grid.SetAppThemeColor(VisualElement.BackgroundColorProperty, App.Res<Color>("Sel"), App.Res<Color>("SelDark")); }
				else { grid.RemoveBinding(VisualElement.BackgroundColorProperty); grid.ClearValue(VisualElement.BackgroundColorProperty); }
			}
			if (old.Title != next.Title || old.Detail != next.Detail || old.Parent != next.Parent) { SemanticProperties.SetDescription(agentButton, SpokenAgent(next)); }
		};
		return grid;
	}
}
