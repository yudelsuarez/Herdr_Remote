using System.Globalization;
using System.Text.Json.Nodes;
using HerdrRemote.Remote;
using Languages;
using Microsoft.Maui.Controls.Shapes;

namespace HerdrRemote.Pages;

// Pane pill shows terminal status, name, and "i/n" within its herdr-app space; multi-pane tabs replace the
// position with a miniature layout highlighting this pane. Tapped opens the pane sheet, or a name-only
// pane menu outside a space. Swiping left/right selects the next/previous pane across tabs in space order,
// raises PaneTapped, and uses Neighbor. The Pane supplies HerdrAgents.Arranged; this control makes no request.
public partial class PaneStrip : ContentView
{
	// The addresses of the open terminal's group in order, and what the pill shows now (a redraw happens only when it changes).
	private List<string> _ids = [];
	private string _shown = "";
	private string _address = "";
	// The open terminal's space name; null for a terminal no space holds.
	private string? _space;
	// The header's last room for the pill (FitName), 0 before the first.
	private double _room;

	public PaneStrip()
	{
		InitializeComponent();
	}

	public event Action<string>? PaneTapped;

	// Notify the page when the pane pill is tapped.
	public event Action? Tapped;

	// An agent's shown status (AgentTerminal.Word) as its catalog text (the strip, the Pane header and the Herdr list).
	internal static string StatusText(string status) => i18n.Translate(status switch { "idle" => "Idle", "working" => "Working", "blocked" => "Blocked", "done" => "Done", _ => "Unknown" });

	// Shared status color keys for the pill dot, Pane subtitle, and agent list.
	internal static string StatusColorKey(string status) => status switch { "working" => "Work", "blocked" => "Need", "done" => "Done", "idle" => "Idle", _ => "Ink3" };

	// Agent glyphs come from Art/icons/glyphs.json (agent-*), keyed by HerdrAgents.Programs;
	// unknown programs retain herdr's agent id. Each mark has light/dark colors and an optional brand tile.
	// Untiled marks retain brand color only with at least 3:1 non-text contrast against #EBF0F7 / #18223A.
	// Null uses muted ink: black reaches only 1.10:1 on the dark tile. Claude's #D97757 reaches 2.73:1 on the
	// light tile, so use Anthropic's darker Crail #C15F3C (3.69:1).
	// Sources: Simple Icons 16.35.0 data/simple-icons.json hex values, with slugs in Art/icons/icons.json;
	// OpenAI's developers.openai.com/favicon.png, kiro.dev/icon.svg, kilo.ai/favicon/favicon.svg,
	// ampcode.com/app-icon.png, and the main blue in antigravity.google/apple-touch-icon.png.
	// Other marks are black/white or lack a verified official source (maki, muse), so keep them muted.
	private static readonly Dictionary<string, (string Glyph, Color? Light, Color? Dark, Color? Tile)> AgentGlyphs = new()
	{
		["claude"] = ("\uE935", Color.FromArgb("#C15F3C"), Color.FromArgb("#D97757"), null),
		["codex"] = ("\uE936", Color.FromArgb("#FFFFFF"), Color.FromArgb("#FFFFFF"), Color.FromArgb("#0080F7")),
		["pi"] = ("\uE937", Color.FromArgb("#000000"), null, null),
		["gemini"] = ("\uE938", Color.FromArgb("#8E75B2"), Color.FromArgb("#8E75B2"), null),
		["opencode"] = ("\uE939", Color.FromArgb("#000000"), null, null),
		["cursor"] = ("\uE93B", Color.FromArgb("#000000"), null, null),
		["devin"] = ("\uE93C", null, null, null),
		["antigravity"] = ("\uE93D", Color.FromArgb("#3087FB"), Color.FromArgb("#3087FB"), null),
		["cline"] = ("\uE93E", Color.FromArgb("#18181B"), null, null),
		["copilot"] = ("\uE93F", Color.FromArgb("#000000"), null, null),
		["kimi"] = ("\uE940", Color.FromArgb("#000000"), null, null),
		["kiro"] = ("\uE941", Color.FromArgb("#FFFFFF"), Color.FromArgb("#FFFFFF"), Color.FromArgb("#9046FF")),
		["droid"] = ("\uE942", null, null, null),
		["amp"] = ("\uE943", Color.FromArgb("#DFDFC1"), Color.FromArgb("#DFDFC1"), Color.FromArgb("#071A16")),
		["grok"] = ("\uE944", null, null, null),
		["hermes"] = ("\uE945", null, null, null),
		["kilo"] = ("\uE946", Color.FromArgb("#000000"), Color.FromArgb("#000000"), Color.FromArgb("#FFDD00")),
		["qoder"] = ("\uE947", null, null, null),
		["qwen"] = ("\uE948", Color.FromArgb("#6950EF"), Color.FromArgb("#6950EF"), null),
		["letta"] = ("\uE949", null, null, null),
		["maki"] = ("\uE94A", null, null, null),
		["muse"] = ("\uE94B", null, null, null),
	};

	// Image-based agent marks live in Resources/Images. Gentle Shell uses its official rose logo,
	// vectorized in Art/icons/agents/gentle-shell.
	private static readonly Dictionary<string, string> AgentImages = new() { ["gentle-shell"] = "gentle_shell.png" };

