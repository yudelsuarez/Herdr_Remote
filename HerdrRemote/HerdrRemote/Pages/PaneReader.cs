using System.Text;
using System.Text.RegularExpressions;

namespace HerdrRemote.Pages;

// Split herdr-core terminal.read text into cleaned paragraphs, matching terminal.ts blockAt.
// Rows without letters or digits (blank lines, rules, or fillers such as vim's "~") end paragraphs and are
// not spoken. List items, prompts, and message markers start paragraphs; remove their markers, markdown,
// and box characters. Join wrapped rows, omitting the space after "-" when the next row starts lowercase.
internal static partial class PaneText
{
	// Every paragraph of the text, in order.
	public static List<string> Paragraphs(string text) => [.. Split(text).Where(paragraph => paragraph.Length > 0)];

	// Speech pieces with paragraph indices, matching the web reader's range planner.
	public static List<(string Text, int Paragraph)> Sentences(IReadOnlyList<string> paragraphs)
	{
		List<(string Text, int Paragraph)> sentences = [];
		for (int p = 0; p < paragraphs.Count; p++)
		{
			string text = Collapse(paragraphs[p]);
			foreach ((int start, int end) in Pieces(text, sentences.Count == 0)) { sentences.Add((text[start..end].Trim(), p)); }
		}
		return sentences;
	}

	// The Reader sends one block straight to Sentences. Match only at the next source position, undoing Collapse's whitespace.
	public static (int Start, int Length)?[] SourceRanges(string source, IReadOnlyList<(string Text, int Paragraph)> sentences)
	{
		var ranges = new (int Start, int Length)?[sentences.Count];
		int at = 0;
		void SkipSpace() { while (at < source.Length && char.IsWhiteSpace(source[at])) { at++; } }
		for (int i = 0; i < sentences.Count; i++)
		{
			SkipSpace();
			int start = at;
			foreach (char expected in sentences[i].Text)
			{
				if (at == source.Length) { return ranges; }
				if (expected == ' ' && char.IsWhiteSpace(source[at])) { SkipSpace(); }
				else if (expected == source[at]) { at++; }
				else { return ranges; } // Without this boundary, later positions are not proven either.
			}
			if (at > start) { ranges[i] = (start, at - start); }
		}
		return ranges;
	}

	private static List<(int Start, int End)> Pieces(string text, bool first)
	{
		List<(int Start, int End)> raw = [];
		int start = 0;
		foreach (Match gap in SentenceEnd().Matches(text))
		{
			string before = text[start..gap.Index];
			if (!TerminalEnd().IsMatch(before) || Abbreviation().IsMatch(before)) { continue; }
			raw.Add((start, gap.Index));
			start = gap.Index + gap.Length;
		}
		raw.Add((start, text.Length));
		List<(int Start, int End)> joined = [];
		(int Start, int End)? pending = null;
		foreach (var piece in raw)
		{
			if (piece.End <= piece.Start) { continue; }
			pending = (pending?.Start ?? piece.Start, piece.End);
			if (pending.Value.End - pending.Value.Start >= 20) { joined.Add(pending.Value); pending = null; }
		}
		if (pending is { } last)
		{
			if (joined.Count > 0) { joined[^1] = (joined[^1].Start, last.End); } else { joined.Add(last); }
		}
		List<(int Start, int End)> pieces = [];
		for (int group = 0; group < joined.Count; group++)
		{
			var (a, b) = joined[group];
			List<int> cuts = [];
			if (first && group == 0)
			{
				MatchCollection words = PieceWords().Matches(text[a..b]);
				if (words.Count > 10)
				{
					Match tenth = words[9];
					int after = a + tenth.Index + tenth.Value.TrimEnd(',', ';', ':', '—', '–', '-').Length;
					for (int i = after; i < b; i++) { if (PieceMark(text, i)) { cuts.Add(i + 1); break; } }
				}
			}
			cuts.Add(b);
			int from = a;
			foreach (int end in cuts)
			{
				while (end - from > 250)
				{
					int cut = -1;
					for (int i = from; i < from + 250; i++) { if (PieceMark(text, i)) { cut = i + 1; } }
					if (cut < 0) { for (int i = from + 249; i > from; i--) { if (char.IsWhiteSpace(text[i])) { cut = i; break; } } }
					if (cut <= from) { break; }
					pieces.Add((from, cut));
					from = cut;
					while (from < end && char.IsWhiteSpace(text[from])) { from++; }
				}
				if (end > from) { pieces.Add((from, end)); }
				from = end;
				while (from < b && char.IsWhiteSpace(text[from])) { from++; }
			}
		}
		return pieces;
	}

