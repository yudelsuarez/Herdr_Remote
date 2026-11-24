using Foundation;
using Photos;

namespace HerdrRemote.Services;

// Create a photo asset with add-only permission; iOS prompts once using NSPhotoLibraryAddUsageDescription.
// The supplied filename is not retained because iOS names its assets.
public static partial class PhotoLibrary
{
	public static partial async Task SaveAsync(byte[] png, string name)
	{
		PHAuthorizationStatus status = await PHPhotoLibrary.RequestAuthorizationAsync(PHAccessLevel.AddOnly);
		if (status is not (PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited)) { throw new UnauthorizedAccessException("Adding photos is not allowed."); }
		TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
			() => PHAssetCreationRequest.CreationRequestForAsset().AddResource(PHAssetResourceType.Photo, NSData.FromArray(png), null),
			(ok, error) =>
			{
				if (ok) { saved.TrySetResult(); }
				else { saved.TrySetException(new IOException(error?.LocalizedDescription ?? "The photo was not saved.")); }
			});
		await saved.Task;
	}
}
