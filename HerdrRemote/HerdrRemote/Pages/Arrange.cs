using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using HerdrRemote.Remote;
using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// What the list or the pane header asked for: New space (the Spaces "+"), the new menu of a space (a space's "+", the pane's "+"),
// the menus of a space ("⋯" or a hold), a tab and an agent (a hold), Close pane (Address = the pane; the pane sheet and menu) and
// the pane menu (Address = the pane; the pane sheet's "⋯", or the pill's tap for a terminal no space holds) and Split right / Split
// down (Address = the pane; the pane sheet's first two buttons and the pane menu).
internal enum AskKind { NewSpace, New, Space, Tab, Agent, ClosePane, Pane, SplitRight, SplitDown }

internal sealed record ArrangeAsk(AskKind Kind, string SpaceId = "", string TabId = "", string Address = "");

// Menu host: Open pushes or switches to a terminal; Pane is the current address (null on the list),
// and Size is the grid inherited by new terminals. Closing receives the full kill set before any kill,
// so the host can leave an affected pane first. Find and Read run the page's actions; Reading selects Read or Stop.
internal sealed record ArrangeHost(Func<string, Task> Open, string? Pane = null, (int Cols, int Rows)? Size = null,
	Func<IReadOnlyCollection<string>, Task>? Closing = null, Action? Find = null, Action? Read = null, Func<bool>? Reading = null,
	Func<ContentPage, Layout, bool>? CanInsert = null);

// Shared list, sidebar, and header actions: menus, sheets, terminal creation, splitting, renaming, and closing.
internal static class Arrange
{
	// One ask at a time: a second tap while a sheet or a start is under way does nothing.
	private static bool _busy;

	// The shared pieces of this connection (a new connection gets new ones); null while not connected.
	private static HerdrCreate? Create() => App.Herdr.State == SessionState.Connected ? App.Herdr.Creation : null;

