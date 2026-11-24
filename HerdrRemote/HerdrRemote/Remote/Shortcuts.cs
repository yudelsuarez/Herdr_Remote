using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HerdrRemote.Remote;

public sealed record ShortcutItem(string Type, string Value)
{
	public static ShortcutItem Text(string value) => new("text", value);
	public static ShortcutItem Key(string named) => new("key", named);
}

public sealed record ShortcutAction(string? BuiltIn = null, string? Character = null, IReadOnlyList<ShortcutItem>? Items = null);
public sealed record KeyBarEntry(string Id, string Label, ShortcutAction Action);
public sealed record Corner(string Position, string Label, ShortcutAction? Action, string Glyph);

// Configuration only; built-in behavior stays in KeyBar/DPad. Sequences never see their modifier state.
internal sealed record Shortcuts(IReadOnlyList<KeyBarEntry> KeyBar, IReadOnlyList<Corner> Corners, int Version = 1)
{
	private const string SettingKey = "herdr.shortcuts";
	private static Shortcuts? _current;
	public static Shortcuts Current => _current ??= Load();
	public static event Action? Changed;

	public static Shortcuts Defaults => new(
	[
		new("esc", "Esc", new("esc")), new("tab", "Tab", new("tab")),
		new("ctrl", "Ctrl", new("ctrl")), new("alt", "Alt", new("alt")),
		new("enter", "\uE959", new("enter")), new("backtab", "⇧Tab", new("backtab")),
		new("shift", "Shift", new("shift")), new("dpad", "\uE95C", new("dpad")),
		new("ctrlc", "^C", new("ctrlc")), new("pipe", "|", new("char", "|")),
		new("slash", "/", new("char", "/")), new("tilde", "~", new("char", "~")),
		new("minus", "-", new("char", "-")), new("pgup", "PgUp", new("pgup")),
		new("pgdn", "PgDn", new("pgdn")), new("home", "Home", new("home")), new("end", "End", new("end")),
	],
	[
		new("tl", "Backspace", new("backspace"), "\uE95A"), new("tr", "Ctrl+C", new("ctrlc"), "\uE95B"),
		new("bl", "Esc", new("esc"), "\uE96F"),
		new("br", "clear", new(Items: [ShortcutItem.Text("clear"), ShortcutItem.Key("enter")]), "\uE96E"),
	]);

	public static Shortcuts Load()
	{
		string saved = App.Setting(SettingKey, "");
		if (saved.Length == 0) { return Defaults; }
		try
		{
			if (JsonNode.Parse(saved) is not JsonObject root || root["version"] is not { } version
				|| root["keyBar"] is not JsonArray bar || root["corners"] is not JsonArray corners) { return Defaults; }
			List<KeyBarEntry> entries = [];
			foreach (JsonNode? node in bar)
			{
				if (node is not JsonObject entry || entry["id"]?.GetValue<string>() is not { } id
					|| entry["label"]?.GetValue<string>() is not { } label
					|| ReadAction(entry["action"]) is not { } action) { return Defaults; }
				entries.Add(new(id, label, action));
			}
			List<Corner> positions = [];
			foreach (JsonNode? node in corners)
			{
				if (node is not JsonObject entry || entry["position"]?.GetValue<string>() is not { } position
					|| entry["label"]?.GetValue<string>() is not { } label || !entry.ContainsKey("action")
					|| entry["glyph"]?.GetValue<string>() is not { } glyph) { return Defaults; }
				ShortcutAction? action = ReadAction(entry["action"]);
				if (entry["action"] is not null && action is null) { return Defaults; }
				positions.Add(new(position, label, action, glyph));
			}
			Shortcuts configuration = new(entries, positions, version.GetValue<int>());
			if (!WellFormed(configuration)) { return Defaults; }
			return configuration with
			{
				KeyBar = [.. configuration.KeyBar.Where(entry => Known(entry.Action, entry.Id))],
				Corners = [.. configuration.Corners.Where(entry => Known(entry.Action, entry.Position))],
			};
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			return Defaults;
		}
	}

	public static void Save(Shortcuts configuration)
	{
		if (!WellFormed(configuration) || configuration.KeyBar.Any(entry => !Known(entry.Action, entry.Id))
			|| configuration.Corners.Any(entry => !Known(entry.Action, entry.Position)))
		{
			throw new ArgumentException("Invalid shortcuts configuration", nameof(configuration));
		}
		string json = configuration.Json().ToJsonString();
		if (json == Current.Json().ToJsonString()) { return; }
		App.SaveSetting(SettingKey, json);
		_current = Load();
		Changed?.Invoke();
	}

	public static void Reset() => Save(Defaults);

