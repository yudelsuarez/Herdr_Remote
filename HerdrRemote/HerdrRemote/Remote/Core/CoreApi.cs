using System.Text.Json.Nodes;

namespace HerdrRemote.Remote;

// terminal.read output: screen and requested history as text. How is "screen", "history" or "stitched" (a full-screen app read
// by scrolling). Complete is false if content may be missing. Why explains screen-only output (working, blocked or error).
internal sealed record CoreRead(string Text, string How, bool Complete, string? Why);

// An open terminal stream and the terminal's record at attach time.
internal sealed record CoreAttach(CoreStream Stream, JsonObject Terminal);

// Typed access to the app's herdr-core JSON-RPC calls; method and parameter names follow `hc schema`.
// Keep terminal records as JSON (address, name, argv, cwd, state, cols, rows, title, status, turn, labels, etc.).
internal sealed class CoreApi(CoreConnection connection)
{
	// Read a live or finished terminal's screen plus `lines` of preceding history.
	public async Task<CoreRead> ReadAsync(string terminal, int lines, CancellationToken ct)
	{
		JsonNode? r = await connection.CallAsync("terminal.read", new JsonObject { ["terminal"] = terminal, ["lines"] = lines }, ct).ConfigureAwait(false);
		return new CoreRead(r?["text"]?.GetValue<string>() ?? "", r?["how"]?.GetValue<string>() ?? "", r?["complete"]?.GetValue<bool>() ?? false, r?["why"]?.GetValue<string>());
	}

	// Set a free-form label; an empty value removes it. The core does not interpret label text.
	public Task<JsonNode?> LabelAsync(string terminal, string key, string value, CancellationToken ct) =>
		connection.CallAsync("terminal.label", new JsonObject { ["terminal"] = terminal, ["labels"] = new JsonObject { [key] = value } }, ct);

	// Subscribe to a snapshot of all terminals, including added machines, and their subsequent changes. CoreEvents consumes the stream.
	public async Task<(CoreStream Stream, JsonObject Snapshot)> SubscribeAsync(CancellationToken ct)
	{
		CoreConnection.Reply reply = await connection.CallAsync("events.subscribe", [], true, CoreConnection.CallTimeout, ct).ConfigureAwait(false);
		if (reply.Stream is not { } stream || reply.Result is not JsonObject snapshot) { throw new InvalidDataException("events.subscribe returned no channel."); }
		return (stream, snapshot);
	}

	// Attach as controller at the view's grid, with observe and resize_notifications left false.
	// history is the initial scrollback count; display supplies colors for the core's responses to program queries.
	// Never resume an attach: apply the view's grid immediately (see TerminalStream.Reopen).
	public async Task<CoreAttach> AttachAsync(string terminal, int cols, int rows, int history, JsonObject display, CancellationToken ct)
	{
		JsonObject parameters = new()
		{
			["terminal"] = terminal,
			["cols"] = cols,
			["rows"] = rows,
			["history"] = history,
			// Clone because JSON nodes have a single parent and these colors are reused on later attaches.
			["display"] = display.DeepClone(),
		};
		CoreConnection.Reply reply = await connection.CallAsync("terminal.attach", parameters, true, CoreConnection.CallTimeout, ct).ConfigureAwait(false);
		if (reply.Stream is not { } stream || reply.Result is not JsonObject result) { throw new InvalidDataException("terminal.attach returned no channel."); }
		stream.AutomaticInput = result["automatic_input"]?.GetValue<bool>() == true
			&& connection.Hello["features"] is JsonArray features && features.Any(f => f?.GetValue<string>() == "automatic_input");
		return new CoreAttach(stream, result["terminal"] as JsonObject ?? []);
	}

	// Read an opaque core document, or null if unwritten. herdr-app stores each machine's spaces and tabs in "herdr-app".
	public async Task<JsonNode?> DocGetAsync(string name, CancellationToken ct) => (await DocReadAsync(name, ct).ConfigureAwait(false)).Value;

	// Read a document with its version (0 if unwritten) for a compare-and-set write through DocSetAsync.
	public async Task<CoreDoc> DocReadAsync(string name, CancellationToken ct)
	{
		JsonNode? r = await connection.CallAsync("doc.get", new JsonObject { ["name"] = name }, ct).ConfigureAwait(false);
		return new CoreDoc(r?["value"]?.DeepClone(), r?["version"]?.GetValue<ulong>() ?? 0);
	}

