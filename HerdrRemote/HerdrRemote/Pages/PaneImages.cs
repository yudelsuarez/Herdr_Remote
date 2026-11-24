using System.Globalization;
using HerdrRemote.Remote;
using Microsoft.Maui.Graphics.Platform;
using GraphicsImage = Microsoft.Maui.Graphics.IImage;
using ImageFormat = Microsoft.Maui.Graphics.ImageFormat;

namespace HerdrRemote.Pages;

// One image a terminal's program sent: its PNG as sent (kept encoded), its thumbnail (a data URL the view draws), its pixel size and,
// for Unicode placeholders, the columns and rows of the grid it fills (0 when it has none). Key names it on the page: "{address}#{id}".
internal sealed record PaneImage(string Address, long Id, byte[] Png, string Thumbnail, int Width, int Height, int Cols, int Rows)
{
	public long Bytes => Png.Length + Thumbnail.Length * 2L;

	public string Key => string.Create(CultureInfo.InvariantCulture, $"{Address}#{Id}");
}

// App-lifetime kitty graphics cache survives stream reattachment because the core does not resend images
// after mode switches, backgrounding, or watchdog recovery. Restore cached images on every attach.
// Budget all terminals together, evicting least-recently-used entries first. Only the viewer decodes a full
// image, one at a time. Use platform codecs off the UI thread; access the cache itself only on the UI thread.
internal static class PaneImages
{
	// An image let go: the budget evicted it, or a new image with its id replaced it (the view's page lets its picture go).
	public static event Action<PaneImage>? Dropped;

	// A thumbnail's longest side, in pixels: sharp across a placeholder grid of a few dozen cells at the phone's density.
	private const float ThumbnailSide = 1024;
	private const long Budget = 48L * 1024 * 1024;
	private const long PendingBudget = Budget;
	private static long _reserved;
	private static readonly SemaphoreSlim Converting = new(1, 1);

	// Owns the PNG from the reader's post until the cache keeps or drops it, including superseded UI work.
	public sealed class Pending
	{
		public readonly long Id;
		public readonly long Bytes;
		private KittyImage? _image;

		private Pending(KittyImage image) { Id = image.Id; Bytes = image.Png.Length; _image = image; }

		public KittyImage? Image => Volatile.Read(ref _image);

		public static Pending? TryReserve(KittyImage image)
		{
			long reserved = Volatile.Read(ref _reserved);
			while (image.Png.Length <= PendingBudget - reserved)
			{
				long previous = Interlocked.CompareExchange(ref _reserved, reserved + image.Png.Length, reserved);
				if (previous == reserved) { return new Pending(image); }
				reserved = previous;
			}
			return null;
		}

		public void Drop()
		{
			if (Interlocked.Exchange(ref _image, null) is not null) { Interlocked.Add(ref _reserved, -Bytes); }
		}
	}

	// Logical budget units, not a measurement of CLR object sizes; metadata-only IDs share the image budget.
	private const long Bookkeeping = 160;
	private sealed class Entry((string Address, long Id) key)
	{
		public readonly (string Address, long Id) Key = key;
		public PaneImage? Image;
		public (int Cols, int Rows) Grid;
		public long KeptSequence;
		public int Pending;
		public Pending? Waiting;
		public CancellationTokenSource? WaitCancel;
		public bool Retired;
		public LinkedListNode<Entry>? Node;
		public long Bytes => Bookkeeping + Key.Address.Length * 2L + (Image?.Bytes ?? 0);
	}

	private static readonly Dictionary<(string Address, long Id), Entry> Entries = [];
	// Least recently used first, including placements whose image has not arrived.
	private static readonly LinkedList<Entry> Lru = [];
	private static long _bytes;
	private static long _sequence;

	public static byte[]? Get(string address, long id)
	{
		if (!Entries.TryGetValue((address, id), out Entry? entry) || entry.Image is null) { return null; }
		Touch(entry);
		return entry.Image.Png;
	}

	// A terminal's images, to draw again after an attach.
	public static IEnumerable<PaneImage> Of(string address) => Lru.Where(entry => entry.Key.Address == address && entry.Image is not null).Select(entry => entry.Image!);

	// A placeholder grid for an image; the image as it now stands (null until it is decoded).
	public static PaneImage? Grid(string address, long id, int cols, int rows)
	{
		Entry entry = Use(address, id);
		entry.Grid = (cols, rows);
		if (entry.Image is { } image) { entry.Image = image with { Cols = cols, Rows = rows }; }
		Trim();
		return entry.Retired ? null : entry.Image;
	}

