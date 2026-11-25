using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Renci.SshNet;

namespace HerdrRemote.Remote;

// herdr-core frame kinds, defined by its Framed mode API.
internal enum CoreFrameKind : byte
{
	Data = 0,
	Credit = 1,
	Close = 2,
	Resize = 3,
	Focus = 4,
	Facts = 5,
	AutomaticInput = 6,
}

// One herdr-core connection over `hc connect`. Begin with line-delimited JSON-RPC; hello with framed=true switches to frames
// after its reply: [channel u32 LE][kind u8][len u32 LE][payload]. Channel 0 carries JSON-RPC in both directions;
// terminal and event streams use dedicated channels. Stream readers return pooled payloads after copying them (CoreStream.Recycle).
// No MAUI dependency: callers dispatch UI work themselves.
internal sealed class CoreConnection : IAsyncDisposable
{
	// The protocol generation this client speaks (hello's `generation`).
	public const int Generation = 1;
	private const int HeaderLength = 9;
	// The documented payload limit.
	private const int MaxPayload = 16 * 1024 * 1024;
	internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
	private const int MaxAbandoned = 32;

	// Required methods, checked against the core's hello response.
	private static readonly string[] Needed = ["terminal.read", "terminal.label", "terminal.attach", "events.subscribe", "doc.get", "terminal.run", "terminal.kill", "terminal.get", "doc.set", "machine.exec"];

	private readonly Stream _input;
	private readonly Stream _output;
	private readonly IDisposable? _owner;
	// A single writer drains queued frames so a core that stops reading cannot block UI callers.
	private readonly Channel<(byte[] Buffer, int Length)> _outgoing = Channel.CreateUnbounded<(byte[] Buffer, int Length)>(new UnboundedChannelOptions { SingleReader = true });
	private readonly Lock _gate = new();
	private readonly Dictionary<long, Waiter> _waiting = [];
	private readonly Dictionary<uint, CoreStream> _streams = [];
	private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private long _lastId;
	private bool _closed;
	private int _abandoned;
	private bool _capClosing;
	private Task _reader = Task.CompletedTask;

	// Pending RPC. Register stream-opening replies immediately so subsequent frames have a destination.
	private enum WaiterOwner { Open, Reader, Abandoned }
	private sealed record Waiter(string Method, TaskCompletionSource<Reply> Done, bool OpensStream)
	{
		public WaiterOwner Owner;
	}

	internal readonly record struct Reply(JsonNode? Result, CoreStream? Stream);

	private CoreConnection(Stream input, Stream output, IDisposable? owner)
	{
		_input = input;
		_output = output;
		_owner = owner;
	}

	// hello result: generation, version, methods and features.
	public JsonObject Hello { get; private set; } = [];

	public string Version => Hello["version"]?.GetValue<string>() ?? "";

	// Completes when the core channel closes through SSH loss, core shutdown or Dispose. Programs keep running in the core.
	public Task Lost => _lost.Task;

