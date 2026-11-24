using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace HerdrRemote.Remote;

// A kitty graphics image as PNG bytes with pixel dimensions. Id is the program's unsigned 32-bit i= value,
// or a stream-local negative ID if none was supplied.
internal sealed record KittyImage(long Id, byte[] Png, int Width, int Height);

// Handle kitty graphics before xterm.js, which does not draw these images (sw.kovidgoyal.net/kitty/graphics-protocol).
// Remove graphics APCs (ESC _ G ... ESC \) from output; pass all other APCs through unchanged.
// Transmissions (a=t or a=T) raise Transmitted. Support base64, m=1 chunking and o=z zlib; preserve PNG f=100 and convert
// raw RGB f=24 or RGBA f=32 to PNG. Only direct payloads (t=d) are supported, not file-based transfers.
// Placements (a=T or a=p) emit OSC 5113 "p;id;cols;rows;x;y;w;h" for the view to draw at the cursor.
// x, y, w and h describe the source rectangle in image pixels, or 0 if omitted. Unless C=1, move the cursor right by the
// placement width and down to its last row, matching kitty. Deletes (a=d) emit "d;id", with 0 meaning all images.
// Virtual placements (U=1) leave output unchanged and raise VirtualPlaced. The program emits U+10EEEE placeholder cells,
// encoding the image ID in their color and row/column in diacritics; the view draws over them.
// The core's screen neither draws images nor advances its cursor for them, so the phone maintains image rows itself.
// Answer queries (a=q) and identified transmissions as kitty does, subject to q= suppression; the core leaves replies to viewers.
// One instance per stream; not thread-safe.
internal sealed class KittyGraphics
{
	// Reject decoded or inflated images larger than this with EINVAL, while still stripping their bytes from output.
	// This permits a 3840x2160 raw RGBA image (33,177,600 bytes).
	private const int MaxImage = 32 * 1024 * 1024;
	private const int MaxPixels = MaxImage / 4;
	// Bound each placement's allocation while keeping IND scrolling for accepted rows.
	private const int MaxPlacementCells = 4096;
	// Drop APC text above this limit: enough for an unchunked MaxImage payload in base64 plus command keys.
	private const int MaxApc = MaxImage * 4 / 3 + 4096;

	private enum State { Ground, Escape, Apc, ApcEscape }

	private State _state;
	// Buffer the APC after ESC _. Release oversized backing storage after processing so one large image does not
	// retain its allocation for the stream's lifetime.
	private ArrayBufferWriter<byte> _apc = new(4096);
	private const int KeptCapacity = 1024 * 1024;
	// For m=1 chunking, retain the first chunk's keys and incrementally decoded bytes.
	// Carry incomplete four-character base64 groups into the next chunk.
	private Dictionary<char, string>? _chunked;
	private readonly Decoded _decoded = new();
	private readonly byte[] _carry = new byte[4];
	private int _carried;
	// Drop the transmission on completion if its APC exceeds MaxApc, its decoded bytes exceed MaxImage,
	// or any chunk contains invalid base64.
	private bool _apcOverflow;
	private bool _chunksOverflow;
	private bool _chunksBad;
	private long _anonymous;
	// View cell dimensions in device pixels, used when a placement omits columns and rows.
	private int _cellWidth = 10;
	private int _cellHeight = 20;
	// Last placement dimensions in cells by image ID, reused by a=p without c= or r=.
	private readonly Dictionary<long, (int Width, int Height)> _sizes = [];

	public event Action<KittyImage>? Transmitted;

	// A chunked transmission with an i= image ID has started; Transmitted or Dropped completes it.
	public event Action<long>? Receiving;

	// An identified transmission ends without Transmitted, for example because it is oversized, invalid or file-based.
	public event Action<long>? Dropped;

	// Automatic terminal replies, not user input.
	public event Action<byte[]>? Replied;

	// Virtual placement (U=1): image ID and placeholder grid dimensions in columns and rows.
	public event Action<long, int, int>? VirtualPlaced;

	public void SetCell(int width, int height)
	{
		if (width > 0 && height > 0) { (_cellWidth, _cellHeight) = (width, height); }
	}

	public void Release()
	{
		_chunked = null;
		_decoded.Release();
	}

