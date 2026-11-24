using System.Buffers;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace HerdrRemote.Remote;

// The open terminal's terminal.attach stream over the session's shared core connection. App.Herdr.Terminal retains it
// for reattachment after reconnect. The phone controls the grid while it is the last active viewer; keys use DATA frames.
// Attach with 2000 scrollback lines. Read frames off the UI thread, but raise events on it.
// Credit bytes only after xterm.js writes them. Closing cancels an in-flight attach.
internal sealed class TerminalStream(string address)
{
	private const int History = 2000;
	// Wait for GridSettled up to this limit: the WebView resizes its page after MAUI layout.
	private static readonly TimeSpan SettleCap = TimeSpan.FromMilliseconds(1500);
	// Maximum reattachments per open when the grid changes before the first paint is written.
	private const int MaxReattaches = 3;
	// The first Data frame shorter than this core repaint chunk size ends the first paint (observed on herdr-core 0.1.19).
	private const int FirstPaintFrame = 16384;
	// Filtered-output batching limit for one UI post.
	private const int MaxPost = 256 * 1024;

	private CoreStream? _stream;
	// Grid-change reattachments since the latest Control or Reopen.
	private int _reattaches;
	// First-paint byte length (-1 until its last frame is posted), bytes credited since opening, and whether the view
	// has written the complete first paint.
	private long _paintBytes = -1;
	private long _credited;
	private bool _painted;
	// Increment on open and close to discard output and completion events from older streams.
	private int _epoch;
	// Cancellation for the pending attach or current stream; Close cancels it.
	private CancellationTokenSource _cancel = new();
	// Cell dimensions in device pixels, used for unsized image placements and core cell-size query responses.
	private (int Width, int Height) _cell = (10, 20);
	private KittyGraphics? _graphics;

	public string Address => address;

	// The phone's terminal grid dimensions.
	public int Cols { get; private set; }

	public int Rows { get; private set; }

	// View colors (bg, fg, cursor as #rrggbb), sent with cell size on every attach so the core can answer display queries
	// with this view's appearance.
	public JsonObject Colors { get; set; } = [];

	// Raised before the first paint of each new stream so the view clears its previous contents.
	public event Action? Opening;

	// Ordered output on the UI thread: pooled filtered bytes, their length, and the original stream byte count to credit.
	// TerminalView.Feed must return the array to ArrayPool<byte>.Shared after copying it into its message,
	// possibly during this call. Other listeners must use only the counts.
	public event Action<byte[], int, int>? Output;

	// Before each attach, wait up to SettleCap for TerminalView.WaitSettledAsync to finish layout. Null skips the wait.
	public Func<CancellationToken, Task>? GridSettled { get; set; }

	// Raised on the UI thread when the stream ends unexpectedly. The core supplies "exited", "killed", etc.,
	// or "lost" for a kept terminal awaiting a new shell. Null means the core connection dropped.
	public event Action<string?>? Ended;

	// Attach failure: core error kind (not_found, finished, unavailable), or null if there is no connection.
	public event Action<string?>? Failed;

	// A kitty graphics image, delivered on the UI thread. Placement markers arrive separately in terminal output.
	public event Action<Pages.PaneImages.Pending>? ImageReceived;

	// A chunked image transmission starts; raised on the UI thread with its image ID.
	public event Action<long>? ImageReceiving;

	// A transmission ends without ImageReceived, for example because it is too large or invalid; raised on the UI thread with its ID.
	public event Action<long>? ImageDropped;

	// A virtual placement's image ID and Unicode-placeholder grid dimensions, delivered on the UI thread.
	public event Action<long, int, int>? ImagePlaced;

	// Update cell dimensions in device pixels after font changes and send the new display facts to the open stream.
	public void SetCell(int width, int height)
	{
		if (width <= 0 || height <= 0 || (width, height) == _cell) { return; }
		_cell = (width, height);
		_graphics?.SetCell(width, height);
		_stream?.Facts(Display());
	}

	public void Control(int cols, int rows)
	{
		_reattaches = 0;
		Open(cols, rows);
	}

	// Reattach at the same grid after reconnect, foreground return or view reset. Always attach as a new controller, never resume.
	// This applies the phone's grid before the first paint instead of rendering output sized for another viewer.
	// A resumed attach retains the Mac's size until the first keypress.
	public void Reopen()
	{
		_reattaches = 0;
		Open(Cols, Rows);
	}

	// Reclaim the terminal size after another viewer became active. Even a same-size Resize makes this viewer active.
	// Without an open stream, do nothing; the next attach claims the size.
	public void Claim()
	{
		_stream?.Resize(Cols, Rows);
	}

