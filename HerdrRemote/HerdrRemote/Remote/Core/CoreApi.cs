using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
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
	internal const string AttachmentTooLarge = "Attachments must be 8 MB or smaller.";
	internal const string AttachmentsUnavailable = "This version of Herdr does not support attachments.";
	private const int PutPiece = 128 * 1024;
	// This JSON goes to a framed RPC, not HTML. Keeping Base64 '+' literal keeps legacy puts below the 16 MiB limit.
	private static readonly JsonSerializerOptions PutJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
	// With empty argv, Herdr chooses its shell. size sets the initial grid, or null uses the core default.
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

	// Herdr owns attachment placement and cleanup on the selected terminal's machine.
	public async Task<string> PutAsync(string terminal, Stream source, string name, CancellationToken ct)
	{
		const int limit = 8 * 1024 * 1024;
		bool Has(string name) => connection.Hello["methods"] is JsonArray methods
			&& methods.Any(method => method?.GetValue<string>() == name);
		bool chunked = Has("terminal.put_begin") && Has("terminal.put_chunk") && Has("terminal.put_finish") && Has("terminal.put_abort");
		if (!chunked && !Has("terminal.put"))
		{
			throw new InvalidDataException(AttachmentsUnavailable);
		}
		if (source.CanSeek && source.Length - source.Position > limit) { throw new InvalidDataException(AttachmentTooLarge); }
		using MemoryStream data = new();
		byte[] block = new byte[81920];
		int read;
		while ((read = await source.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, limit + 1 - data.Length)), ct).ConfigureAwait(false)) > 0)
		{
			if (data.Length + read > limit) { throw new InvalidDataException(AttachmentTooLarge); }
			data.Write(block, 0, read);
		}
		ct.ThrowIfCancellationRequested();
		int size = (int)data.Length;
		JsonNode? result = chunked
			? await PutChunksAsync(terminal, data.GetBuffer(), size, name, ct).ConfigureAwait(false)
			: await PutLegacyAsync(terminal, data.GetBuffer(), size, name, ct).ConfigureAwait(false);
		if (result?["path"]?.GetValue<string>() is not { Length: > 0 } path || path.Any(char.IsControl)
			|| result["bytes"]?.GetValue<long>() != size)
		{
			throw new InvalidDataException("Herdr returned an invalid attachment.");
		}
		return path;
	}

	private Task<JsonNode?> PutLegacyAsync(string terminal, byte[] data, int size, string name, CancellationToken ct) =>
		// Compatibility with cores before chunked puts (herdr-core v0.1.21): one frame, sized under the frame limit, with a
		// deadline scaled to its size. Such a core cannot interleave other traffic with it or abort it once queued.
		connection.CallAsync("terminal.put", new JsonObject
		{
			["terminal"] = terminal,
			["name"] = name,
			["data"] = Convert.ToBase64String(data, 0, size),
		}, ct, CoreConnection.CallTimeout + TimeSpan.FromSeconds(4 * ((size + PutPiece - 1) / PutPiece)), PutJson);

	private async Task<JsonNode?> PutChunksAsync(string terminal, byte[] data, int size, string name, CancellationToken ct)
	{
		string transfer = Guid.NewGuid().ToString("N");
		try
		{
			JsonNode? begin = await connection.CallAsync("terminal.put_begin", new JsonObject
			{
				["terminal"] = terminal, ["name"] = name, ["bytes"] = size, ["transfer"] = transfer,
			}, ct).ConfigureAwait(false);
			if (begin?["transfer"]?.GetValue<string>() != transfer) { throw new InvalidDataException("Herdr did not begin the attachment transfer."); }
			for (int offset = 0; offset < size;)
			{
				ct.ThrowIfCancellationRequested();
				int count = Math.Min(PutPiece, size - offset);
				JsonNode? piece = await connection.CallAsync("terminal.put_chunk", new JsonObject
				{
					["transfer"] = transfer, ["offset"] = offset, ["data"] = Convert.ToBase64String(data, offset, count),
				}, ct, json: PutJson).ConfigureAwait(false);
				offset += count;
				if (piece?["bytes"]?.GetValue<int>() != offset) { throw new InvalidDataException("Herdr did not acknowledge the attachment piece."); }
			}
			ct.ThrowIfCancellationRequested();
			return await connection.CallAsync("terminal.put_finish", new JsonObject { ["transfer"] = transfer }, ct).ConfigureAwait(false);
		}
		catch
		{
			// The connection owns the transfer; abort also covers a begin whose reply was lost or canceled.
			try
			{
				await connection.CallAsync("terminal.put_abort", new JsonObject { ["transfer"] = transfer }, CancellationToken.None)
					.ConfigureAwait(false);
			}
			catch (Exception ex) { Debug.WriteLine($"[Core] attachment abort: {ex.GetType().Name}"); }
			throw;
		}
	}

	// Run argv directly on the core's machine without a shell or terminal.
	// Nonzero exits and timeouts are returned as results. The RPC deadline adds the usual call timeout to the program deadline.
	public async Task<CoreExec> ExecAsync(IReadOnlyList<string> argv, string? cwd, int seconds, CancellationToken ct, byte[]? input = null)
	{
		JsonObject parameters = new()
		{
			["argv"] = new JsonArray([.. argv.Select(arg => (JsonNode?)JsonValue.Create(arg))]),
			["timeout"] = seconds,
		};
		if (cwd is not null) { parameters["cwd"] = cwd; }
		if (input is not null) { parameters["input"] = Convert.ToBase64String(input); }
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