	// Write stream bytes to output, replacing graphics APCs with placement markers and cursor movements.
	public void Filter(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
	{
		// Start of the plain-text run being copied, used only in Ground state.
		int start = 0;
		for (int i = 0; i < input.Length; i++)
		{
			byte b = input[i];
			switch (_state)
			{
				case State.Ground:
					if (b == 0x1b)
					{
						output.Write(input[start..i]);
						_state = State.Escape;
					}
					break;
				case State.Escape:
					if (b == (byte)'_')
					{
						_apc.ResetWrittenCount();
						_apcOverflow = false;
						_state = State.Apc;
					}
					else
					{
						// Not an APC: pass the escape through and reprocess this byte as plain input; it may begin another escape.
						output.Write("\u001b"u8);
						_state = State.Ground;
						start = i;
						i--;
					}
					break;
				case State.Apc:
				{
					// Copy the entire run up to the next ESC or end of input.
					int esc = input[i..].IndexOf((byte)0x1b);
					int end = esc < 0 ? input.Length : i + esc;
					ReadOnlySpan<byte> run = input[i..end];
					int room = MaxApc - _apc.WrittenCount;
					if (run.Length <= room) { _apc.Write(run); }
					else
					{
						_apc.Write(run[..room]);
						_apcOverflow = true;
					}
					if (esc >= 0) { _state = State.ApcEscape; }
					i = esc < 0 ? input.Length - 1 : end;
					break;
				}
				case State.ApcEscape:
					if (b == (byte)'\\')
					{
						// Drop oversized APCs without leaking their truncated prefix into terminal output.
						// For graphics APCs, retain only enough to read keys and answer oversized transmissions with EINVAL.
						bool graphics = _apc.WrittenCount > 0 && _apc.WrittenSpan[0] == (byte)'G';
						if (graphics) { Command(_apc.WrittenSpan[1..], output, _apcOverflow); }
						else if (!_apcOverflow)
						{
							// Pass non-graphics APCs through unchanged.
							output.Write("\u001b_"u8);
							output.Write(_apc.WrittenSpan);
							output.Write("\u001b\\"u8);
						}
						if (_apc.Capacity > KeptCapacity) { _apc = new ArrayBufferWriter<byte>(4096); }
						_state = State.Ground;
						start = i + 1;
					}
					else
					{
						if (_apc.WrittenCount < MaxApc - 1)
						{
							_apc.Write("\u001b"u8);
							_apc.Write(input.Slice(i, 1));
						}
						else { _apcOverflow = true; }
						_state = State.Apc;
					}
					break;
			}
		}
		if (_state == State.Ground) { output.Write(input[start..]); }
	}

	// Parse "key=value,...;payload". For APCs exceeding MaxApc, command holds only the prefix needed to read keys.
	// Answer oversized transmissions with EINVAL; silently drop other oversized commands.
	private void Command(ReadOnlySpan<byte> command, IBufferWriter<byte> output, bool overflow)
	{
		int split = command.IndexOf((byte)';');
		// A truncated APC without the key terminator identifies no command.
		if (overflow && split < 0) { return; }
		Dictionary<char, string> keys = [];
		foreach (string pair in Encoding.ASCII.GetString(split < 0 ? command : command[..split]).Split(','))
		{
			if (pair.Length >= 3 && pair[1] == '=') { keys[pair[0]] = pair[2..]; }
		}
		ReadOnlySpan<byte> payload = split < 0 || overflow ? [] : command[(split + 1)..];
		// Decoded image bytes, or null for invalid or oversized data (tooLarge).
		byte[]? data = null;
		bool tooLarge = overflow;
		// Continuation chunks carry only m= and payload; the first chunk's keys define the transmission.
		// Decode incrementally. Once the total exceeds MaxImage, discard subsequent data and drop the transmission on its final chunk.
		if (_chunked is not null)
		{
			bool more = keys.GetValueOrDefault('m') == "1";
			if (overflow) { Overflow(); }
			else { DecodeChunk(payload, !more); }
			if (more) { return; }
			keys = _chunked;
			_chunked = null;
			tooLarge = _chunksOverflow;
			if (!_chunksOverflow && !_chunksBad) { data = _decoded.ToArray(); }
			_decoded.Release();
		}
		else if (keys.GetValueOrDefault('m') == "1")
		{
			_chunked = keys;
			_chunksOverflow = false;
			_chunksBad = false;
			_carried = 0;
			_decoded.Release();
			if (overflow) { Overflow(); }
			else { DecodeChunk(payload, false); }
			if (Transmission(keys) && IdOf(keys) is > 0 and long receiving) { Receiving?.Invoke(receiving); }
			return;
		}
		else if (!overflow && (keys.GetValueOrDefault('a') ?? "t") is "t" or "T" or "q")
		{
			// Decode unchunked payloads at once; MaxApc bounds their size near MaxImage.
			if (_decoded.Decode(payload) && _decoded.Length <= MaxImage) { data = _decoded.ToArray(); }
			else if (_decoded.Length > MaxImage) { tooLarge = true; }
			_decoded.Release();
		}
		string action = keys.GetValueOrDefault('a') ?? "t";
		long id = IdOf(keys);
		int quiet = int.TryParse(keys.GetValueOrDefault('q'), NumberStyles.None, CultureInfo.InvariantCulture, out int q) ? q : 0;
		// Oversized commands cannot place or delete images. Only transmission/query processing can return EINVAL.
		if (tooLarge && action is not ("t" or "T" or "q")) { return; }
		switch (action)
		{
			case "t" or "T" or "q":
				// Decode base64 and optional o=z compression. Preserve PNG bytes, reading dimensions from the header; convert raw RGB/RGBA
				// (s= width, v= height) to PNG. Leave image null if invalid, oversized or file-based (t=f, t=t or t=s).
				KittyImage? image = null;
				if (keys.GetValueOrDefault('t') is null or "d" && data is not null)
				{
					int length = data.Length;
					if (keys.GetValueOrDefault('o') == "z")
					{
						// Reject inflated output above MaxImage, including zlib bombs, and malformed zlib data.
						// Clear rejected output so image validation fails below without ending the stream.
						using MemoryStream inflated = new();
						try
						{
							using ZLibStream zlib = new(new MemoryStream(data, 0, length), CompressionMode.Decompress);
							byte[] block = new byte[81920];
							for (int read = zlib.Read(block); read > 0 && inflated.Length <= MaxImage; read = zlib.Read(block)) { inflated.Write(block, 0, read); }
							if (inflated.Length > MaxImage) { inflated.SetLength(0); }
						}
						catch (InvalidDataException)
						{
							inflated.SetLength(0);
						}
						data = inflated.ToArray();
						length = data.Length;
					}
					string format = keys.GetValueOrDefault('f') ?? "32";
					int channels = format == "24" ? 3 : format == "32" ? 4 : 0;
					long imageId = id == 0 ? --_anonymous : id;
					// Validate the eight-byte PNG signature and IHDR, then read dimensions at offsets 16 and 20. Preserve the PNG bytes.
					if (format == "100" && length >= 33 && data.AsSpan(0, 8).SequenceEqual<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a])
						&& BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) == 13 && data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
					{
						uint pngWidth = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
						uint pngHeight = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
						if (pngWidth >= 1 && pngHeight >= 1 && (ulong)pngWidth * pngHeight <= MaxPixels)
						{
							image = new KittyImage(imageId, data, (int)pngWidth, (int)pngHeight);
						}
					}
					else if (channels > 0
						&& int.TryParse(keys.GetValueOrDefault('s'), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
						&& int.TryParse(keys.GetValueOrDefault('v'), NumberStyles.None, CultureInfo.InvariantCulture, out int height)
						&& width > 0 && height > 0 && (long)width * height <= MaxPixels && (long)width * height * channels <= length)
					{
						// Encode raw pixels as PNG: 8-bit RGB/RGBA IHDR, one IDAT with filter byte 0 before each row, then IEND.
						using MemoryStream png = new();
						png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
						byte[] header = new byte[13];
						BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
						BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
						header[8] = 8;
						header[9] = channels == 3 ? (byte)2 : (byte)6;
						Chunk(png, "IHDR", header);
						using MemoryStream rows = new();
						using (ZLibStream zlib = new(rows, CompressionLevel.Fastest, leaveOpen: true))
						{
							for (int y = 0; y < height; y++)
							{
								zlib.WriteByte(0);
								zlib.Write(data, y * width * channels, width * channels);
							}
						}
						Chunk(png, "IDAT", rows.ToArray());
						Chunk(png, "IEND", []);
						image = new KittyImage(imageId, png.ToArray(), width, height);
					}
				}
				int placementCols = 0, placementRows = 0;
				if (action == "T" && image is not null && !TryPlacementSize(image.Id, keys, image.Width, image.Height, out placementCols, out placementRows))
				{
					if (id != 0 && quiet < 2) { Replied?.Invoke(Encoding.ASCII.GetBytes($"\e_Gi={id.ToString(CultureInfo.InvariantCulture)};EINVAL:placement exceeds supported dimensions\e\\")); }
					Drop(keys);
					return;
				}
				if (id != 0 && (image is null ? quiet < 2 : quiet < 1))
				{
					Replied?.Invoke(Encoding.ASCII.GetBytes($"\e_Gi={id.ToString(CultureInfo.InvariantCulture)};{(image is null ? "EINVAL:cannot read the image" : "OK")}\e\\"));
				}
				if (image is null || action == "q")
				{
					Drop(keys);
					return;
				}
				Transmitted?.Invoke(image);
				if (action == "T") { Place(image.Id, keys, placementCols, placementRows, output); }
				break;
			case "p" when id != 0:
				if (!TryPlacementSize(id, keys, 0, 0, out int placeCols, out int placeRows))
				{
					if (quiet < 2) { Replied?.Invoke(Encoding.ASCII.GetBytes($"\e_Gi={id.ToString(CultureInfo.InvariantCulture)};EINVAL:placement exceeds supported dimensions\e\\")); }
					return;
				}
				Place(id, keys, placeCols, placeRows, output);
				break;
			case "d":
				// Delete all images for d=a/A or no d=; delete one ID for d=i/I. Other deletion scopes are not tracked separately and do nothing.
				string kind = keys.GetValueOrDefault('d') ?? "a";
				if (kind is "a" or "A" or "i" or "I") { output.Write(Encoding.ASCII.GetBytes($"\e]5113;d;{(kind is "i" or "I" ? id : 0L).ToString(CultureInfo.InvariantCulture)}\a")); }
				break;
		}
	}

