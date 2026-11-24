namespace HerdrRemote.Services;

// Saves images from the image viewer to the photo library. Platform implementations are in Platforms/<os>/PhotoLibrary.<os>.cs.
public static partial class PhotoLibrary
{
	// Saves the PNG as a new photo with the given name; throws if saving is denied, including missing iOS add-photo permission.
	public static partial Task SaveAsync(byte[] png, string name);
}
