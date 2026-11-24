using Android.Content;
using Android.Webkit;

namespace HerdrRemote.Services;

// Read original image bytes from primary-clip content URIs with an image/* MIME type,
// as supplied by photo apps, screenshot editors and browsers.
public static partial class ClipboardImage
{
	private static ClipboardManager? Manager => Platform.AppContext.GetSystemService(Context.ClipboardService) as ClipboardManager;

	public static partial (bool Text, bool Image) Kinds()
	{
		if (Manager?.PrimaryClipDescription is not { } description) { return (false, false); }
		bool text = description.HasMimeType(ClipDescription.MimetypeTextPlain) || description.HasMimeType(ClipDescription.MimetypeTextHtml);
		return (text, description.HasMimeType("image/*"));
	}

	public static partial async Task<(byte[] Data, string Extension)?> ReadAsync()
	{
		if (Manager?.PrimaryClip is not { } clip || Platform.AppContext.ContentResolver is not { } resolver) { return null; }
		for (int i = 0; i < clip.ItemCount; i++)
		{
			if (clip.GetItemAt(i)?.Uri is not { } uri || resolver.GetType(uri) is not { } type || !type.StartsWith("image/", StringComparison.Ordinal)) { continue; }
			await using Stream? input = resolver.OpenInputStream(uri);
			if (input is null) { continue; }
			using MemoryStream data = new();
			await input.CopyToAsync(data);
			string extension = MimeTypeMap.Singleton?.GetExtensionFromMimeType(type) is { Length: > 0 } known ? known : "png";
			return (data.ToArray(), extension);
		}
		return null;
	}
}
