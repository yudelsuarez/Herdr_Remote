using System.Buffers;

namespace HerdrRemote.Remote;

// herdr-core 0.1.19 replays an attach as reset (including ESC[H ESC[2J), history, `\r\n`, one `\n` per row, then
// ESC[H ESC[2J and the screen. The extra line feed leaves an empty scrollback row one screen above the bottom, splitting
// a paragraph for Read aloud. Remove one `\n` only from this exact sequence with exactly `rows` line feeds after `\r\n`.
// Pass every other count unchanged, including the one-fewer count from a fixed core. Buffer partial matches across frames;
// forward everything else immediately and in order. Credit all received bytes, including the removed byte.
internal sealed class AttachSeam(int rows)
{
	private static ReadOnlySpan<byte> Clear => "\e[H\e[2J"u8;

	// Pending match: a lone `\r` (_cr), or `\r\n` plus _lineFeeds further `\n` (-1 means none), followed by _clear bytes of Clear.
	private bool _cr;
	private int _lineFeeds = -1;
	private int _clear;

	// Stop scanning after correcting the sequence or reaching the end of the first paint.
	public bool Done { get; private set; }

	public void Write(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
	{
		int i = 0;
		for (; i < input.Length && !Done; i++) { Step(input[i], output); }
		if (i < input.Length) { output.Write(input[i..]); }
	}

	// At the end of the first paint, flush any partial match unchanged and stop scanning.
	public void End(IBufferWriter<byte> output)
	{
		if (Done) { return; }
		Release(output);
		Done = true;
	}

	private void Step(byte b, IBufferWriter<byte> output)
	{
		if (_lineFeeds >= 0)
		{
			if (_clear > 0 || b == Clear[0])
			{
				if (b != Clear[_clear])
				{
					Release(output);
					Step(b, output);
					return;
				}
				if (++_clear < Clear.Length) { return; }
				// Remove one `\n` only when the matched sequence contains exactly `rows` line feeds after `\r\n`.
				if (_lineFeeds == rows) { _lineFeeds--; }
				Release(output);
				Done = true;
				return;
			}
			if (b == '\n' && _lineFeeds < rows)
			{
				_lineFeeds++;
				return;
			}
			Release(output);
			Step(b, output);
			return;
		}
		if (_cr)
		{
			if (b == '\n')
			{
				_cr = false;
				_lineFeeds = 0;
				return;
			}
			Release(output);
			Step(b, output);
			return;
		}
		if (b == '\r')
		{
			_cr = true;
			return;
		}
		Put(output, b);
	}

	// Flush pending bytes in order, removing the extra `\n` only for a confirmed match. Retain nothing afterward.
	private void Release(IBufferWriter<byte> output)
	{
		if (_cr) { Put(output, (byte)'\r'); }
		if (_lineFeeds >= 0)
		{
			Put(output, (byte)'\r');
			for (int n = 0; n <= _lineFeeds; n++) { Put(output, (byte)'\n'); }
			output.Write(Clear[.._clear]);
		}
		_cr = false;
		_lineFeeds = -1;
		_clear = 0;
	}

	private static void Put(IBufferWriter<byte> output, byte b)
	{
		output.GetSpan(1)[0] = b;
		output.Advance(1);
	}
}