	// Decode complete four-character base64 groups immediately and carry the remainder to the next chunk.
	// kitty normally aligns all but the final chunk; carrying also handles unaligned input. Decode all remaining bytes on the last chunk.
	private void DecodeChunk(ReadOnlySpan<byte> base64, bool last)
	{
		if (_chunksOverflow || _chunksBad) { return; }
		if (_carried > 0)
		{
			int take = Math.Min(4 - _carried, base64.Length);
			base64[..take].CopyTo(_carry.AsSpan(_carried));
			_carried += take;
			base64 = base64[take..];
			if (_carried < 4 && !last) { return; }
			if (!_decoded.Decode(_carry.AsSpan(0, _carried)))
			{
				Unreadable();
				return;
			}
			_carried = 0;
		}
		int whole = last ? base64.Length : base64.Length - base64.Length % 4;
		if (!_decoded.Decode(base64[..whole]))
		{
			Unreadable();
			return;
		}
		base64[whole..].CopyTo(_carry);
		_carried = base64.Length - whole;
		if (_decoded.Length > MaxImage) { Overflow(); }
	}

	// Stop retaining data after a chunked transmission exceeds the size limit.
	private void Overflow()
	{
		_chunksOverflow = true;
		_decoded.Release();
	}

	// Invalid base64 makes the entire chunked transmission unreadable.
	private void Unreadable()
	{
		_chunksBad = true;
		_decoded.Release();
	}