	// Keep a received image by id and build its thumbnail off the UI thread: reuse a small PNG, otherwise resize.
	// Return null on decode failure or if a newer transmission wins during decoding. Evict oldest entries over
	// budget and raise Dropped for each removed image. Repeated identical id/PNG bytes reuse the cached image
	// and thumbnail, moving it to most recently used; ShowImage then avoids resending the same key to the view.
	public static async Task<PaneImage?> AddAsync(string address, Pending pending)
	{
		if (pending.Image is not { } image) { return null; }
		long sequence = ++_sequence;
		Entry entry = Use(address, image.Id);
		if (Same(entry, image, sequence) is { } same) { pending.Drop(); return same; }
		image = null;
		CancellationTokenSource cancel = new();
		entry.Waiting?.Drop();
		entry.WaitCancel?.Cancel();
		entry.Waiting = pending;
		entry.WaitCancel = cancel;
		entry.Pending++;
		bool converting = false;
		try
		{
			Trim();
			try { await Converting.WaitAsync(cancel.Token); }
			catch (OperationCanceledException) { return null; }
			converting = true;
			if (entry.Retired || pending.Image is not { } current) { return null; }
			string? thumbnail = await Task.Run(() =>
			{
				if (Math.Max(current.Width, current.Height) <= ThumbnailSide) { return $"data:image/png;base64,{Convert.ToBase64String(current.Png)}"; }
				try
				{
					using GraphicsImage full = PlatformImage.FromStream(new MemoryStream(current.Png), ImageFormat.Png);
					using GraphicsImage small = full.Downsize(ThumbnailSide);
					return $"data:image/png;base64,{Convert.ToBase64String(small.AsBytes(ImageFormat.Png))}";
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[Pane] image {current.Id} not decoded: {ex.GetType().Name}: {ex.Message}");
					return null;
				}
			});
			if (thumbnail is null || entry.Retired || pending.Image is null || !Entries.TryGetValue(entry.Key, out Entry? active) || active != entry || entry.KeptSequence > sequence) { return null; }
			// The same picture kept while this one was built (two equal transmissions back to back): that one stays.
			if (Same(entry, current, sequence) is { } already) { return already; }
			entry.KeptSequence = sequence;
			(int cols, int rows) = entry.Grid;
			PaneImage kept = new(address, current.Id, current.Png, thumbnail, current.Width, current.Height, cols, rows);
			// At most one: every image is kept only after the one with its id left.
			PaneImage? replaced = entry.Image;
			_bytes += kept.Bytes - (replaced?.Bytes ?? 0);
			entry.Image = kept;
			Touch(entry);
			if (replaced is not null) { Dropped?.Invoke(replaced); }
			Trim();
			return entry.Retired ? null : kept;
		}
		finally
		{
			if (converting) { Converting.Release(); }
			pending.Drop();
			entry.Pending--;
			if (entry.Waiting == pending) { entry.Waiting = null; entry.WaitCancel = null; }
			cancel.Dispose();
		}
	}

	public static bool Busy(string address, long id) => Entries.TryGetValue((address, id), out Entry? entry) && (entry.Pending > 0 || entry.Waiting is not null);

	// Reuse matching id/PNG bytes, update LRU order, and record sequence so an older in-progress decode
	// cannot overwrite this transmission. Return null when no matching image exists.
	private static PaneImage? Same(Entry entry, KittyImage image, long sequence)
	{
		if (entry.Image is not { } kept || !kept.Png.AsSpan().SequenceEqual(image.Png)) { return null; }
		Touch(entry);
		entry.KeptSequence = sequence;
		return kept;
	}

	private static Entry Use(string address, long id)
	{
		if (Entries.TryGetValue((address, id), out Entry? entry)) { Touch(entry); return entry; }
		entry = new((address, id));
		Entries.Add(entry.Key, entry);
		entry.Node = Lru.AddLast(entry);
		_bytes += entry.Bytes;
		return entry;
	}

	private static void Touch(Entry entry)
	{
		Lru.Remove(entry.Node!);
		Lru.AddLast(entry.Node!);
	}

	private static void Trim()
	{
		// A sole oversized image may exceed the budget; a sole oversized placement may not.
		while (_bytes > Budget && Lru.First is { } first && !(Lru.Count == 1 && first.Value.Image is not null)) { Retire(first.Value); }
	}

	private static void Retire(Entry entry)
	{
		entry.Retired = true;
		entry.Waiting?.Drop();
		entry.WaitCancel?.Cancel();
		Entries.Remove(entry.Key);
		Lru.Remove(entry.Node!);
		entry.Node = null;
		_bytes -= entry.Bytes;
		PaneImage? image = entry.Image;
		entry.Image = null; // an outstanding decoder keeps only the retired identity, not the evicted PNG/thumbnail
		if (image is not null) { Dropped?.Invoke(image); }
	}

	public static void RemoveTerminal(string address)
	{
		foreach (Entry entry in Entries.Values.Where(entry => entry.Key.Address == address).ToList()) { Retire(entry); }
	}

	public static void Clear()
	{
		while (Lru.First is { } first) { Retire(first.Value); }
	}
}