	// Write only if the version still matches, returning the new version. CoreRpcException Kind "conflict" requires a fresh read.
	public async Task<ulong> DocSetAsync(string name, ulong version, JsonNode? value, CancellationToken ct)
	{
		JsonObject parameters = new() { ["name"] = name, ["version"] = version, ["value"] = value?.DeepClone() };
		JsonNode? r = await connection.CallAsync("doc.set", parameters, ct).ConfigureAwait(false);
		return r?["version"]?.GetValue<ulong>() ?? 0;
	}

	// Start a terminal for herdr-app to adopt. Space and tab labels place it; unknown IDs create new spaces or tabs.
	// herdr.seen "0" means no turn has been seen, and keep replaces a lost program with a shell, matching herdr-app.
	// An empty argv uses the Mac's shell. size sets the initial grid, or null uses the core default until a viewer attaches.
	// display supplies the same colors Pane sends on attach. Return the address; launch failures throw CoreRpcException Kind "spawn_failed".
	public async Task<string> RunAsync(IReadOnlyList<string> argv, string? cwd, string spaceId, string tabId, (int Cols, int Rows)? size, JsonObject? display, CancellationToken ct)
	{
		JsonObject parameters = new()
		{
			["argv"] = new JsonArray([.. argv.Select(arg => (JsonNode?)JsonValue.Create(arg))]),
			["cwd"] = cwd,
			["keep"] = true,
			["env"] = new JsonObject { ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor", ["TERM_PROGRAM"] = "herdr" },
			["labels"] = new JsonObject { ["herdr.space"] = spaceId, ["herdr.tab"] = tabId, ["herdr.seen"] = "0" },
		};
		if (size is { } grid) { parameters["size"] = new JsonObject { ["cols"] = grid.Cols, ["rows"] = grid.Rows }; }
		if (display is not null) { parameters["display"] = display.DeepClone(); }
		JsonNode? r = await connection.CallAsync("terminal.run", parameters, ct).ConfigureAwait(false);
		return r?["address"]?.GetValue<string>() ?? throw new InvalidDataException("terminal.run returned no address.");
	}

	// End the terminal's program using hangup, terminate and kill. Treat "not_found" as already complete.
	public async Task KillAsync(string terminal, CancellationToken ct)
	{
		try
		{
			await connection.CallAsync("terminal.kill", new JsonObject { ["terminal"] = terminal }, ct).ConfigureAwait(false);
		}
		catch (CoreRpcException ex) when (ex.Kind == "not_found")
		{
		}
	}

	// Read a live or finished terminal record, including reason, exit_code and timestamps. Unknown addresses throw "not_found".
	public async Task<JsonObject> GetAsync(string terminal, CancellationToken ct) =>
		await connection.CallAsync("terminal.get", new JsonObject { ["terminal"] = terminal }, ct).ConfigureAwait(false) as JsonObject
			?? throw new InvalidDataException("terminal.get returned no terminal.");

	// Run argv directly on the Mac without a shell or terminal. After `seconds`, end it and its descendants.
	// Nonzero exits and timeouts are returned as results. The RPC deadline adds the usual call timeout to the program deadline.
	public async Task<CoreExec> ExecAsync(IReadOnlyList<string> argv, string? cwd, int seconds, CancellationToken ct)
	{
		JsonObject parameters = new()
		{
			["argv"] = new JsonArray([.. argv.Select(arg => (JsonNode?)JsonValue.Create(arg))]),
			["timeout"] = seconds,
		};
		if (cwd is not null) { parameters["cwd"] = cwd; }
		JsonNode? r = await connection.CallAsync("machine.exec", parameters, ct, TimeSpan.FromSeconds(seconds) + CoreConnection.CallTimeout).ConfigureAwait(false);
		return new CoreExec(
			r?["code"]?.GetValue<int>(),
			Convert.FromBase64String(r?["stdout"]?.GetValue<string>() ?? ""),
			Convert.FromBase64String(r?["stderr"]?.GetValue<string>() ?? ""),
			r?["timed_out"]?.GetValue<bool>() ?? false);
	}
}

// Core document value (null if unwritten or deleted) and version (0 if never written).
internal sealed record CoreDoc(JsonNode? Value, ulong Version);

// machine.exec result: exit code (null for signal termination), stdout, stderr and timeout status.
internal sealed record CoreExec(int? Code, byte[] Stdout, byte[] Stderr, bool TimedOut)
{
	public string Out => System.Text.Encoding.UTF8.GetString(Stdout);

	public string Error => System.Text.Encoding.UTF8.GetString(Stderr);
}