	// Record fit()'s grid only when changed. With no stream (settling or attaching), defer sending it.
	// Before the first paint is written, reattach at the new grid; afterward, send a resize.
	public void Resize(int cols, int rows)
	{
		if (cols == Cols && rows == Rows) { return; }
		Cols = cols;
		Rows = rows;
		if (_stream is null) { return; }
		if (!_painted)
		{
			if (_reattaches < MaxReattaches)
			{
				_reattaches++;
				Open(cols, rows);
				return;
			}
			Debug.WriteLine("[Herdr] grid kept changing; resized after the first paint");
		}
		_stream.Resize(cols, rows);
	}

	// Bytes for the program (keys, text, pastes, mouse reports).
	public bool Input(ReadOnlySpan<byte> bytes)
	{
		if (_stream is not { } stream) { return false; }
		return stream.Input(bytes);
	}

	// Return credit for bytes written by the view so the core can send more. The first paint is complete once all its bytes are credited.
	public void Credit(int bytes)
	{
		_stream?.Credit(bytes);
		_credited += bytes;
		if (_painted || _paintBytes < 0 || _credited < _paintBytes) { return; }
		_painted = true;
		Debug.WriteLine($"[A07] t={Stopwatch.GetTimestamp()} paint=written address={address} epoch={_epoch} bytes={_paintBytes} credited={_credited}");
	}

	// Close intentionally when navigating away, switching terminals or entering the background. Suppress subsequent end events.
	public void Close()
	{
		_epoch++;
		_cancel.Cancel();
		_cancel.Dispose();
		_cancel = new CancellationTokenSource();
		_stream?.Close();
		_stream = null;
	}

	// Display facts sent to the core: colors and cell dimensions in device pixels.
	private JsonObject Display()
	{
		JsonObject display = (JsonObject)Colors.DeepClone();
		display["cell"] = new JsonArray(_cell.Width, _cell.Height);
		return display;
	}

