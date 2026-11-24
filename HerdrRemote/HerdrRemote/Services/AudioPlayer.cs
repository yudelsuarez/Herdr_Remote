using System.Diagnostics;

namespace HerdrRemote.Services;

// Streams signed 16-bit little-endian mono PCM at SampleRate through Android AudioTrack or iOS AVAudioEngine.
// Only one playback runs at a time; starting another stops the previous one. Platform implementations are in
// Platforms/<os>/AudioPlayer.<os>.cs. PlayAsync, Open, Write, End, StopNative, Pause, Resume, BeginRead and EndRead run on the main thread.
public static partial class AudioPlayer
{
	internal const int SampleRate = 24000;
	internal const int ChunkBytes = 8000;
	internal const int QueueBytes = SampleRate * 2 * 2;
	private static float _rate = 1;
	public static bool SetRate(float rate)
	{
		if (rate is not (0.8f or 1f or 1.25f or 1.5f) || !ApplyRate(rate)) { return false; }
		_rate = rate;
		return true;
	}
	private static partial bool ApplyRate(float rate);

	// Plays incoming chunks and invokes playing on the first chunk. Completes after playback drains, or throws
	// OperationCanceledException when ct cancels. Cleanup waits for this playback to end before rethrowing a source failure.
	// A source with no audio throws InvalidOperationException.
	internal static async Task PlayAsync(IAsyncEnumerable<byte[]> pcm, Action playing, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Playback playback = Open(done);
		using CancellationTokenSource capacity = CancellationTokenSource.CreateLinkedTokenSource(ct, playback.Stopped.Token);
		using CancellationTokenRegistration stop = ct.Register(() => MainThread.BeginInvokeOnMainThread(() => StopNative(playback)));
		bool any = false;
		try
		{
			await foreach (byte[] piece in pcm.WithCancellation(ct))
			{
				if (!any)
				{
					any = true;
					playing();
				}
				for (int offset = 0; offset < piece.Length; offset += ChunkBytes)
				{
					await playback.Slots.WaitAsync(capacity.Token);
					ct.ThrowIfCancellationRequested();
					int length = Math.Min(ChunkBytes, piece.Length - offset);
					Write(playback, offset == 0 && length == piece.Length ? piece : piece.AsSpan(offset, length).ToArray());
				}
			}
		}
		catch
		{
			StopNative(playback);
			try { await done.Task; }
			catch (Exception ex) when (ct.IsCancellationRequested) { Debug.WriteLine($"[AudioPlayer] stopped playback failed: {ex.GetType().Name}"); }
			throw;
		}
		End(playback);
		await done.Task;
		if (!any) { throw new InvalidOperationException("The reply had no audio."); }
	}

	public static partial void Pause();
	public static partial void Resume();

	// A session's chunks share one native player; a replacement waits for the previous session to release it.
	private static int _reads;

	private static bool Reading => _reads > 0;

	internal static void BeginRead() => _reads++;

	internal static void EndRead()
	{
		if (_reads == 0) { return; }
		_reads--;
		if (_reads == 0) { ReadEnded(); }
	}

	// Releases the shared player while a read waits for more text, just as at the end of a read.
	// The next playback reopens it, so waiting retains no audio resources.
	internal static void Idle()
	{
		if (Reading) { ReadEnded(); }
	}

	// Releases the player after the last read: releases Android's track, or stops and resets the iOS engine.
	private static partial void ReadEnded();

	// Prepares the reading audio session; iOS playback works even with the silent switch on.
	internal static partial void PrepareSession();

	// Native state for one playback. Write, End and StopNative affect only this playback and do nothing after it ends.
	private sealed partial class Playback
	{
		public readonly SemaphoreSlim Slots = new(QueueBytes / ChunkBytes, QueueBytes / ChunkBytes);
		public readonly CancellationTokenSource Stopped = new();
	}
	private static void ReleaseSlot(Playback playback)
	{
		if (!playback.Stopped.IsCancellationRequested) { playback.Slots.Release(); }
	}

	private static partial Playback Open(TaskCompletionSource done);
	private static partial void Write(Playback playback, byte[] pcm);
	// Completes done after all queued audio has played.
	private static partial void End(Playback playback);
	// Discards queued audio and completes done immediately.
	private static partial void StopNative(Playback playback);
}
