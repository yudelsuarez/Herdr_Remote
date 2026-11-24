using Foundation;
using UIKit;

namespace HerdrRemote.Services;

// Read JPEG and PNG bytes unchanged from the general pasteboard; convert other images, such as HEIC, to PNG.
public static partial class ClipboardImage
{
	public static partial (bool Text, bool Image) Kinds() => (UIPasteboard.General.HasStrings, UIPasteboard.General.HasImages);

	internal static (byte[] Data, string Extension)? ReadNative()
	{
		UIPasteboard pasteboard = UIPasteboard.General;
		(byte[], string)? image = null;
		if (pasteboard.HasImages)
		{
			if (pasteboard.DataForPasteboardType("public.jpeg") is { Length: > 0 } jpeg) { image = (jpeg.ToArray(), "jpg"); }
			else if (pasteboard.DataForPasteboardType("public.png") is { Length: > 0 } png) { image = (png.ToArray(), "png"); }
			else if (pasteboard.Image?.AsPNG() is { Length: > 0 } drawn) { image = (drawn.ToArray(), "png"); }
		}
		return image;
	}
}
