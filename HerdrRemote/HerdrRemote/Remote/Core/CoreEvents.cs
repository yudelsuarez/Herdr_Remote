using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HerdrRemote.Remote;

// An event already applied to Terminals. Type is created, lost, restarted, keep, finished, removed, status, turn, title, labels,
// size, foreground or doc; "missed" means a fresh snapshot replaced all records.
internal sealed record CoreEvent(string Type, string? Terminal, JsonNode? Data);

// Maintain terminal records from the subscription snapshot and event lines, following herdr-core's Events API.
// Events at or below the snapshot's seq are already included. Not thread-safe: use a single reader.
internal sealed class CoreEvents
{
	// Event lines may span frames; retain the incomplete tail for the next frame.
	private readonly ArrayBufferWriter<byte> _partial = new(4096);

	public CoreEvents(JsonObject snapshot)
	{
		Seq = snapshot["seq"]?.GetValue<long>() ?? 0;
		Replace(snapshot["terminals"] as JsonArray);
	}

	public long Seq { get; private set; }

	// Last received frame time (Environment.TickCount64), including heartbeats, for the session watchdog.
	public static long LastFrame { get; private set; }

	// Records keyed by address: `t7` locally, or `machine/t7` for another machine.
	public Dictionary<string, JsonObject> Terminals { get; } = [];

	public event Action<CoreEvent>? Changed;

	// Consume until Closed or Lost, then return the ending so callers can resubscribe for a fresh snapshot.
	// Apply each frame and raise Changed on RunAsync's starting context (the UI thread in this app); Terminals needs no lock.
	public async Task<CoreFrame> RunAsync(CoreStream stream, CancellationToken ct)
	{
		await foreach (CoreFrame frame in stream.Frames.ReadAllAsync())
		{
			try
			{
				LastFrame = Environment.TickCount64;
				if (frame.Type != CoreFrameType.Data) { return frame; }
				if (ct.IsCancellationRequested) { continue; }
				ReadOnlySpan<byte> rest = frame.Bytes;
				for (int end = rest.IndexOf((byte)'\n'); end >= 0; end = rest.IndexOf((byte)'\n'))
				{
					if (_partial.WrittenCount == 0) { Apply(rest[..end]); }
					else
					{
						_partial.Write(rest[..end]);
						Apply(_partial.WrittenSpan);
						_partial.ResetWrittenCount();
					}
					rest = rest[(end + 1)..];
				}
				_partial.Write(rest);
				stream.Credit(frame.Length);
			}
			finally
			{
				CoreStream.Recycle(frame);
			}
		}
		return new CoreFrame(CoreFrameType.Lost, null, 0, null);
	}

	private void Apply(ReadOnlySpan<byte> line)
	{
		JsonObject? message;
		try
		{
			message = JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException ex)
		{
			Debug.WriteLine($"[Core] unreadable event: {ex.Message}");
			return;
		}
		if (message?["params"] is not JsonObject p) { return; }
		long seq = p["seq"]?.GetValue<long>() ?? Seq;
		switch (message["method"]?.GetValue<string>())
		{
			case "missed":
				Seq = seq;
				Replace(p["terminals"] as JsonArray);
				Changed?.Invoke(new CoreEvent("missed", null, null));
				return;
			case "event" when seq > Seq:
				Seq = seq;
				break;
			default:
				// Ignore heartbeats (every 15 s with the current seq) and events already included in the snapshot.
				return;
		}
		string type = p["type"]?.GetValue<string>() ?? "";
		string? address = p["terminal"]?.GetValue<string>();
		JsonNode? data = p["data"];
		JsonObject? fields = data as JsonObject;
		JsonObject? record = null;
		if (address is not null) { Terminals.TryGetValue(address, out record); }
		switch (type)
		{
			case "created" or "restarted" when address is not null && fields is not null:
				Terminals[address] = (JsonObject)fields.DeepClone();
				break;
			case "removed" when address is not null:
				Terminals.Remove(address);
				break;
			// A terminal newly matching the subscription filter arrives as a complete record.
			case "labels" when address is not null && (fields?["record"] is JsonObject added):
				Terminals[address] = (JsonObject)added.DeepClone();
				break;
			// The current foreground process and its directory, or null.
			case "foreground" when record is not null:
				record["foreground"] = data?.DeepClone();
				break;
			case "lost" or "finished" when record is not null && fields is not null:
				Copy(fields, record, "reason", "exit_code", "signal", "finished_ms");
				record.Remove("foreground");
				if (type == "lost") { record["pid"] = null; }
				else { record["state"] = "finished"; }
				break;
			case "status" when record is not null && fields is not null:
				// The core ends the turn before publishing status, then sends the counter in a separate event.
				if (StatusWord(record["status"]) == "working" && StatusWord(fields["status"]) != "working")
				{
					record["turn"] = (record["turn"]?.GetValue<long>() ?? 0) + 1;
				}
				Copy(fields, record, "status", "records", "reported");
				break;
			case "size" when record is not null && fields is not null:
				Copy(fields, record, "cols", "rows");
				break;
			case "keep" or "turn" or "title" or "labels" when record is not null && fields is not null:
				Copy(fields, record, type);
				break;
		}
		Changed?.Invoke(new CoreEvent(type, address, data));
	}

	// The current root status can be an object or a bare word.
	internal static string? StatusWord(JsonNode? value) => value switch
	{
		JsonObject status => status["state"] is JsonValue state && state.TryGetValue(out string? word) ? word : null,
		JsonValue bare when bare.TryGetValue(out string? word) => word,
		_ => null,
	};

	private static void Copy(JsonObject from, JsonObject to, params ReadOnlySpan<string> keys)
	{
		foreach (string key in keys) { to[key] = from[key]?.DeepClone(); }
	}

	private void Replace(JsonArray? terminals)
	{
		Terminals.Clear();
		foreach (JsonObject t in terminals?.OfType<JsonObject>() ?? [])
		{
			if (t["address"]?.GetValue<string>() is { } address) { Terminals[address] = t; }
		}
	}
}
