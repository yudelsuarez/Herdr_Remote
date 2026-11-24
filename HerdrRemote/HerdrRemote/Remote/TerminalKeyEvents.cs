using System.Text;

namespace HerdrRemote.Remote;

internal static class TerminalKeyEvents
{
	internal static string Hardware(string key, bool control, bool alt, bool shift) =>
		(control ? "ctrl+" : "") + (alt ? "alt+" : "") + (shift ? "shift+" : "") + key;

	internal static void Move(int steps, Action<string> key)
	{
		for (int i = 0; i < Math.Abs(steps); i++) { key(steps < 0 ? "left" : "right"); }
	}

	internal static void Insert(string typed, Func<string, string?> modify, Action<string> text, Action<string> key)
	{
		StringBuilder plain = new();
		bool afterReturn = false;
		foreach (Rune rune in typed.EnumerateRunes())
		{
			if (rune.Value == '\n' && afterReturn) { afterReturn = false; continue; }
			afterReturn = rune.Value == '\r';
			string? named = rune.Value switch { '\r' or '\n' => "enter", '\t' => "tab", _ => null };
			string character = named ?? rune.ToString();
			named = modify(character) ?? named;
			if (named is null) { plain.Append(character); continue; }
			if (plain.Length > 0) { text(plain.ToString()); plain.Clear(); }
			key(named);
		}
		if (plain.Length > 0) { text(plain.ToString()); }
	}
}
