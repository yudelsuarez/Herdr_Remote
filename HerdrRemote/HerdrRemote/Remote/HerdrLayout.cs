using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace HerdrRemote.Remote;

// A visible tab pane and its relative area (0..1 in each dimension), in tree order. Terminal is null for a mod pane.
internal sealed record LayoutCell(string Id, string? Terminal, double X, double Y, double Width, double Height);

// Split result: Done inserts the pane after the target; Missing means the target or tab is gone;
// Refused means tree validation failed and the result must not be written.
internal enum LayoutSplit { Done, Missing, Refused }

// Edit herdr-app layout documents in place as JSON to preserve unknown fields. The "herdr-app" document contains spaces,
// tabs and each tab's `root` tree. Nodes contain id, type (block|row|column|stack), optional size ({weight}|{px}) and hidden.
// Containers have children; blocks have content ({terminal}|{mod}); stacks have active.
// Split, removal, collapse and visibility rules mirror herdr-app, as do relative pane areas.
// No MAUI dependency, so a standalone console harness can compile this code.
internal static class HerdrLayout
{
	public const string Right = "row";
	public const string Down = "column";

	// The document's spaces (none when it was never written).
	public static IEnumerable<JsonObject> Spaces(JsonNode? document) => (document?["spaces"] as JsonArray ?? []).OfType<JsonObject>();

	public static IEnumerable<JsonObject> Tabs(JsonObject space) => (space["tabs"] as JsonArray ?? []).OfType<JsonObject>();

	public static string? Id(JsonNode? node) => Text(node?["id"]);

	public static string? Type(JsonNode? node) => Text(node?["type"]);

	// The terminal a block shows; null for a container or a mod pane.
	public static string? Terminal(JsonNode? node) => Type(node) == "block" ? Text(node?["content"]?["terminal"]) : null;

	public static bool IsContainer(JsonNode? node) => Type(node) is "row" or "column" or "stack";

	public static IEnumerable<JsonObject> Children(JsonNode? node) =>
		IsContainer(node) ? (node!["children"] as JsonArray ?? []).OfType<JsonObject>() : [];

	// Every node of a tree, depth first, the node itself first.
	public static IEnumerable<JsonObject> Walk(JsonObject node)
	{
		yield return node;
		foreach (JsonObject child in Children(node))
		{
			foreach (JsonObject n in Walk(child)) { yield return n; }
		}
	}

	// Terminal addresses in tree order, matching herdr-app's `terminals` traversal.
	public static List<string> Terminals(JsonObject root) => [.. Walk(root).Select(Terminal).OfType<string>()];

	// The space and tab with this id.
	public static (JsonObject Space, JsonObject Tab)? FindTab(JsonNode? document, string tabId)
	{
		foreach (JsonObject space in Spaces(document))
		{
			foreach (JsonObject tab in Tabs(space))
			{
				if (Id(tab) == tabId) { return (space, tab); }
			}
		}
		return null;
	}

	// The space and tab whose tree holds a block of this terminal.
	public static (JsonObject Space, JsonObject Tab)? FindTabOf(JsonNode? document, string terminal)
	{
		foreach (JsonObject space in Spaces(document))
		{
			foreach (JsonObject tab in Tabs(space))
			{
				if (tab["root"] is JsonObject root && FindBlock(root, terminal) is not null) { return (space, tab); }
			}
		}
		return null;
	}

	// The block showing this terminal.
	public static JsonObject? FindBlock(JsonObject root, string terminal) => Walk(root).FirstOrDefault(n => Terminal(n) == terminal);

	// The container holding the node with this id; null for the root or an id the tree does not hold.
	public static JsonObject? Parent(JsonObject root, string id) => Walk(root).FirstOrDefault(node => Children(node).Any(child => Id(child) == id));

	// Idempotently place terminal T after pane P along an axis (Right = row, Down = column).
	// If herdr-app already adopted T into the same tab, remove its pane using herdr-app rules before reinserting it.
	// Otherwise create a block with `blockId`; use `wrapperId` if a container is needed. Validate that the resulting tree contains
	// the original terminals plus T exactly once and has unique, nonempty node IDs. Missing means P is absent or has no parent.
	public static LayoutSplit SplitAfter(JsonNode? document, string p, string t, string axis, string blockId, string wrapperId)
	{
		if (FindTabOf(document, p) is not { } found || found.Tab["root"] is not JsonObject root
			|| FindBlock(root, p) is not { } before || Parent(root, Id(before) ?? "") is null) { return LayoutSplit.Missing; }
		List<string> expected = [.. Terminals(root).Where(terminal => terminal != t), t];
		JsonObject block;
		if (FindBlock(root, t) is { } existing && Id(existing) is { } existingId)
		{
			Remove(root, existingId);
			block = existing;
			// Placed as a new pane: the split gives it its size.
			block.Remove("size");
		}
		else
		{
			block = new JsonObject { ["id"] = blockId, ["type"] = "block", ["content"] = new JsonObject { ["terminal"] = t } };
		}
		if (FindBlock(root, p) is not { } target || Id(target) is not { } targetId || !Split(root, targetId, axis, block, wrapperId)) { return LayoutSplit.Refused; }
		return Check(root, expected) ? LayoutSplit.Done : LayoutSplit.Refused;
	}