	// Render the lowercase program mark from AgentTerminal.Mark, the programs table, or a start-row key.
	// Use its brand tile when defined; otherwise leave it bare, as herdr-app does for Claude. Do not add
	// a status badge. Marks without a color use Ink2; small selects the compact-row size.
	internal static Border AgentMark(string agent, bool small)
	{
		string id = agent.ToLowerInvariant();
		if (AgentImages.TryGetValue(id, out string? file))
		{
			return new Border { Style = App.Res<Style>(small ? "Mark.Small" : "Mark"), BackgroundColor = Colors.Transparent, Content = new Image { Source = file, Aspect = Aspect.AspectFill } };
		}
		(string glyph, Color? light, Color? dark, Color? tile) = AgentGlyphs.GetValueOrDefault(id, ("\xE93A", null, null, null));
		Label mark = new() { Style = App.Res<Style>(tile is null ? (small ? "Mark.BareSmall" : "Mark.Bare") : (small ? "Mark.OnTileSmall" : "Mark.OnTile")), Text = glyph };
		mark.SetAppThemeColor(Label.TextColorProperty, light ?? App.Res<Color>("Ink2"), dark ?? App.Res<Color>("Ink2Dark"));
		return new Border { Style = App.Res<Style>(small ? "Mark.Small" : "Mark"), BackgroundColor = tile, Content = mark };
	}

	// Show the terminal's position in its ordered group; hide the pill if no group contains it.
	// Include the current tab's tree key and live terminals in the cache key so only layout changes redraw the map.
	internal void Show(List<AgentSpace> spaces, string address)
	{
		_address = address;
		AgentSpace? group = spaces.Find(space => space.Terminals.Exists(terminal => terminal.Address == address));
		List<AgentTerminal> terminals = group?.Terminals ?? [];
		AgentTab? tab = group?.Tabs.Find(t => t.Terminals.Exists(terminal => terminal.Address == address));
		JsonObject? root = group is { Id.Length: > 0 } && tab?.Root is { } tree && LayoutMap.Pieces(tree) > 1 ? tree : null;
		string shown = $"{group?.Name}\n{(root is null ? "" : $"{tab!.Key}\t{string.Join(",", tab.Terminals.Select(t => t.Address))}")}\n"
			+ string.Join("\n", terminals.Select(terminal => $"{terminal.Address}\t{terminal.Name}\t{terminal.Word}\t{terminal.Address == address}"));
		if (shown == _shown) { return; }
		_shown = shown;
		_ids = [.. terminals.Select(terminal => terminal.Address)];
		_space = group is { Id.Length: > 0 } ? group.Name : null;
		AgentTerminal? open = terminals.Find(terminal => terminal.Address == address);
		IsVisible = open is not null;
		if (open is null) { return; }
		string status = open.Word ?? "";
		string color = StatusColorKey(status);
		Dot.SetAppTheme<Brush>(Shape.FillProperty, new SolidColorBrush(App.Res<Color>(color)), new SolidColorBrush(App.Res<Color>(color + "Dark")));
		Lbl_Name.Text = open.Name;
		Lbl_Count.Text = string.Format(CultureInfo.CurrentCulture, "{0}/{1}", _ids.IndexOf(address) + 1, _ids.Count);
		Lbl_Count.IsVisible = _space is not null && root is null;
		MapSlot.Content = root is null ? null : new LayoutMap(root, App.Herdr.Agents.Terminals.Keys.ToHashSet(), address);
		MapSlot.IsVisible = root is not null;
		Lbl_Chev.IsVisible = _space is not null;
		SemanticProperties.SetDescription(Pill, $"{open.Name}, {StatusText(status)}");
		// Recalculate name width when the count or chevron appears or disappears.
		if (_room > 0) { FitName(_room); }
	}

	// The pill's name may grow to fill this width (the header's room for it, on every change of it): the room less the pill's padding,
	// dot, the count or the map and the chevron while shown, and the spacing between them; never under the style's 44.
	public void FitName(double room)
	{
		_room = room;
		double rest = Pill.Padding.HorizontalThickness + Dot.WidthRequest + PillRow.Spacing;
		foreach (Label label in new[] { Lbl_Count, Lbl_Chev }.Where(label => label.IsVisible))
		{
			rest += label.Measure(double.PositiveInfinity, double.PositiveInfinity).Width + PillRow.Spacing;
		}
		if (MapSlot.IsVisible && MapSlot.Content is { } map) { rest += map.WidthRequest + PillRow.Spacing; }
		Lbl_Name.MaximumWidthRequest = Math.Max(44, room - rest);
		// On iOS a new width limit can retain the old measurement (observed: 189 allowed, 44 laid out).
		// Measure both row and strip again.
		Dispatcher.Dispatch(() =>
		{
			((IView)Lbl_Name).InvalidateMeasure();
			((IView)PillRow).InvalidateMeasure();
			((IView)Pill).InvalidateMeasure();
			InvalidateMeasure();
		});
	}

	// The pane next to paneId in chip order (direction +1 or -1), null at either end.
	public string? Neighbour(string paneId, int direction)
	{
		int next = _ids.IndexOf(paneId) + direction;
		return _ids.Contains(paneId) && next >= 0 && next < _ids.Count ? _ids[next] : null;
	}

	// Tapping opens the pane sheet through the Pane's handler.
	private void Pill_Tapped(object? sender, TappedEventArgs e) => Tapped?.Invoke();

	// Left = the next pane, right = the previous one (the terminal's own swipe, TerminalView).
	private void Pill_Swiped(object? sender, SwipedEventArgs e)
	{
		if (Neighbour(_address, e.Direction == SwipeDirection.Left ? 1 : -1) is { } next) { PaneTapped?.Invoke(next); }
	}
}
