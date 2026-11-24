using Languages;

namespace HerdrRemote.Pages;

// The show/hide toggle of a secret field: the eye label flips the entry between dots and text, and keeps the caret at the end.
internal static class SecretEye
{
	public static void Attach(Entry entry, Label eye)
	{
		Show(entry, eye);
		TapGestureRecognizer tap = new();
		tap.Tapped += (_, _) =>
		{
			entry.IsPassword = !entry.IsPassword;
			Show(entry, eye);
			entry.CursorPosition = entry.Text?.Length ?? 0;
		};
		eye.GestureRecognizers.Add(tap);
	}

	// The icon and accessibility description name the action: E920 (eye) reveals hidden text;
	// E921 (eye off) hides visible text.
	private static void Show(Entry entry, Label eye)
	{
		eye.Text = entry.IsPassword ? "\uE920" : "\uE921";
		SemanticProperties.SetDescription(eye, i18n.Translate(entry.IsPassword ? "Show password" : "Hide password"));
	}
}
