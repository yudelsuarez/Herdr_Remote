using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace HerdrRemote.Remote;

internal enum CoreFrameType
{
	// Pooled stream bytes. Recycle after copying (CoreStream.Recycle); return credit after consumption.
	Data,
	// The core closed the stream. Reason is its JSON ({"reason":"exited","exit_code":0,"signal":null}), or null after local Close.
	Closed,
	// The core connection dropped; the program likely still runs. Reattach after reconnecting.
	Lost,
}

internal readonly record struct CoreFrame(CoreFrameType Type, byte[]? Buffer, int Length, JsonNode? Reason)
{
	public ReadOnlySpan<byte> Bytes => Buffer.AsSpan(0, Length);
}

// One CoreConnection stream channel, with frames delivered in order through Frames. Start with 256 KiB of credit;
// Credit returns consumed bytes. Viewers over 4 MiB behind receive a fresh repaint instead of the backlog.
// Sends queue on the connection and never block.
internal sealed class CoreStream
{
	private readonly CoreConnection _connection;
	private readonly Channel<CoreFrame> _frames = System.Threading.Channels.Channel.CreateUnbounded<CoreFrame>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
	private int _ended;
	private int _closed;

	internal CoreStream(CoreConnection connection, uint channel)
	{
		_connection = connection;
		Channel = channel;
	}

	public uint Channel { get; }

	// CoreApi.AttachAsync enables this only if hello features and the attach response both confirm AUTOMATIC_INPUT.
	public bool AutomaticInput { get; internal set; }

	public ChannelReader<CoreFrame> Frames => _frames.Reader;

	// Return a Data frame's buffer after parsing or encoding its bytes for the view. Call exactly once per Data frame.
	public static void Recycle(CoreFrame frame)
	{
		if (frame.Buffer is not null) { ArrayPool<byte>.Shared.Return(frame.Buffer); }
	}

	// Return credit for bytes consumed by the parser or written by the view so the core can send more.
	public void Credit(int bytes)
	{
		Span<byte> credit = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(credit, (uint)bytes);
		_connection.Send(Channel, CoreFrameKind.Credit, credit);
	}

	// Forward keys, pastes and mouse reports already encoded by the terminal view; the core does not encode stream input.
	public bool Input(ReadOnlySpan<byte> bytes) => !bytes.IsEmpty && Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _ended) == 0
		&& _connection.Send(Channel, CoreFrameKind.Data, bytes);

	// Send automatic terminal replies (query responses and kitty graphics acknowledgements) without claiming viewer size
	// or resetting the core's quiet period. Fall back to Data if AUTOMATIC_INPUT was not confirmed.
	public void ReplyInput(ReadOnlySpan<byte> bytes)
	{
		if (!bytes.IsEmpty) { _connection.Send(Channel, AutomaticInput ? CoreFrameKind.AutomaticInput : CoreFrameKind.Data, bytes); }
	}

	// Update this viewer's grid. The last viewer to attach, type, resize or focus controls the terminal size.
	public void Resize(int cols, int rows)
	{
		Span<byte> size = stackalloc byte[4];
		BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)cols);
		BinaryPrimitives.WriteUInt16LittleEndian(size[2..], (ushort)rows);
		_connection.Send(Channel, CoreFrameKind.Resize, size);
	}

	// Display facts: bg, fg and cursor as #rrggbb; cell dimensions as [width, height] in pixels.
	public void Facts(JsonObject display) => _connection.Send(Channel, CoreFrameKind.Facts, Encoding.UTF8.GetBytes(display.ToJsonString()));

	// Detach; the core's empty Close response ends Frames.
	public void Close()
	{
		if (Interlocked.Exchange(ref _closed, 1) == 0 && Volatile.Read(ref _ended) == 0) { _connection.Send(Channel, CoreFrameKind.Close, []); }
	}

	// Without a consumer, recycle late frames until the core closes the stream or the connection drops.
	public async Task Drain()
	{
		await foreach (CoreFrame frame in Frames.ReadAllAsync().ConfigureAwait(false)) { Recycle(frame); }
	}

	// Deliver Data frames from the connection reader.
	internal ChannelWriter<CoreFrame> Incoming => _frames.Writer;

	// Deliver the final Closed or Lost frame exactly once.
	internal void End(CoreFrameType type, JsonNode? reason)
	{
		if (Interlocked.Exchange(ref _ended, 1) == 1) { return; }
		_frames.Writer.TryWrite(new CoreFrame(type, null, 0, reason));
		_frames.Writer.TryComplete();
	}
}