	// Track a separate first paint for every attach, including reattachments. Wait for GridSettled up to SettleCap, then attach
	// at the current grid. Before that paint is written, Resize reattaches up to MaxReattaches times; later changes use resize frames.
	private async void Open(int cols, int rows)
	{
		Close();
		_paintBytes = -1;
		_credited = 0;
		_painted = false;
		int epoch = _epoch;
		CancellationToken ct = _cancel.Token;
		Cols = cols;
		Rows = rows;
		if (App.Herdr.Api is not { } api)
		{
			Failed?.Invoke(null);
			return;
		}
		Opening?.Invoke();
		// Wait for settled layout or SettleCap if the page never responds. Close cancels the wait silently, including navigation,
		// backgrounding or another open. An already settled view attaches immediately.
		if (GridSettled is { } settled)
		{
			try
			{
				await settled(ct).WaitAsync(SettleCap, ct);
			}
			catch (TimeoutException)
			{
				Debug.WriteLine("[Herdr] the view did not settle; attached at its grid now");
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
		if (epoch != _epoch) { return; }
		(cols, rows) = (Cols, Rows);
		try
		{
			(int Width, int Height) attachedCell = _cell;
			Debug.WriteLine($"[A07] t={Stopwatch.GetTimestamp()} attach=start address={address} epoch={epoch} cols={cols} rows={rows}");
			CoreAttach attach = await api.AttachAsync(address, cols, rows, History, Display(), ct);
			if (epoch != _epoch)
			{
				attach.Stream.Close();
				_ = attach.Stream.Drain();
				return;
			}
			// The grid changed while attaching, before a stream could send a resize. Reattach, or resize if the retry cap is reached.
			bool gridMoved = (Cols, Rows) != (cols, rows);
			if (gridMoved && _reattaches < MaxReattaches)
			{
				attach.Stream.Close();
				_ = attach.Stream.Drain();
				_reattaches++;
				Open(Cols, Rows);
				return;
			}
			_stream = attach.Stream;
			if (gridMoved)
			{
				Debug.WriteLine("[Herdr] grid kept changing; resized after the first paint");
				attach.Stream.Resize(Cols, Rows);
			}
			// Send cell dimensions that changed while the attach was in flight.
			if (_cell != attachedCell) { attach.Stream.Facts(Display()); }
			// Each attach starts with independent graphics state and no images. Send replies as automatic terminal input.
			KittyGraphics graphics = _graphics = new KittyGraphics();
			Dictionary<long, Pages.PaneImages.Pending> latest = [];
			graphics.SetCell(_cell.Width, _cell.Height);
			graphics.Transmitted += image =>
			{
				if (latest.TryGetValue(image.Id, out Pages.PaneImages.Pending? older)) { older.Drop(); }
				Pages.PaneImages.Pending? pending = Pages.PaneImages.Pending.TryReserve(image);
				if (pending is null)
				{
					long id = image.Id;
					MainThread.BeginInvokeOnMainThread(() => { if (epoch == _epoch) { ImageDropped?.Invoke(id); } });
					return;
				}
				latest[image.Id] = pending;
				MainThread.BeginInvokeOnMainThread(() =>
				{
					if (epoch != _epoch || ImageReceived is not { } received) { pending.Drop(); return; }
					received(pending);
				});
				if (latest.Count > 32)
				{
					foreach (var pair in latest) { if (pair.Value.Image is null) { latest.Remove(pair.Key); } }
				}
			};
			graphics.Receiving += id => MainThread.BeginInvokeOnMainThread(() =>
			{
				if (epoch == _epoch) { ImageReceiving?.Invoke(id); }
			});
			graphics.Dropped += id => MainThread.BeginInvokeOnMainThread(() =>
			{
				if (epoch == _epoch) { ImageDropped?.Invoke(id); }
			});
			graphics.VirtualPlaced += (id, gridCols, gridRows) => MainThread.BeginInvokeOnMainThread(() =>
			{
				if (epoch == _epoch) { ImagePlaced?.Invoke(id, gridCols, gridRows); }
			});
			graphics.Replied += reply => attach.Stream.ReplyInput(reply);
			_ = Task.Run(() => ReadAsync(attach.Stream, graphics, rows, epoch), CancellationToken.None);
		}
		catch (Exception ex)
		{
			// Log all attach failures, but report only the current attempt; the stream may have closed or been replaced meanwhile.
			Debug.WriteLine($"[Herdr] attach failed: {ex.GetType().Name}: {ex.Message}");
			if (epoch == _epoch) { Failed?.Invoke((ex as CoreRpcException)?.Kind); }
		}
	}

	// Off the UI thread, batch queued Data frames through the graphics filter, replacing images with placement markers.
	// Recycle each input buffer immediately and copy filtered output into one pooled UI post with the summed original byte count.
	// Stop batching at MaxPost filtered bytes; the view credits the original bytes later in its write callback.
	// The first Data frame shorter than FirstPaintFrame marks the first paint's byte length. Until then, run AttachSeam first
	// to remove the core's extra history/screen line feed for this attach's row count.
	// Post buffered output before an end event. Both run on the UI thread and discard stale epochs.
	// After Close or another Open changes the epoch, only recycle frames until the core ends the stream; do not filter or post them.
	private async Task ReadAsync(CoreStream stream, KittyGraphics graphics, int rows, int epoch)
	{
		ChannelReader<CoreFrame> reader = stream.Frames;
		ArrayBufferWriter<byte> filtered = new(64 * 1024);
		AttachSeam seam = new(rows);
		ArrayBufferWriter<byte> seamed = new(16 * 1024);
		// Total Data bytes received for this stream, and whether its first paint has ended.
		long received = 0;
		bool paintMarked = false;
		int frames = 0;
		int bytes = 0;
		long paint = -1;
		try
		{
			while (await reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (filtered.WrittenCount < MaxPost && reader.TryRead(out CoreFrame frame))
				{
					if (epoch != Volatile.Read(ref _epoch))
					{
						CoreStream.Recycle(frame);
						if (frame.Type != CoreFrameType.Data) { return; }
						continue;
					}
					if (frame.Type == CoreFrameType.Data)
					{
						frames++;
						bytes += frame.Length;
						received += frame.Length;
						if (!paintMarked && frame.Length < FirstPaintFrame)
						{
							paintMarked = true;
							paint = received;
						}
						if (seam.Done)
						{
							graphics.Filter(frame.Bytes, filtered);
						}
						else
						{
							seam.Write(frame.Bytes, seamed);
							if (paintMarked) { seam.End(seamed); }
							graphics.Filter(seamed.WrittenSpan, filtered);
							seamed.ResetWrittenCount();
						}
						CoreStream.Recycle(frame);
						continue;
					}
					if (!seam.Done)
					{
						seam.End(seamed);
						graphics.Filter(seamed.WrittenSpan, filtered);
						seamed.ResetWrittenCount();
					}
					PostOutput();
					string? reason = frame.Type == CoreFrameType.Closed ? frame.Reason?["reason"]?.GetValue<string>() : null;
					MainThread.BeginInvokeOnMainThread(() =>
					{
						if (epoch != _epoch) { return; }
						_stream = null;
						Ended?.Invoke(reason);
					});
					return;
				}
				PostOutput();
			}
		}
		finally
		{
			graphics.Release();
			Debug.WriteLine("[Stream] graphics released");
		}

		// Copy the accumulated Data frames into one pooled UI post. The post containing the first paint's last frame records its length.
		// Return the array on the UI thread if the post is stale or has no listener.
		void PostOutput()
		{
			if (frames == 0) { return; }
			int length = filtered.WrittenCount;
			byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
			filtered.WrittenSpan.CopyTo(rented);
			(int sum, long paintBytes) = (bytes, paint);
			filtered.ResetWrittenCount();
			(frames, bytes, paint) = (0, 0, -1);
			MainThread.BeginInvokeOnMainThread(() =>
			{
				if (epoch != _epoch || Output is not { } output)
				{
					ArrayPool<byte>.Shared.Return(rented);
					return;
				}
				if (paintBytes >= 0) { _paintBytes = paintBytes; }
				output(rented, length, sum);
			});
		}
	}
}