	public static byte[] Encode(IReadOnlyList<ShortcutItem> items, TerminalModes modes)
	{
		if (!ValidItems(items) || items.Any(item => item.Type == "key" && !NamedKey(item.Value)))
		{
			throw new ArgumentException("Invalid shortcut sequence", nameof(items));
		}
		using MemoryStream bytes = new();
		foreach (ShortcutItem item in items)
		{
			bytes.Write(item.Type == "text" ? TerminalInput.Text(item.Value, false, modes) : TerminalInput.Key(item.Value, modes));
		}
		return bytes.ToArray();
	}

	private static bool NamedKey(string name) => name is "esc" or "enter" or "tab" or "shift+tab" or "backspace"
		or "up" or "down" or "left" or "right" or "home" or "end"
		|| name is ['c', 't', 'r', 'l', '+', >= 'a' and <= 'z'];

	private static bool ValidItems(IReadOnlyList<ShortcutItem>? items) => items is { Count: >= 1 and <= 32 }
		&& items.All(item => item is { Value: not null, Type: "text" or "key" })
		&& items.Where(item => item.Type == "text").Sum(item => (long)item.Value.Length) <= 1024;

	private static bool WellFormed(ShortcutAction? action) => action is null || (action.BuiltIn is not null
		? action.Items is null && (action.BuiltIn == "char" ? action.Character is "|" or "/" or "~" or "-" : action.Character is null)
		: action.Character is null && ValidItems(action.Items));

	private static bool WellFormed(Shortcuts configuration)
	{
		if (configuration is not { Version: 1, KeyBar: not null, Corners: not null }
			|| configuration.KeyBar.Any(entry => entry is not { Id.Length: > 0, Label.Length: >= 1 and <= 8, Action: not null } || !WellFormed(entry.Action))
			|| configuration.Corners.Any(entry => entry is null || entry.Position is not ("tl" or "tr" or "bl" or "br")
				|| entry.Label is null || entry.Glyph is not ("" or "\uE96E" or "\uE95B" or "\uE926" or "\uE96F" or "\uE95A" or "\uE959" or "\uE916" or "\uE934" or "\uE94E")
				|| !WellFormed(entry.Action))
			|| configuration.KeyBar.Select(entry => entry.Id).Distinct().Count() != configuration.KeyBar.Count
			|| configuration.Corners.Select(entry => entry.Position).Distinct().Count() != configuration.Corners.Count) { return false; }
		foreach (string id in new[] { "ctrl", "alt", "shift", "dpad" })
		{
			if (configuration.KeyBar.Count(entry => entry.Action.BuiltIn == id) > 1) { return false; }
		}
		return true;
	}

	private static bool Known(ShortcutAction? action, string id)
	{
		bool known = action is null || (action.BuiltIn is { } builtIn
			? builtIn is "esc" or "tab" or "enter" or "backtab" or "backspace" or "up" or "down" or "left" or "right" or "home" or "end"
				or "ctrl" or "alt" or "shift" or "dpad" or "ctrlc" or "pgup" or "pgdn" or "char"
			: action.Items!.All(item => item.Type != "key" || NamedKey(item.Value)));
		if (!known) { Debug.WriteLine($"[Herdr] unknown shortcut: {id}"); }
		return known;
	}

	private static ShortcutAction? ReadAction(JsonNode? node)
	{
		if (node is not JsonObject action) { return null; }
		List<ShortcutItem>? items = null;
		if (action["items"] is { } sequence)
		{
			if (sequence is not JsonArray array) { return null; }
			items = [];
			foreach (JsonNode? item in array)
			{
				if (item is not JsonObject value || value["t"]?.GetValue<string>() is not { } type
					|| value["v"]?.GetValue<string>() is not { } text) { return null; }
				items.Add(new(type, text));
			}
		}
		return new(action["builtIn"]?.GetValue<string>(), action["character"]?.GetValue<string>(), items);
	}

	private static JsonObject ActionJson(ShortcutAction action) => action.BuiltIn is { } builtIn
		? new JsonObject { ["builtIn"] = builtIn, ["character"] = action.Character }
		: new JsonObject { ["items"] = new JsonArray([.. action.Items!.Select(item => (JsonNode)new JsonObject { ["t"] = item.Type, ["v"] = item.Value })]) };

	private JsonObject Json() => new()
	{
		["version"] = Version,
		["keyBar"] = new JsonArray([.. KeyBar.Select(entry => (JsonNode)new JsonObject
			{ ["id"] = entry.Id, ["label"] = entry.Label, ["action"] = ActionJson(entry.Action) })]),
		["corners"] = new JsonArray([.. Corners.Select(entry => (JsonNode)new JsonObject
			{ ["position"] = entry.Position, ["label"] = entry.Label, ["glyph"] = entry.Glyph, ["action"] = entry.Action is null ? null : ActionJson(entry.Action) })]),
	};
}
