namespace HerdrRemote.Pages;

static class ChipRow
{
	// The spoken name of a chip, tab or segment: MAUI has no selected trait, so the state is part of the description. The group (the section a chip filters) follows.
	internal static string? Spoken(string? text, bool on, string? group = null)
	{
		if (string.IsNullOrEmpty(text)) { return null; }
		string name = on ? string.Format(Languages.i18n.Translate("{0}, selected"), text) : text;
		return string.IsNullOrEmpty(group) ? name : $"{name}, {group}";
	}
}
