using System.Globalization;
using System.Text;

namespace HerdrRemote.Remote;

// Only the known suffix at the terminal cursor is editable. Never model the program's line or history.
internal sealed class TerminalImeWord(Action<string> insert, Action erase)
{
	private const int Limit = 64;
	internal string Text { get; private set; } = "";
	internal int Start { get; private set; } = -1;

	internal void Reset() { Text = ""; Start = -1; }
	internal void Finish() => Start = -1;

	internal bool Mark(int start, int end)
	{
		if (start > end) { (start, end) = (end, start); }
		if (start < 0 || end != Text.Length || !Boundary(start)) { return false; }
		Start = start == end ? -1 : start;
		return true;
	}

	internal bool Replace(string text, bool composing)
	{
		int start = Start < 0 ? Text.Length : Start;
		if (composing && text.Length > Limit - start) { return false; }
		string previous = Text[start..];
		bool append = previous.Length == 0 || text == previous ||
			(text.StartsWith(previous, StringComparison.Ordinal) && Array.BinarySearch(StringInfo.ParseCombiningCharacters(text), previous.Length) >= 0);
		string added = append ? text[previous.Length..] : text;
		if (composing)
		{
			Text = Text[..start] + text;
			Start = text.Length == 0 ? -1 : start;
		}
		else
		{
			string value = Text[..start] + text;
			int space = value.AsSpan().LastIndexOfAny(" \t\r\n");
			// Keep context for IMEs that commit individual letters, not a completed composition.
			Text = Start >= 0 || value.Length - space - 1 > Limit ? "" : value[(space + 1)..];
			Start = -1;
		}
		if (!append) { Erase(previous); }
		if (added.Length > 0) { insert(added); }
		return true;
	}

	internal bool Delete(int before, int after, bool codePoints)
	{
		if (after != 0 || before < 0) { return false; }
		if (before == 0) { return true; }
		if (Text.Length == 0 && before == 1) { erase(); return true; }
		int start = Text.Length;
		if (codePoints)
		{
			for (int left = before; left > 0; left--)
			{
				if (Rune.DecodeLastFromUtf16(Text.AsSpan(0, start), out _, out int units) != System.Buffers.OperationStatus.Done) { return false; }
				start -= units;
			}
		}
		else { start -= before; }
		if (start < 0 || !Boundary(start)) { return false; }
		string removed = Text[start..];
		Text = Text[..start];
		if (Start >= start) { Start = -1; }
		Erase(removed);
		return true;
	}

	private bool Boundary(int offset) => offset == Text.Length || Array.BinarySearch(StringInfo.ParseCombiningCharacters(Text), offset) >= 0;

	private void Erase(string text)
	{
		foreach (int _ in StringInfo.ParseCombiningCharacters(text)) { erase(); }
	}
}