	// Insert `block` after `targetId` along `axis`, matching herdr-app's `split`. If the parent uses the same axis, halve the
	// target's weight and give the other half to the block. Missing or px sizes count as weight 1; preserve the target's px size.
	// Otherwise wrap both nodes in a new {wrapperId, axis} container that inherits the target's size; remove that size from the target.
	// Never replace a root: it has no parent and carries the tab ID. Return false instead.
	public static bool Split(JsonObject root, string targetId, string axis, JsonObject block, string wrapperId)
	{
		if (Parent(root, targetId) is not { } parent || parent["children"] is not JsonArray children) { return false; }
		int i = IndexOf(children, targetId);
		JsonObject target = (JsonObject)children[i]!;
		if (Type(parent) == axis)
		{
			double share = Weight(target["size"]) ?? 1;
			if (Px(target["size"]) is null) { target["size"] = WeightSize(share / 2); }
			block["size"] = WeightSize(share / 2);
			children.Insert(i + 1, block);
			return true;
		}
		children.RemoveAt(i);
		JsonNode? size = target["size"];
		target.Remove("size");
		JsonObject wrapper = new() { ["id"] = wrapperId, ["type"] = axis, ["children"] = new JsonArray(target, block) };
		if (size is not null) { wrapper["size"] = size; }
		children.Insert(i, wrapper);
		return true;
	}

	// Remove a node using herdr-app rules: transfer its weight to the previous sibling, or the next if none precedes it,
	// unless either has a px size. If the active stack child is removed, activate the child that slides into its place, or the last.
	// Remove empty rows and columns; collapse single-child rows and columns, passing their size to the child.
	// Never replace the tab root; retain its sole child's weight.
	public static bool Remove(JsonObject root, string id)
	{
		if (!RemoveInner(root, id)) { return false; }
		Collapse(root);
		return true;
	}

	private static bool RemoveInner(JsonObject node, string id)
	{
		if (!IsContainer(node) || node["children"] is not JsonArray children) { return false; }
		int i = IndexOf(children, id);
		if (i < 0) { return children.OfType<JsonObject>().Any(child => RemoveInner(child, id)); }
		JsonNode? removed = children[i];
		children.RemoveAt(i);
		if (Type(node) is "row" or "column" && WeightOrOne(removed?["size"]) is { } gone
			&& children.Count > 0 && children[Math.Max(i - 1, 0)] is JsonObject neighbour && WeightOrOne(neighbour["size"]) is { } w)
		{
			neighbour["size"] = WeightSize(w + gone);
		}
		if (Type(node) == "stack" && Text(node["active"]) == id)
		{
			string? next = Id(i < children.Count ? children[i] : children.Count > 0 ? children[^1] : null);
			if (next is null) { node.Remove("active"); }
			else { node["active"] = next; }
		}
		return true;
	}

	private static void Collapse(JsonObject node)
	{
		if (!IsContainer(node) || node["children"] is not JsonArray children) { return; }
		foreach (JsonObject child in children.OfType<JsonObject>()) { Collapse(child); }
		// A stack's children are tabs: they keep their identity even with one pane or none.
		if (Type(node) == "stack") { return; }
		for (int i = children.Count - 1; i >= 0; i--)
		{
			if (Type(children[i]) is "row" or "column" && !Children(children[i]).Any()) { children.RemoveAt(i); }
		}
		for (int i = 0; i < children.Count; i++)
		{
			if (children[i] is JsonObject n && Type(n) is "row" or "column" && n["children"] is JsonArray only && only.Count == 1 && only[0] is JsonObject child)
			{
				JsonNode? size = n["size"];
				n.Remove("size");
				only.RemoveAt(0);
				if (size is null) { child.Remove("size"); }
				else { child["size"] = size; }
				children[i] = child;
			}
		}
	}

	// Validate before writing: terminals must equal `expected`, each once. Every node needs a known type, the appropriate
	// children or content, and a unique ID. Check IDs within this tree only: the root intentionally shares its tab's ID.
	public static bool Check(JsonObject root, IReadOnlyCollection<string> expected)
	{
		List<string> terminals = Terminals(root);
		if (terminals.Count != expected.Count || terminals.Distinct().Count() != terminals.Count || !terminals.ToHashSet().SetEquals(expected)) { return false; }
		HashSet<string> ids = [];
		foreach (JsonObject node in Walk(root))
		{
			if (Id(node) is not { Length: > 0 } id || !ids.Add(id)) { return false; }
			if (Type(node) == "block" ? node["content"] is not JsonObject : !IsContainer(node) || node["children"] is not JsonArray) { return false; }
		}
		return true;
	}

