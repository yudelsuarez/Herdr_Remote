namespace HerdrRemote.Services;

// Provides clipboard text and images for terminal paste. MAUI handles text; platform implementations in
// Platforms/<os>/ClipboardImage.<os>.cs handle images. Checking content types reads no content and triggers no iOS paste prompt.
public static partial class ClipboardImage
{
	// Both flags can be true: an image copied from a web page can include its address as text.
	public static partial (bool Text, bool Image) Kinds();

	// Returns image file bytes and their extension (jpg, png, etc.), or null if the clipboard has no image.
#if ANDROID
	public static partial Task<(byte[] Data, string Extension)?> ReadAsync();
#endif
}
