using Android.Content;
using Android.Provider;

namespace HerdrRemote.Services;

// Save to the shared images collection under Pictures/Remote Shell. Android 10+ allows adding
// app-created images without storage permission; the app requires Android 13+.
public static partial class PhotoLibrary
{
	public static partial async Task SaveAsync(byte[] png, string name)
	{
		ContentValues values = new();
		values.Put(MediaStore.IMediaColumns.DisplayName, name);
		values.Put(MediaStore.IMediaColumns.MimeType, "image/png");
		values.Put(MediaStore.IMediaColumns.RelativePath, $"{Android.OS.Environment.DirectoryPictures}/Remote Shell");
		ContentResolver resolver = Platform.AppContext.ContentResolver ?? throw new IOException("No content resolver.");
		Android.Net.Uri uri = resolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values) ?? throw new IOException("The photo could not be created.");
		await using Stream output = resolver.OpenOutputStream(uri) ?? throw new IOException("The photo could not be opened.");
		await output.WriteAsync(png);
	}
}
