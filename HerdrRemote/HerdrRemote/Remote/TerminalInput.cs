using System.Globalization;
using System.Text;

namespace HerdrRemote.Remote;

// Input modes tracked by xterm.js from the output stream: application cursor keys (DECCKM), bracketed paste (2004),
// mouse reporting ("none", "x10", "vt200", "drag", "any"), SGR mouse encoding (1006), and kitty keyboard flags for the
// currently displayed screen (CSI = / > / < u).
internal sealed record TerminalModes(bool AppCursor, bool BracketedPaste, string Mouse, bool Sgr, int Kitty)
{
	public static readonly TerminalModes Default = new(false, false, "none", false, 0);
}

// Encode phone keys, pastes and taps as xterm does for the program's current modes. With kitty keyboard disambiguation
// enabled (pi, Claude Code), Escape and modified keys use CSI u (sw.kovidgoyal.net/kitty/keyboard-protocol).
// Keep encoding here: the core does not encode stream input.
internal static class TerminalInput
{
	// Accept named keys ("esc", "tab", "enter", "backspace", arrows, "home", "end") or a single character.
	// Prefix key-bar modifiers in ctrl+, alt+, shift+ order, for example "ctrl+c", "alt+up" or "ctrl+shift+x".
	public static byte[] Key(string named, TerminalModes modes)
	{
		// Read repeated modifier prefixes in any order, including "shift+tab" from the key bar. The remainder is the key.
		bool ctrl = false, alt = false, shift = false;
		string key = named;
		while (true)
		{
			if (key.StartsWith("ctrl+", StringComparison.Ordinal) && key.Length > 5) { (ctrl, key) = (true, key[5..]); }
			else if (key.StartsWith("alt+", StringComparison.Ordinal) && key.Length > 4) { (alt, key) = (true, key[4..]); }
			else if (key.StartsWith("shift+", StringComparison.Ordinal) && key.Length > 6) { (shift, key) = (true, key[6..]); }
			else { break; }
		}
		// xterm modifier parameter: 1 + shift (1) + alt (2) + ctrl (4).
		int mods = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
		bool kitty = (modes.Kitty & 1) != 0;
		char arrow = key switch { "up" => 'A', "down" => 'B', "right" => 'C', "home" => 'H', "end" => 'F', _ => 'D' };
		// Match xterm Ctrl mappings: a-z and @ A-Z [ \ ] ^ _ map to 0-31, space to 0, and ? to DEL. Leave other characters unchanged.
		string typed = shift ? key.ToUpperInvariant() : key;
		string control = typed switch
		{
			[char c] when c is >= 'a' and <= 'z' => ((char)(c - 'a' + 1)).ToString(),
			[char c] when c is >= '@' and <= '_' => ((char)(c - '@')).ToString(),
			" " => "\0",
			"?" => "\x7f",
			_ => typed,
		};
		string sent = key switch
		{
			"up" or "down" or "right" or "left" or "home" or "end" => mods > 1 ? $"\e[1;{mods}{arrow}" : modes.AppCursor ? $"\eO{arrow}" : $"\e[{arrow}",
			"enter" => mods == 1 ? "\r" : kitty ? $"\e[13;{mods}u" : alt ? "\e\r" : "\r",
			"tab" => mods == 1 ? "\t" : kitty ? $"\e[9;{mods}u" : shift ? "\e[Z" : alt ? "\e\t" : "\t",
			"backspace" => mods == 1 ? "\x7f" : kitty ? $"\e[127;{mods}u" : ctrl ? "\b" : alt ? "\e\x7f" : "\x7f",
			"delete" => mods == 1 ? "\e[3~" : $"\e[3;{mods}~",
			"esc" => kitty ? mods == 1 ? "\e[27u" : $"\e[27;{mods}u" : alt ? "\e\e" : "\e",
			_ when key.Length == 0 => "",
			// kitty encodes Ctrl/Alt character combinations as CSI u using the first unshifted code point, including emoji.
			// Shift alone sends text.
			_ when kitty && (ctrl || alt) && Rune.TryGetRuneAt(key.ToLowerInvariant(), 0, out Rune point) => $"\e[{point.Value.ToString(CultureInfo.InvariantCulture)};{mods}u",
			_ => (alt ? "\e" : "") + (ctrl ? control : typed),
		};
		return Encoding.UTF8.GetBytes(sent);
	}

	// Send typed text unchanged. For pastes, normalize line breaks to Enter (\r). When bracketed-paste mode (2004) is enabled,
	// wrap the paste in markers and remove ESC from its payload so it cannot terminate the paste early.
	public static byte[] Text(string text, bool paste, TerminalModes modes)
	{
		if (!paste) { return Encoding.UTF8.GetBytes(text); }
		string lines = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
		return Encoding.UTF8.GetBytes(modes.BracketedPaste ? $"\e[200~{lines.Replace("\e", "", StringComparison.Ordinal)}\e[201~" : lines);
	}

	// Match Herdr's desktop file paste: one destination-shell word, a space, and no Enter.
	internal static byte[] Attachment(string path, TerminalModes modes)
	{
		bool windows = RemotePath.IsWindows(path);
		string word = path.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+=:@%~".Contains(c) || (!windows && c == ','))
			? path : windows ? "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'" : SshRemoteCommands.Quote(path);
		return Text(word + " ", true, modes);
	}

	// Encode a left click (press then release) or wheel step (64 up, 65 down; press only) at a zero-based cell.
	// Use SGR 1006 when requested, otherwise xterm's original byte encoding. Return null if mouse reporting is disabled.
	public static byte[]? Mouse(int button, int count, int column, int row, TerminalModes modes)
	{
		if (modes.Mouse == "none") { return null; }
		if (button == 0 && (column < 0 || row < 0 || (!modes.Sgr && (column > 222 || row > 222)))) { return null; }
		StringBuilder sent = new();
		for (int i = 0; i < count; i++)
		{
			if (modes.Sgr)
			{
				sent.Append(CultureInfo.InvariantCulture, $"\e[<{button};{column + 1};{row + 1}M");
				if (button == 0 && modes.Mouse != "x10") { sent.Append(CultureInfo.InvariantCulture, $"\e[<0;{column + 1};{row + 1}m"); }
			}
			else
			{
				// Legacy xterm encoding: one byte each for 32 + button, 32 + one-based column and 32 + one-based row;
				// columns and rows are limited to 223.
				sent.Append("\e[M").Append((char)(32 + button)).Append((char)(33 + Math.Min(column, 222))).Append((char)(33 + Math.Min(row, 222)));
				if (button == 0 && modes.Mouse != "x10") { sent.Append("\e[M").Append((char)35).Append((char)(33 + Math.Min(column, 222))).Append((char)(33 + Math.Min(row, 222))); }
			}
		}
		return Encoding.Latin1.GetBytes(sent.ToString());
	}
}