	// Decode into pooled 64 KiB blocks, then copy once to an exactly sized array.
	// Peak transmission storage is about twice the decoded size, without spare capacity from a doubling buffer.
	private sealed class Decoded
	{
		private const int BlockSize = 64 * 1024;
		private readonly List<(byte[] Block, int Used)> _blocks = [];

		public long Length { get; private set; }

		// Decode complete four-character base64 groups after any retained bytes; allow padding only at the end. Return false if invalid.
		public bool Decode(ReadOnlySpan<byte> base64)
		{
			while (!base64.IsEmpty)
			{
				if (_blocks.Count == 0 || BlockSize - _blocks[^1].Used < 3) { _blocks.Add((ArrayPool<byte>.Shared.Rent(BlockSize), 0)); }
				(byte[] block, int used) = _blocks[^1];
				OperationStatus status = Base64.DecodeFromUtf8(base64, block.AsSpan(used, BlockSize - used), out int consumed, out int written);
				_blocks[^1] = (block, used + written);
				Length += written;
				base64 = base64[consumed..];
				if (status == OperationStatus.Done) { return true; }
				if (status != OperationStatus.DestinationTooSmall || consumed == 0 && BlockSize - used >= 3) { return false; }
			}
			return true;
		}

		public byte[] ToArray()
		{
			byte[] all = new byte[Length];
			int at = 0;
			foreach ((byte[] block, int used) in _blocks)
			{
				block.AsSpan(0, used).CopyTo(all.AsSpan(at));
				at += used;
			}
			return all;
		}