	// Open one additional SSH exec channel for `hc connect`, without a PTY, to carry all core traffic.
	public static async Task<CoreConnection> OpenAsync(SshClient client, SshShell shell, CancellationToken ct)
	{
		SshCommand command = client.CreateCommand(SshRemoteCommands.CoreConnect(shell));
		Task run = command.ExecuteAsync(CancellationToken.None);
		CoreConnection connection = new(command.CreateInputStream(), command.OutputStream, command);
		try
		{
			await connection.HelloAsync(ct).ConfigureAwait(false);
			return connection;
		}
		catch (IOException ex)
		{
			// The command ended before hello. Use stderr and the exit code to explain why.
			await run.WaitAsync(ct).ConfigureAwait(false);
			string error = command.Error.Trim();
			bool missing = command.ExitStatus == 127;
			await connection.DisposeAsync().ConfigureAwait(false);
			throw new CoreConnectException(error.Length > 0 ? error : ex.Message, missing, ex);
		}
		catch
		{
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	// Exchange hello in plain mode. Read its reply one byte at a time to avoid consuming bytes from the first framed message.
	private async Task HelloAsync(CancellationToken ct)
	{
		JsonObject hello = new()
		{
			["jsonrpc"] = "2.0",
			["id"] = 0,
			["method"] = "hello",
			["params"] = new JsonObject { ["framed"] = true, ["generation"] = Generation },
		};
		await _input.WriteAsync(Encoding.UTF8.GetBytes(hello.ToJsonString() + "\n"), ct).ConfigureAwait(false);
		await _input.FlushAsync(ct).ConfigureAwait(false);
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(CallTimeout);
		ArrayBufferWriter<byte> line = new(1024);
		byte[] one = new byte[1];
		while (await _output.ReadAsync(one, deadline.Token).ConfigureAwait(false) == 1 && one[0] != (byte)'\n') { line.Write(one); }
		if (one[0] != (byte)'\n') { throw new IOException("The core closed the line before answering hello."); }
		JsonNode? reply = JsonNode.Parse(line.WrittenSpan);
		if (reply?["error"] is JsonObject error) { throw CoreRpcException.From("hello", error); }
		Hello = reply?["result"] as JsonObject ?? throw new InvalidDataException("The core's hello reply has no result.");
		int generation = Hello["generation"]?.GetValue<int>() ?? 0;
		if (generation != Generation) { throw new CoreMismatchException($"The core speaks protocol generation {generation}; this app speaks {Generation}."); }
		HashSet<string?> methods = Hello["methods"] is JsonArray list ? [.. list.Select(m => m?.GetValue<string>())] : [];
		string[] absent = [.. Needed.Where(name => !methods.Contains(name))];
		if (absent.Length > 0) { throw new CoreMismatchException($"The core {Version} has no {string.Join(", ", absent)}."); }
		// SSH.NET PipeStream blocks: one dedicated reader avoids async-over-sync work for every frame.
		_reader = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		_ = Task.Run(WriteAsync, CancellationToken.None);
	}

	// Concurrent JSON-RPC calls on channel 0. Throws CoreRpcException with the core's kind and hint,
	// CoreLostException on connection loss, or TimeoutException after the deadline.
	public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken ct, TimeSpan? timeout = null,
		JsonSerializerOptions? json = null) =>
		(await CallAsync(method, parameters, false, timeout ?? CallTimeout, ct, json).ConfigureAwait(false)).Result;

	// Stream-opening RPC (terminal.attach or events.subscribe). The reply precedes the stream's first frame.
	// Register the stream while reading that reply so no subsequent frame is unrouted.
	internal async Task<Reply> CallAsync(string method, JsonObject? parameters, bool opensStream, TimeSpan timeout, CancellationToken ct,
		JsonSerializerOptions? json = null)
	{
		long id = Interlocked.Increment(ref _lastId);
		Waiter waiter = new(method, new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously), opensStream);
		lock (_gate)
		{
			if (_closed) { throw new CoreLostException(method); }
			_waiting[id] = waiter;
		}
		JsonObject request = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
		Send(0, CoreFrameKind.Data, Encoding.UTF8.GetBytes(request.ToJsonString(json)));
		// Log only the method name, never terminal output or user input.
		Debug.WriteLine($"[Core] {method}");
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(timeout);
		try
		{
			return await waiter.Done.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			if (opensStream)
			{
				bool hit = false, readerOwned;
				lock (_gate)
				{
					if (!_closed && waiter.Owner == WaiterOwner.Open && _waiting.ContainsKey(id))
					{
						waiter.Owner = WaiterOwner.Abandoned;
						if (++_abandoned >= MaxAbandoned && !_capClosing) { _capClosing = true; hit = true; }
					}
					readerOwned = waiter.Owner == WaiterOwner.Reader;
				}
				if (hit) { Close(); }
				if (readerOwned) { return await waiter.Done.Task.ConfigureAwait(false); }
			}
			if (!ct.IsCancellationRequested) { throw new TimeoutException($"{method}: no reply from the core."); }
			throw;
		}
		finally
		{
			if (!opensStream) { lock (_gate) { _waiting.Remove(id); } }
		}
	}