	// Does what was asked; every failure ends in one dialog (the core's own words for its errors), never in a crash.
	public static async Task AskAsync(ArrangeHost host, ArrangeAsk ask)
	{
		if (_busy || Create() is not { } create) { return; }
		_busy = true;
		try
		{
			switch (ask.Kind)
			{
				case AskKind.NewSpace:
					await NewSpaceAsync(host, create);
					break;
				case AskKind.New:
					await NewMenuAsync(host, create, ask.SpaceId);
					break;
				case AskKind.Space:
					await SpaceMenuAsync(host, create, ask.SpaceId);
					break;
				case AskKind.Tab:
					await TabMenuAsync(host, create, ask.SpaceId, ask.TabId);
					break;
				case AskKind.Agent:
					await AgentMenuAsync(host, ask.Address);
					break;
				case AskKind.ClosePane:
					await ClosePaneAsync(host, create, ask.Address);
					break;
				case AskKind.Pane:
					await PaneMenuAsync(host, create, ask.Address);
					break;
				case AskKind.SplitRight:
					await SplitAsync(host, create, ask.Address, HerdrLayout.Right);
					break;
				case AskKind.SplitDown:
					await SplitAsync(host, create, ask.Address, HerdrLayout.Down);
					break;
			}
		}
		catch (CoreRpcException ex) when (ex.Kind == "spawn_failed")
		{
			// Show herdr-app's launch-failure notice with the core's error message.
			Debug.WriteLine($"[Arrange] {ask.Kind}: {ex.Message}");
			await BusyRunner.Show("Terminal failed to start", ex.CoreMessage, host.CanInsert);
		}
		catch (CoreRpcException ex)
		{
			Debug.WriteLine($"[Arrange] {ask.Kind}: {ex.Message}");
			await BusyRunner.Show("Error", ex.CoreMessage, host.CanInsert);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Arrange] {ask.Kind} unexpected: {ex}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"), host.CanInsert);
		}
		finally
		{
			_busy = false;
		}
	}

	// Whether a space has a menu: every space (Close; New tab and the note for a pending one); the Mac's own group has none.
	public static bool HasSpaceMenu(AgentSpace space) => space.Id.Length > 0;

	// Offer Close pane only when connected, inside a space, and allowed by the block's closability rules.
	// The pane sheet and menu share this check.
	public static bool CanClosePane(string address) =>
		Create() is { } create && SpaceOf(address) is { Id.Length: > 0 } space && create.PlanClose(space.Id, null, address) is not null;

	// Offer splits only when connected and the pane belongs to a placed space and tab, with its block under
	// a container in that tab's tree. Pending spaces or tabs and ungrouped terminals cannot split.
	public static bool CanSplit(string address)
	{
		if (Create() is null || SpaceOf(address) is not { Id.Length: > 0, Pending: false } space) { return false; }
		if (space.Tabs.Find(t => t.Terminals.Exists(terminal => terminal.Address == address)) is not { Root: { } root }) { return false; }
		return HerdrLayout.FindBlock(root, address) is { } block && HerdrLayout.Id(block) is { } id && HerdrLayout.Parent(root, id) is not null;
	}

	// Return a launch-failure message when a terminal started by this phone exits immediately with a nonzero code.
	// Other stream endings, or an unavailable lookup, return null.
	public static async Task<string?> LaunchFailureAsync(string address)
	{
		if (Create() is not { } create) { return null; }
		try
		{
			return await create.LaunchFailureAsync(address, CancellationToken.None);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Arrange] launch failure of {address}: {ex.Message}");
			return null;
		}
	}

	// Current space containing the terminal, or null if it is ungrouped.
	private static AgentSpace? SpaceOf(string address) => App.Herdr.Agents.Arranged.Find(space => space.Terminals.Exists(t => t.Address == address));

	// Tab label: explicit document name, then agent program name, then first terminal's program name or terminal name.
	internal static string TabName(AgentTab tab)
	{
		if (tab.Name.Length > 0) { return tab.Name; }
		AgentTerminal? agent = tab.Terminals.Find(t => t.App is not null);
		if (agent is not null && HerdrAgents.ProgramName(agent.App) is { Length: > 0 } program) { return program; }
		AgentTerminal first = tab.Terminals[0];
		return HerdrAgents.ProgramName(first.App) is { Length: > 0 } firstProgram ? firstProgram : first.Name;
	}

	// The program whose mark a tab shows (AgentTerminal.Mark): its agent's (a terminal that reports, as before), else the first terminal
	// whose program the programs table names, else null (the terminal glyph).
	internal static string? TabMark(AgentTab tab) => tab.Terminals.Find(t => t.App is not null)?.Mark ?? tab.Terminals.Find(t => t.Mark is not null)?.Mark;

	// The mark a tab or a terminal shows: its program's (AgentTerminal.Mark, TabMark), else the terminal glyph.
	internal static View Mark(string? app)
	{
		if (app is not null) { return PaneStrip.AgentMark(app, true); }
		Label glyph = new() { Style = App.Res<Style>("Lst.Glyph"), Text = "\uE94E" };
		AutomationProperties.SetIsInAccessibleTree(glyph, false);
		return glyph;
	}

	// New space offers folders already in use and Browse; selecting one starts the remote shell in a new space and tab, then opens it.
	private static async Task NewSpaceAsync(ArrangeHost host, HerdrCreate create)
	{
		ShellFacts facts = await create.FactsAsync(CancellationToken.None);
		string? folder = await ListSheet.PickFolderAsync(facts.Home, FoldersInUse(), dir => create.ListFoldersAsync(dir, CancellationToken.None), host.CanInsert);
		if (folder is null) { return; }
		string address = await create.RunAsync([], folder, true, HerdrCreate.NewId("sp"), HerdrCreate.NewId("tb"), host.Size, Pane.DisplayColors(), CancellationToken.None);
		await host.Open(address);
	}

	// Each document space's folder, then each live terminal's folder and cwd, without repeats, in that order.
	private static List<string> FoldersInUse()
	{
		HerdrAgents agents = App.Herdr.Agents;
		IEnumerable<string?> folders = HerdrLayout.Spaces(agents.LayoutDocument).Select(space => space["folder"]?.GetValue<string>())
			.Concat(agents.Arranged.SelectMany(space => space.Terminals).SelectMany(t => new[] { agents.FolderOf(t.Address), agents.CwdOf(t.Address) }));
		return [.. folders.OfType<string>().Where(folder => folder.Length > 0).Distinct()];
	}

	// Match herdr-app's new-menu order: Terminal, Start an agent (one row per installed agent), New space.
	// Show cached agents immediately; refresh the list when unknown or older than 60 seconds.
	private static async Task NewMenuAsync(ArrangeHost host, HerdrCreate create, string spaceId)
	{
		AgentSpace? space = App.Herdr.Agents.Arranged.Find(s => s.Id == spaceId);
		if (space is null || spaceId.Length == 0) { return; }
		List<string>? installed = create.Installed;
		MenuSheet? sheet = null;
		Task<MenuSheet.Row?> picked = MenuSheet.PopAsync(space.Name, NewEntries(installed, false), opened => sheet = opened, CanInsert: host.CanInsert);
		if (!create.InstalledFresh) { _ = RefreshAsync(); }
		MenuSheet.Row? row = await picked;
		if (row is null) { return; }
		if (row.Glyph == "\uE902")
		{
			await NewSpaceAsync(host, create);
			return;
		}
		IReadOnlyList<string> argv = row.Agent is { } key
			? await create.AgentArgvAsync(HerdrAgents.Startable.First(agent => agent.Key == key).Run, CancellationToken.None)
			: [];
		await NewTabAsync(host, create, spaceId, argv);

		async Task RefreshAsync()
		{
			bool failed = false;
			try
			{
				installed = await create.CheckInstalledAsync(CancellationToken.None);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Arrange] installed agents: {ex.Message}");
				failed = true;
			}
			// A failed check keeps the last list (herdr-app shows the installed ones it knows); with none, its note.
			if (!picked.IsCompleted) { sheet?.Show(NewEntries(installed, failed)); }
		}
	}

	private static List<MenuSheet.Entry> NewEntries(List<string>? installed, bool failed)
	{
		List<MenuSheet.Entry> entries = [new MenuSheet.Row(i18n.Translate("Terminal"), Glyph: "\uE94E"), new MenuSheet.Section(i18n.Translate("Start an agent"))];
		if (installed is not null)
		{
			entries.AddRange(HerdrAgents.Startable.Where(agent => installed.Contains(agent.Run[0])).Select(agent => new MenuSheet.Row(agent.Name, Agent: agent.Key)));
		}
		else
		{
			entries.Add(new MenuSheet.Note(i18n.Translate(failed ? "Could not check installed agents" : "Checking installed agents…")));
		}
		entries.Add(new MenuSheet.Separator());
		entries.Add(new MenuSheet.Row(i18n.Translate("New space"), Glyph: "\uE902"));
		return entries;
	}

	// Start argv in a new tab (empty argv uses the core shell), then open it. Use the current pane's folder
	// when it belongs to this space; otherwise use the space's current folder.
	private static async Task NewTabAsync(ArrangeHost host, HerdrCreate create, string spaceId, IReadOnlyList<string> argv)
	{
		string? folder = host.Pane is { } pane && SpaceOf(pane)?.Id == spaceId ? App.Herdr.Agents.FolderOf(pane) : null;
		folder ??= await create.WhereSpaceIsAsync(spaceId, CancellationToken.None);
		string address = await create.RunAsync(argv, folder, false, spaceId, HerdrCreate.NewId("tb"), host.Size, Pane.DisplayColors(), CancellationToken.None);
		await host.Open(address);
	}

	// Pending spaces offer New tab and a waiting note; placed spaces offer Rename.
	// All spaces offer Close after herdr-app's separator.
	private static async Task SpaceMenuAsync(ArrangeHost host, HerdrCreate create, string spaceId)
	{
		if (App.Herdr.Agents.Arranged.Find(s => s.Id == spaceId) is not { } space || !HasSpaceMenu(space)) { return; }
		MenuSheet.Row rename = new(i18n.Translate("Rename"), Glyph: Pen);
		MenuSheet.Row newTab = new(i18n.Translate("New tab"), Glyph: "\uE902");
		MenuSheet.Row close = new(i18n.Translate("Close"), Glyph: "\uE91D", Need: true);
		List<MenuSheet.Entry> entries = space.Pending
			? [newTab, new MenuSheet.Separator(), close, new MenuSheet.Note(i18n.Translate("Waiting for Herdr on the machine to place it."))]
			: [rename, new MenuSheet.Separator(), close];
		MenuSheet.Row? row = await MenuSheet.PopAsync(space.Name, entries, lead: AgentList.Tile(space.Name, space.Order), CanInsert: host.CanInsert);
		if (row == rename) { await RenameAsync(host, create, spaceId, null); }
		else if (row == newTab) { await NewTabAsync(host, create, spaceId, []); }
		else if (row == close && create.PlanClose(spaceId, null, null) is { } plan) { await CloseAsync(host, create, plan); }
	}

	// Tab menu matches herdr-app: Rename, New tab, separator, Close.
	// Pending tabs have no Root yet, so they show a waiting note instead of Rename.
	private static async Task TabMenuAsync(ArrangeHost host, HerdrCreate create, string spaceId, string tabId)
	{
		if (App.Herdr.Agents.Arranged.Find(s => s.Id == spaceId)?.Tabs.Find(t => t.Id == tabId) is not { } tab) { return; }
		MenuSheet.Row rename = new(i18n.Translate("Rename"), Glyph: Pen);
		MenuSheet.Row newTab = new(i18n.Translate("New tab"), Glyph: "\uE902");
		MenuSheet.Row close = new(i18n.Translate("Close"), Glyph: "\uE91D", Need: true);
		List<MenuSheet.Entry> entries = tab.Root is null
			? [newTab, new MenuSheet.Separator(), close, new MenuSheet.Note(i18n.Translate("Waiting for Herdr on the machine to place it."))]
			: [rename, newTab, new MenuSheet.Separator(), close];
		MenuSheet.Row? row = await MenuSheet.PopAsync(TabName(tab), entries, lead: Mark(TabMark(tab)), CanInsert: host.CanInsert);
		if (row == rename) { await RenameAsync(host, create, spaceId, tabId); }
		else if (row == newTab) { await NewTabAsync(host, create, spaceId, []); }
		else if (row == close && create.PlanClose(spaceId, tabId, null) is { } plan) { await CloseAsync(host, create, plan); }
	}

	// Rename uses herdr-app's Lucide square-pen icon.
	private const string Pen = "\uE95D";

	// Rename a space (null tabId) or tab in a FormSheet. Titles are "Rename space" / "Rename tab"; the Name field
	// selects the document's current name, or is empty for an automatic name. Hints explain folder / number fallbacks.
	// Save trims text into spaces[i].name or tabs[j].name via EditDocumentAsync: read, edit in place, compare-and-set,
	// at most three attempts. Document events refresh the list. After three conflicts, keep the sheet open with
	// "The layout changed on the machine. Try again."; connection or core errors also keep it open with their message.
	// If the space or tab disappears, write nothing and close the sheet. Cancel makes no change.
	private static async Task RenameAsync(ArrangeHost host, HerdrCreate create, string spaceId, string? tabId)
	{
		JsonObject? space = HerdrLayout.Spaces(App.Herdr.Agents.LayoutDocument).FirstOrDefault(s => HerdrLayout.Id(s) == spaceId);
		JsonObject? tab = space is null || tabId is null ? null : HerdrLayout.Tabs(space).FirstOrDefault(t => HerdrLayout.Id(t) == tabId);
		if (space is null || (tabId is not null && tab is null)) { return; }
		string given = (tab ?? space)["name"] is JsonValue value && value.TryGetValue(out string? name) ? name : "";
		FormSheet.Form form = new(i18n.Translate(tabId is null ? "Rename space" : "Rename tab"),
			i18n.Translate("Save"), SaveAsync,
			i18n.Translate("Name"), i18n.Translate(tabId is null ? "Empty: named after its folder" : "Empty: its number"), given);
		// The keyboard comes up on the field once the sheet has slid up (FormSheet.Shown), the given name selected.
		await FormSheet.PopAsync(form, sheet => sheet.Shown += sheet.FocusField, host.CanInsert);

		async Task<FormSheet.Outcome> SaveAsync(string text)
		{
			string wanted = text.Trim();
			try
			{
				DocEdit edit = await create.EditDocumentAsync(document => SetName(document, spaceId, tabId, wanted), CancellationToken.None);
				return edit == DocEdit.Conflicted ? new FormSheet.Outcome(i18n.Translate("The layout changed on the machine. Try again.")) : new FormSheet.Outcome(null, wanted);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Arrange] rename {spaceId} {tabId}: {ex.Message}");
				return new FormSheet.Outcome(Problem(ex));
			}
		}
	}

	// Sets the name of the space (tabId null) or the tab in the freshly read document; false (nothing to write) when it is gone or
	// already has that name.
	private static bool SetName(JsonNode? document, string spaceId, string? tabId, string name)
	{
		JsonObject? space = HerdrLayout.Spaces(document).FirstOrDefault(s => HerdrLayout.Id(s) == spaceId);
		JsonObject? target = tabId is null ? space : space is null ? null : HerdrLayout.Tabs(space).FirstOrDefault(t => HerdrLayout.Id(t) == tabId);
		if (target is null || (target["name"] is JsonValue value && value.TryGetValue(out string? now) && now == name)) { return false; }
		target["name"] = name;
		return true;
	}

	// Match herdr-app's pane.split: start the core shell in the pane's folder, falling back to the space's current
	// folder, or home if a followed folder has disappeared. Preserve space and tab labels. HerdrLayout.SplitAfter
	// validates the tree and idempotently places the terminal after the pane on the chosen axis, removing any prior
	// automatic adoption at the tab's right edge. Compare-and-set retries at most three times. On success, open
	// the new pane, which herdr-app also focuses. If creation succeeds but the edit fails (pane gone, validation,
	// conflicts, or disconnect), keep the terminal where herdr-app adopts it and show the error; never kill it.
	private static async Task SplitAsync(ArrangeHost host, HerdrCreate create, string pane, string axis)
	{
		if (!CanSplit(pane) || SpaceOf(pane) is not { } space || space.Tabs.Find(t => t.Terminals.Exists(x => x.Address == pane)) is not { } tab) { return; }
		string folder = App.Herdr.Agents.FolderOf(pane) is { Length: > 0 } own ? own : await create.WhereSpaceIsAsync(space.Id, CancellationToken.None);
		string terminal = await create.RunAsync([], folder, false, space.Id, tab.Id, host.Size, Pane.DisplayColors(), CancellationToken.None);
		DocEdit edit = await create.EditDocumentAsync(document =>
			HerdrLayout.SplitAfter(document, pane, terminal, axis, HerdrCreate.NewId("p"), HerdrCreate.NewId("s")) == LayoutSplit.Done, CancellationToken.None);
		if (edit == DocEdit.Written)
		{
			await host.Open(terminal);
			return;
		}
		Debug.WriteLine($"[Arrange] split {axis} of {pane} with {terminal}: {edit}");
		await BusyRunner.Show("Error", i18n.Translate(edit == DocEdit.Conflicted ? "The layout changed on the machine. Try again." : "Something went wrong, please try again"), host.CanInsert);
	}

	// Compute the full kill set, including all descendant agents. Confirm a space with "Close space?" and
	// "<name> — n tabs, m terminals"; for a pane or tab, list each working or blocked terminal before confirmation.
	// The host leaves affected panes first, selecting the nearest survivor or returning to the list.
	// Await kills one at a time; stop on the first failure without undoing completed kills.
	private static async Task CloseAsync(ArrangeHost host, HerdrCreate create, ClosePlan plan)
	{
		List<string> kill = create.KillSet(plan.Terminals);
		List<string> lines = WorkingLines(kill);
		if (plan.Level == CloseLevel.Space || lines.Count > 0)
		{
			string title = i18n.Translate(plan.Level switch { CloseLevel.Space => "Close space?", CloseLevel.Tab => "Close tab?", _ => "Close pane?" });
			if (plan.Level == CloseLevel.Space)
			{
				lines.Insert(0, string.Format(CultureInfo.CurrentCulture, i18n.Translate("{0} — {1}, {2}"), plan.Space.Name,
					Count(plan.Tabs, "1 tab", "{0} tabs"), Count(plan.Panes, "1 terminal", "{0} terminals")));
			}
			string confirm = i18n.Translate("Confirm");
			if (await PopupsHandle.PopDisplayAsync(title, string.Join("\n", lines), [confirm, i18n.Translate("Cancel")], CanInsert: host.CanInsert) != confirm) { return; }
			// What runs may have changed while the question was open (a sub-agent started): the set again, from the same terminals.
			kill = create.KillSet(plan.Terminals);
		}
		await KillAllAsync(host, kill);
	}

	// Confirmation lists each working or blocked terminal in the kill set by program name, falling back to terminal name.
	private static List<string> WorkingLines(IEnumerable<string> kill)
	{
		HerdrAgents agents = App.Herdr.Agents;
		return [.. kill.Select(address => agents.Terminals.GetValueOrDefault(address)).OfType<AgentTerminal>()
			.Where(t => t.Word is "working" or "blocked")
			.Select(t => string.Format(CultureInfo.CurrentCulture, i18n.Translate(t.Word == "working" ? "{0} is working." : "{0} needs you"),
				HerdrAgents.ProgramName(t.App) ?? t.Name))];
	}

	// Check the connection before moving the host or killing anything; a disconnected close reports "Connection lost".
	// Then leave affected panes and await each kill in order. A failed kill throws and stops the sequence.
	private static async Task KillAllAsync(ArrangeHost host, List<string> kill)
	{
		if (App.Herdr.State != SessionState.Connected || App.Herdr.Api is not { } api)
		{
			await BusyRunner.Show("Error", i18n.Translate("Connection lost"), host.CanInsert);
			return;
		}
		if (host.Closing is { } closing) { await closing(kill); }
		foreach (string address in kill) { await api.KillAsync(address, CancellationToken.None); }
	}

	// Close pane from the sheet or menu follows herdr-app's pane.close rules.
	private static async Task ClosePaneAsync(ArrangeHost host, HerdrCreate create, string address)
	{
		if (SpaceOf(address) is { Id.Length: > 0 } space && create.PlanClose(space.Id, null, address) is { } plan) { await CloseAsync(host, create, plan); }
	}

	// What a failed step shows: the core's message, "Connection lost" when the line dropped, else the generic error.
	private static string Problem(Exception ex) => ex switch
	{
		CoreRpcException rpc => rpc.CoreMessage,
		CoreLostException => i18n.Translate("Connection lost"),
		_ when App.Herdr.State != SessionState.Connected => i18n.Translate("Connection lost"),
		_ => i18n.Translate("Something went wrong, please try again"),
	};

	// Format counts with the appropriate singular or plural translation.
	private static string Count(int n, string one, string many) =>
		n == 1 ? i18n.Translate(one) : string.Format(CultureInfo.CurrentCulture, i18n.Translate(many), n);

	// A pane's shown name (the pane sheet's head, its map's pieces, the pane menu's title): its agent's program name, else the
	// terminal's name, as herdr-app's pane header.
	internal static string PaneName(AgentTerminal terminal) => HerdrAgents.ProgramName(terminal.App) is { Length: > 0 } program ? program : terminal.Name;

	// The remote home for paths shown with "~"; a failed lookup leaves the full path visible.
	internal static async Task<string?> HomeAsync()
	{
		if (Create() is not { } create) { return null; }
		try { return (await create.FactsAsync(CancellationToken.None)).Home; }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Arrange] home: {ex.Message}");
			return null;
		}
	}

	// Pane menu: Split right, Split down (CanSplit); Find in terminal; Pin / Unpin (agents only);
	// Read aloud or Stop reading; separator; Close pane (CanClosePane).
	// Ungrouped terminals offer only Find, Pin / Unpin, and Read aloud. Read invokes the page's existing read-key action.
	private static async Task PaneMenuAsync(ArrangeHost host, HerdrCreate create, string address)
	{
		if (App.Herdr.Agents.Terminals.GetValueOrDefault(address) is not { } pane) { return; }
		bool reading = host.Reading?.Invoke() ?? false;
		MenuSheet.Row find = new(i18n.Translate("Find in terminal"), Glyph: "");
		MenuSheet.Row pin = new(i18n.Translate(pane.Pinned ? "Unpin" : "Pin"), Glyph: "");
		MenuSheet.Row read = new(i18n.Translate(reading ? "Stop reading" : "Read aloud"), Glyph: reading ? "" : "");
		MenuSheet.Row close = new(i18n.Translate("Close pane"), Glyph: "", Need: true);
		MenuSheet.Row splitRight = new(i18n.Translate("Split right"), Glyph: "");
		MenuSheet.Row splitDown = new(i18n.Translate("Split down"), Glyph: "");
		List<MenuSheet.Entry> entries = CanSplit(address) ? [splitRight, splitDown, find] : [find];
		if (pane.Word is not null) { entries.Add(pin); }
		entries.Add(read);
		if (CanClosePane(address)) { entries.AddRange([new MenuSheet.Separator(), close]); }
		string? home = null;
		try { home = (await create.FactsAsync(CancellationToken.None)).Home; }
		catch (Exception ex) { Debug.WriteLine($"[Arrange] pane menu home: {ex.Message}"); }
		string subtitle = App.Herdr.Agents.FolderOf(address) is { Length: > 0 } folder
			? $"{address} · {(home is null ? folder : RemotePath.Tilde(folder, home, RemotePath.IsWindows(home)))}"
			: address;
		MenuSheet.Row? row = await MenuSheet.PopAsync(PaneName(pane), entries, lead: Mark(pane.Mark), subtitle: subtitle, CanInsert: host.CanInsert);
		if (row == splitRight) { await SplitAsync(host, create, address, HerdrLayout.Right); }
		else if (row == splitDown) { await SplitAsync(host, create, address, HerdrLayout.Down); }
		else if (row == find) { host.Find?.Invoke(); }
		else if (row == read) { host.Read?.Invoke(); }
		else if (row == pin && App.Herdr.Api is { } api) { await api.LabelAsync(address, "herdr.pinned", pane.Pinned ? "" : "1", CancellationToken.None); }
		else if (row == close) { await ClosePaneAsync(host, create, address); }
	}

	// Pin sets herdr.pinned to "1"; Unpin removes it. Label events refresh the list.
	private static async Task AgentMenuAsync(ArrangeHost host, string address)
	{
		if (App.Herdr.Agents.Terminals.GetValueOrDefault(address) is not { } agent) { return; }
		string title = agent.Space.Length > 0 ? agent.Space : agent.Title;
		string subtitle = agent.Word is { } word ? $"{address} · {PaneStrip.StatusText(word)}" : address;
		MenuSheet.Row? row = await MenuSheet.PopAsync(title, [new MenuSheet.Row(i18n.Translate(agent.Pinned ? "Unpin" : "Pin"), Glyph: "\uE95F")],
			lead: Mark(agent.Mark), subtitle: subtitle, CanInsert: host.CanInsert);
		if (row is null || App.Herdr.Api is not { } api) { return; }
		await api.LabelAsync(address, "herdr.pinned", agent.Pinned ? "" : "1", CancellationToken.None);
	}
}