	private static bool PieceMark(string text, int at) => text[at] is ',' or ';' or ':' or '—' or '–'
		|| (text[at] == '-' && at > 0 && at + 1 < text.Length && char.IsWhiteSpace(text[at - 1]) && char.IsWhiteSpace(text[at + 1]));

	// The paragraphs, each cleaned.
	private static List<string> Split(string text)
	{
		List<string> paragraphs = [];
		StringBuilder current = new();
		foreach (string raw in text.Split('\n'))
		{
			// Box characters and image placeholder cells out first, so a row holding only a panel's bars or an image is blank, as it is in
			// a marked panel.
			string row = Box().Replace(Placeholder().Replace(raw.TrimEnd('\r'), " "), " ");
			bool blank = !Word().IsMatch(row);
			if ((blank || Opener().IsMatch(row)) && current.Length > 0)
			{
				paragraphs.Add(Collapse(current.ToString()));
				current.Clear();
			}
			if (blank) { continue; }
			string line = Markdown().Replace(Lead().Replace(row, ""), "").Trim();
			if (current.Length > 0 && !(current[^1] == '-' && line.Length > 0 && char.IsLower(line[0]))) { current.Append(' '); }
			current.Append(line);
		}
		if (current.Length > 0) { paragraphs.Add(Collapse(current.ToString())); }
		return paragraphs;
	}

	private static string Collapse(string text) => Space().Replace(text, " ").Trim();

	[GeneratedRegex(@"[\p{L}\p{N}]")]
	private static partial Regex Word();

	[GeneratedRegex(@"^\s*([❯›>⏺●•◦▪*+-]|\d+[.)])\s")]
	private static partial Regex Opener();

	[GeneratedRegex(@"^\s*([❯›>⏺●•◦▪*+-]|\d+[.)])\s+")]
	private static partial Regex Lead();

	[GeneratedRegex(@"\*\*|__|`|^#+\s")]
	private static partial Regex Markdown();

	[GeneratedRegex(@"[│▎┃║▏▕─━═╭╮╰╯]")]
	private static partial Regex Box();

	// A kitty image placeholder cell: U+10EEEE (a surrogate pair) with its row, column and id diacritics (combining marks; the table's
	// ones above U+FFFF, U+10A0F, U+10A38 and U+1D185-U+1D244, are surrogate pairs on D802 and D834).
	[GeneratedRegex(@"􎻮(?:\p{Mn}|[\uD802\uD834][\uDC00-\uDFFF])*")]
	private static partial Regex Placeholder();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Space();

	[GeneratedRegex(@"\s+(?=[\p{Lu}\d¿¡""“'(])")]
	private static partial Regex SentenceEnd();

	[GeneratedRegex(@"[.!?…][""”')\]]*$")]
	private static partial Regex TerminalEnd();

	[GeneratedRegex(@"\b(?:mr|mrs|ms|dr|dra|prof|sr|sra|jr|st|vs|etc|e\.g|i\.e|p\.ej)\.[""”')\]]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Abbreviation();

	[GeneratedRegex(@"\S+")]
	private static partial Regex PieceWords();
}
