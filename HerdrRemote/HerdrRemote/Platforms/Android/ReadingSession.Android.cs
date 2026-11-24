using Android.Content;

namespace HerdrRemote.Services;

internal sealed partial class ReadingSession
{
	private TaskCompletionSource? _mediaReady;
	private long _mediaGeneration;

	partial void MediaStarting(long generation, ref Task ready)
	{
		_mediaGeneration = generation;
		_mediaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ready = _mediaReady.Task;
		Intent intent = new(Platform.AppContext, typeof(ReadingService));
		intent.PutExtra(ReadingService.GenerationExtra, generation);
		try { Platform.AppContext.StartForegroundService(intent); }
		catch (Java.Lang.Exception) { _mediaReady.TrySetException(new SpeechStreamException(TtsFailureKind.Audio)); }
	}

	internal void MediaReady(long generation, bool allowed)
	{
		if (_mediaGeneration != generation) { return; }
		if (allowed) { _mediaReady?.TrySetResult(); }
		else { _mediaReady?.TrySetException(new SpeechStreamException(TtsFailureKind.Interrupted)); }
	}

	partial void MediaChanged(long generation) => ReadingService.Current?.Update(generation);
	partial void MediaResuming(long generation, ref bool allowed) => allowed = ReadingService.Current?.Resume(generation) == true;
	partial void MediaEnded(long generation)
	{
		if (_mediaGeneration != generation) { return; }
		_mediaGeneration = 0;
		_mediaReady?.TrySetCanceled(); _mediaReady = null;
		ReadingService.Current?.Release(generation);
	}
}