	// Queue a frame in a pooled buffer; the writer returns the buffer after sending.
	internal bool Send(uint channel, CoreFrameKind kind, ReadOnlySpan<byte> payload)
	{
		int length = HeaderLength + payload.Length;
		byte[] frame = ArrayPool<byte>.Shared.Rent(length);
		BinaryPrimitives.WriteUInt32LittleEndian(frame, channel);
		frame[4] = (byte)kind;
		BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), (uint)payload.Length);
		payload.CopyTo(frame.AsSpan(HeaderLength));
		if (_outgoing.Writer.TryWrite((frame, length))) { return true; }
		ArrayPool<byte>.Shared.Return(frame);
		return false;
	}

	private async Task WriteAsync()
	{
		try
		{
			// Drain queued frames before flushing so keys typed together travel in one SSH packet.
			while (await _outgoing.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (_outgoing.Reader.TryRead(out (byte[] Buffer, int Length) frame))
				{
					try
					{
						await _input.WriteAsync(frame.Buffer.AsMemory(0, frame.Length)).ConfigureAwait(false);
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(frame.Buffer);
					}
				}
				await _input.FlushAsync().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Core] write failed: {ex.GetType().Name}: {ex.Message}");
			Close();
			while (await _outgoing.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (_outgoing.Reader.TryRead(out var frame)) { ArrayPool<byte>.Shared.Return(frame.Buffer); }
			}
		}
	}

	// Read frames until the connection ends. Return pooled buffers for replies, Close and Resize here.
	// Transfer Data buffers to the stream reader, which returns them through CoreStream.Recycle.
	private void Read()
	{
		byte[] header = new byte[HeaderLength];
		try
		{
			while (Fill(_output, header, HeaderLength))
			{
				uint channel = BinaryPrimitives.ReadUInt32LittleEndian(header);
				CoreFrameKind kind = (CoreFrameKind)header[4];
				int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5)), MaxPayload + 1u);
				if (length > MaxPayload) { throw new InvalidDataException("A frame is over the 16 MiB limit."); }
				byte[]? payload = ArrayPool<byte>.Shared.Rent(length);
				try
				{
					if (!Fill(_output, payload, length))
					{
						break;
					}
					if (channel == 0)
					{
						// Channel 0 carries replies only; framed-mode events use their subscription channel.
						JsonNode? message = JsonNode.Parse(payload.AsSpan(0, length));
						Waiter? waiter = null;
						bool abandoned = false;
						if (message?["id"] is JsonValue idValue && idValue.TryGetValue(out long id))
						{
							lock (_gate)
							{
								if (_waiting.Remove(id, out waiter))
								{
									abandoned = waiter.Owner == WaiterOwner.Abandoned;
									if (abandoned) { _abandoned--; }
									else { waiter.Owner = WaiterOwner.Reader; }
								}
							}
						}
						if (waiter is null) { continue; }
						if (abandoned)
						{
							if (message!["error"] is null && message["result"]?["channel"] is JsonValue lateChannel && lateChannel.TryGetValue(out uint late)) { Send(late, CoreFrameKind.Close, []); }
							continue;
						}
						try
						{
							if (message!["error"] is JsonObject error)
							{
								waiter.Done.TrySetException(CoreRpcException.From(waiter.Method, error));
								continue;
							}
							JsonNode? result = message["result"];
							CoreStream? opened = null;
							if (waiter.OpensStream && result?["channel"] is JsonValue channelValue && channelValue.TryGetValue(out uint streamChannel))
							{
								opened = new CoreStream(this, streamChannel);
								bool registered = false;
								lock (_gate) { if (!_closed) { _streams[streamChannel] = opened; registered = true; } }
								if (!registered)
								{
									waiter.Done.TrySetException(new CoreLostException(waiter.Method));
									continue;
								}
							}
							waiter.Done.TrySetResult(new Reply(result, opened));
						}
						catch (Exception ex)
						{
							// Close cannot fail a waiter already removed from _waiting by this reader.
							waiter.Done.TrySetException(ex);
							throw;
						}
						continue;
					}
					CoreStream? stream;
					lock (_gate) { _streams.TryGetValue(channel, out stream); }
					if (stream is not null && kind == CoreFrameKind.Data)
					{
						if (stream.Incoming.TryWrite(new CoreFrame(CoreFrameType.Data, payload, length, null))) { payload = null; }
						continue;
					}
					if (stream is not null && kind == CoreFrameKind.Close)
					{
						lock (_gate) { _streams.Remove(channel); }
						stream.End(CoreFrameType.Closed, length == 0 ? null : JsonNode.Parse(payload.AsSpan(0, length)));
					}
				}
				finally
				{
					if (payload is not null) { ArrayPool<byte>.Shared.Return(payload); }
				}
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Core] read failed: {ex.GetType().Name}: {ex.Message}");
		}
		Close();
	}

	private static bool Fill(Stream output, byte[] buffer, int length)
	{
		int filled = 0;
		while (filled < length)
		{
			int n = output.Read(buffer, filled, length - filled);
			if (n == 0) { return false; }
			filled += n;
		}
		return true;
	}

	// On connection loss, fail pending calls and end all streams with Lost.
	private void Close()
	{
		Waiter[] waiting;
		CoreStream[] streams;
		lock (_gate)
		{
			if (_closed) { return; }
			_closed = true;
			waiting = [.. _waiting.Values];
			_waiting.Clear();
			_abandoned = 0;
			streams = [.. _streams.Values];
			_streams.Clear();
		}
		foreach (Waiter w in waiting) { w.Done.TrySetException(new CoreLostException(w.Method)); }
		foreach (CoreStream s in streams) { s.End(CoreFrameType.Lost, null); }
		try
		{
			_input.Dispose();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Core] closing stdin: {ex.GetType().Name}: {ex.Message}");
		}
		// Let SshCommand detach its producer before stdout closes, even if channel cleanup fails.
		try
		{
			_owner?.Dispose();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Core] closing owner: {ex.GetType().Name}: {ex.Message}");
		}
		try
		{
			_output.Dispose();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Core] closing stdout: {ex.GetType().Name}: {ex.Message}");
		}
		finally
		{
			_outgoing.Writer.TryComplete();
			_lost.TrySetResult();
		}
	}

	public async ValueTask DisposeAsync()
	{
		Close();
		await _reader.ConfigureAwait(false);
	}
}

// JSON-RPC error: Kind is the stable category (not_found, finished, unavailable, conflict, etc.) for caller decisions.
// The exception message includes method, core message and hint for logging; CoreMessage contains only the message for display.
internal sealed class CoreRpcException(string method, string message, string? kind, string? hint)
	: Exception($"{method}: {message}{(string.IsNullOrEmpty(hint) ? "" : $" ({hint})")}")
{
	public string? Kind { get; } = kind;

	public string CoreMessage { get; } = message;

	internal static CoreRpcException From(string method, JsonObject error) => new(
		method,
		error["message"]?.GetValue<string>() ?? "core call failed",
		error["data"]?["kind"]?.GetValue<string>(),
		error["data"]?["hint"]?.GetValue<string>());
}

// The connection dropped before or during an RPC.
internal sealed class CoreLostException(string method) : Exception($"{method}: lost the core.");

// The core has an incompatible protocol generation or lacks a required method.
internal sealed class CoreMismatchException(string message) : Exception(message);

// `hc connect` could not start. NotInstalled means hc was absent from all locations tried.
internal sealed class CoreConnectException(string message, bool notInstalled, Exception inner) : Exception(message, inner)
{
	public bool NotInstalled { get; } = notInstalled;
}