		public void Release()
		{
			foreach ((byte[] block, _) in _blocks) { ArrayPool<byte>.Shared.Return(block); }
			_blocks.Clear();
			Length = 0;
		}
	}

	// Read the unsigned 32-bit i= image ID, or 0 if absent or invalid.
	private static long IdOf(Dictionary<char, string> keys) =>
		uint.TryParse(keys.GetValueOrDefault('i'), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0;

	// Transmission actions are a=t (the default) and a=T; a=q only tests the image.
	private static bool Transmission(Dictionary<char, string> keys) => (keys.GetValueOrDefault('a') ?? "t") is "t" or "T";

	// Raise Dropped for an identified transmission that ends without Transmitted.
	private void Drop(Dictionary<char, string> keys)
	{
		if (Transmission(keys) && IdOf(keys) is > 0 and long id) { Dropped?.Invoke(id); }
	}

	// Use c= and r= placement dimensions, otherwise round image pixels up to whole cells.
	// For a=p without dimensions, reuse the image's last placement size.
	private bool TryPlacementSize(long id, Dictionary<char, string> keys, int width, int height, out int cols, out int rows)
	{
		cols = rows = 0;
		uint c = 0, r = 0;
		if (keys.TryGetValue('c', out string? columns)
			&& (!uint.TryParse(columns, NumberStyles.None, CultureInfo.InvariantCulture, out c) || c > MaxPlacementCells
				|| columns.AsSpan().ContainsAnyExceptInRange('0', '9'))) { return false; }
		if (keys.TryGetValue('r', out string? lines)
			&& (!uint.TryParse(lines, NumberStyles.None, CultureInfo.InvariantCulture, out r) || r > MaxPlacementCells
				|| lines.AsSpan().ContainsAnyExceptInRange('0', '9'))) { return false; }
		(int lastCols, int lastRows) = _sizes.GetValueOrDefault(id, (1, 1));
		cols = c > 0 ? (int)c : width > 0 ? 1 + (width - 1) / _cellWidth : lastCols;
		rows = r > 0 ? (int)r : height > 0 ? 1 + (height - 1) / _cellHeight : lastRows;
		return cols is >= 1 and <= MaxPlacementCells && rows is >= 1 and <= MaxPlacementCells;
	}

	// Virtual placements (U=1), as used by Claude Code, only raise VirtualPlaced with the placeholder grid dimensions;
	// the program renders Unicode placeholder cells. Direct placements emit "p;id;cols;rows;x;y;w;h" at the cursor,
	// with x/y/w/h in source-image pixels (0 if omitted; w/h of 0 use the rest of the image).
	// Unless C=1, move the cursor past the image.
	private void Place(long id, Dictionary<char, string> keys, int cols, int rows, IBufferWriter<byte> output)
	{
		_sizes[id] = (cols, rows);
		if (keys.GetValueOrDefault('U') == "1")
		{
			VirtualPlaced?.Invoke(id, cols, rows);
			return;
		}
		int Source(char key) => int.TryParse(keys.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
		StringBuilder marker = new(string.Create(CultureInfo.InvariantCulture,
			$"\e]5113;p;{id};{cols};{rows};{Source('x')};{Source('y')};{Source('w')};{Source('h')}\a"));
		if (keys.GetValueOrDefault('C') != "1")
		{
			// Move down to the image's last row using IND so scrolling works, then right past its last column.
			marker.Insert(marker.Length, "\eD", rows - 1);
			marker.Append(CultureInfo.InvariantCulture, $"\e[{cols}C");
		}
		output.Write(Encoding.ASCII.GetBytes(marker.ToString()));
	}

	// Write a PNG chunk: length, type, data and the CRC-32 of type plus data.
	private static void Chunk(Stream png, string type, byte[] data)
	{
		Span<byte> number = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
		png.Write(number);
		byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
		png.Write(typed);
		uint crc = 0xffffffff;
		foreach (byte b in typed)
		{
			crc ^= b;
			for (int k = 0; k < 8; k++) { crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1; }
		}
		BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
		png.Write(number);
	}
}