	// Compute relative pane areas. Rows and columns divide space by visible children's weights; missing weights and px sizes
	// count as 1 because the map has no window width for pixel conversion. Skip hidden nodes.
	// A stack shows its active child, or its first visible child if none is active.
	public static List<LayoutCell> Map(JsonObject root) => Map(root, null);

	// A pane's own row can highlight it even when its stack is inactive or its split is hidden on the Mac.
	public static List<LayoutCell> MapForPane(JsonObject root, string address)
	{
		List<LayoutCell> cells = Map(root);
		if (cells.Exists(cell => cell.Terminal == address)) { return cells; }
		HashSet<JsonObject> path = [];
		bool Find(JsonObject node)
		{
			if (Terminal(node) != address && !Children(node).Any(Find)) { return false; }
			path.Add(node);
			return true;
		}
		return Find(root) ? Map(root, path) : cells;
	}

	private static List<LayoutCell> Map(JsonObject root, HashSet<JsonObject>? reveal)
	{
		List<LayoutCell> cells = [];
		Place(root, 0, 0, 1, 1, cells, reveal);
		return cells;
	}

	private static void Place(JsonObject node, double x, double y, double width, double height, List<LayoutCell> cells, HashSet<JsonObject>? reveal)
	{
		if (Hidden(node) && reveal?.Contains(node) != true) { return; }
		List<JsonObject> shown = [.. Children(node).Where(child => !Hidden(child) || reveal?.Contains(child) == true)];
		switch (Type(node))
		{
			case "block":
				cells.Add(new LayoutCell(Id(node) ?? "", Terminal(node), x, y, width, height));
				return;
			case "stack":
				JsonObject? target = reveal is null ? null : shown.Find(reveal.Contains);
				if ((target ?? shown.Find(child => Id(child) == Text(node["active"])) ?? shown.FirstOrDefault()) is { } active)
				{
					Place(active, x, y, width, height, cells, reveal);
				}
				return;
			case "row" or "column":
				double total = shown.Sum(child => Weight(child["size"]) ?? 1);
				if (total <= 0) { return; }
				bool row = Type(node) == "row";
				double at = 0;
				foreach (JsonObject child in shown)
				{
					double share = (Weight(child["size"]) ?? 1) / total;
					if (row) { Place(child, x + at * width, y, share * width, height, cells, reveal); }
					else { Place(child, x, y + at * height, width, share * height, cells, reveal); }
					at += share;
				}
				return;
		}
	}

	// A compact layout key with node kinds, IDs, terminals and sizes in order, such as `row#tb1(p1=t5:0.5,p2=t6:0.5)`.
	// List rows and layout pills use it as part of their value keys, so only layout changes trigger their redraw.
	public static string Key(JsonObject root)
	{
		StringBuilder key = new();
		Append(root, key);
		return key.ToString();
	}

	private static void Append(JsonObject node, StringBuilder key)
	{
		if (Hidden(node)) { key.Append('!'); }
		if (Type(node) == "block") { key.Append(Id(node)).Append('=').Append(Terminal(node) ?? (Text(node["content"]?["mod"]) is { } mod ? "mod:" + mod : "?")); }
		else
		{
			key.Append(Type(node)).Append('#').Append(Id(node));
			if (Text(node["active"]) is { } active) { key.Append('*').Append(active); }
		}
		if (Weight(node["size"]) is { } weight) { key.Append(':').Append(weight.ToString("R", CultureInfo.InvariantCulture)); }
		else if (Px(node["size"]) is { } px) { key.Append(':').Append(px.ToString("R", CultureInfo.InvariantCulture)).Append("px"); }
		if (!IsContainer(node)) { return; }
		key.Append('(').AppendJoin(',', Children(node).Select(Key)).Append(')');
	}

	private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? text) ? text : null;

	private static bool Hidden(JsonNode? node) => node?["hidden"] is JsonValue v && v.TryGetValue(out bool hidden) && hidden;

	private static int IndexOf(JsonArray children, string id)
	{
		for (int i = 0; i < children.Count; i++)
		{
			if (Id(children[i]) == id) { return i; }
		}
		return -1;
	}

	private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? d : null;

	// A size's weight; null when it has none (missing, or px).
	private static double? Weight(JsonNode? size) => Number(size?["weight"]);

	private static double? Px(JsonNode? size) => Number(size?["px"]);

	// herdr-app's `weight` in remove: a missing size counts as 1, a px size as none.
	private static double? WeightOrOne(JsonNode? size) => size is null ? 1 : Weight(size);

	private static JsonObject WeightSize(double weight) => new() { ["weight"] = weight };
}
