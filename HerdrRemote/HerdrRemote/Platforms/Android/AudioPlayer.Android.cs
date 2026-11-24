using System.Collections.Concurrent;
using System.Diagnostics;
using Android.Media;
using Handler = Android.OS.Handler;
using Looper = Android.OS.Looper;

namespace HerdrRemote.Services;

// Each Sink streams mono 16-bit PCM at SampleRate through one AudioTrack and long-running feeder thread,
// using a buffer sized from the platform minimum. BeginRead/EndRead share a sink across playback segments; standalone playback gets its own.
// Frame slots bound queued audio until the playback head passes it, so pausing backpressures the producer.
// Short blocking writes wait for Resume rather than busy-looping when paused or stopped.
// Mark each segment's last real frame; if no segment follows, write one silence buffer to flush short tails.
// MarkerReached wakes the feeder to complete the segment; an unpaused StallMs stall ends both segment and sink.
// Main-thread play/pause/flush calls take the sink lock. Only the feeder releases the track, under that lock,
// after a stop, read completion or failure.
public static partial class AudioPlayer
{
	// An unpaused playback head stalled before the segment's end for this long ends the segment and sink.
	private const int StallMs = 3000;

	private static Playback? _current;
	// The open read's sink (null while no read is open or before its first playback).
	private static Sink? _sink;

	// One track and its feeder.
	private sealed class Sink
	{
		public readonly object Gate = new();
		// The segments in order; completed when the sink takes no more (one playback outside a read, the read's end, a stop, a failure).
		public readonly BlockingCollection<Playback> Segments = new();
		// Set while not paused; a short write waits on it.
		public readonly ManualResetEventSlim Resumed = new(true);
		// Set by MarkerReached and by a stop; the feeder waits on it at a segment's end.
		public readonly ManualResetEventSlim Marked = new(false);
		public readonly short[] Silence;
		public AudioTrack? Track;
		public bool Paused;
		public bool Stopped;
		public bool Started;
		// Frames written to the track, the silence included (the marker positions count them).
		public long Written;
		public readonly Queue<(long End, Playback Playback)> Scheduled = new();

		public Sink(AudioTrack track)
		{
			Track = track;
			Silence = new short[track.BufferSizeInFrames];
		}
	}

	private sealed partial class Playback
	{
		public readonly BlockingCollection<short[]> Queue = new();
		public Sink? Sink;
		public TaskCompletionSource? Done;
		public long Frames;
	}

	// MarkerReached runs on the main looper and wakes the feeder waiting at the segment's end.
	private sealed class MarkerListener(Sink sink) : Java.Lang.Object, AudioTrack.IOnPlaybackPositionUpdateListener
	{
		public void OnMarkerReached(AudioTrack? track) => sink.Marked.Set();

		public void OnPeriodicNotification(AudioTrack? track)
		{
			lock (sink.Gate)
			{
				if (sink.Stopped || sink.Track is null) { return; }
				long head = (uint)sink.Track.PlaybackHeadPosition;
				while (sink.Scheduled.TryPeek(out var item) && item.End <= head)
				{
					sink.Scheduled.Dequeue();
					ReleaseSlot(item.Playback);
				}
			}
		}
	}

	internal static partial void PrepareSession() { }

	private static partial Playback Open(TaskCompletionSource done)
	{
		if (_current is { } previous) { StopNative(previous); }
		Playback playback = new() { Done = done };
		_current = playback;
		if (Reading && _sink is { } open && Add(open, playback)) { return playback; }
		Sink sink = NewSink();
		Add(sink, playback);
		if (Reading) { _sink = sink; }
		else { sink.Segments.CompleteAdding(); }
		return playback;
	}

	// Reject new segments after the sink stops or completes adding.
	private static bool Add(Sink sink, Playback playback)
	{
		lock (sink.Gate)
		{
			if (sink.Stopped || sink.Segments.IsAddingCompleted) { return false; }
			playback.Sink = sink;
			sink.Segments.Add(playback);
			return true;
		}
	}

	private static Sink NewSink()
	{
		int minimum = AudioTrack.GetMinBufferSize(SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
		if (minimum <= 0 || minimum > QueueBytes / 2) { throw new InvalidOperationException("Unsupported audio buffer size."); }
		int bufferBytes = (minimum * 2 + 1) & ~1;
		AudioAttributes attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)?.SetContentType(AudioContentType.Speech)?.Build()
			?? throw new InvalidOperationException("AudioAttributes.Builder built nothing.");
		AudioFormat format = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)?.SetSampleRate(SampleRate)?.SetChannelMask(ChannelOut.Mono)?.Build()
			?? throw new InvalidOperationException("AudioFormat.Builder built nothing.");
		AudioTrack track = new AudioTrack.Builder()
			.SetAudioAttributes(attributes)
			.SetAudioFormat(format)
			.SetTransferMode(AudioTrackMode.Stream)
			.SetBufferSizeInBytes(bufferBytes)
			.Build();
		try
		{
			if (_rate != 1)
			{
				using PlaybackParams parameters = new();
				parameters.SetSpeed(_rate);
				parameters.SetPitch(1f);
				track.PlaybackParams = parameters;
				track.Pause();
			}
		}
		catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
		{
			track.Release();
			throw new SpeechStreamException(TtsFailureKind.PlaybackSpeed);
		}
		Sink sink = new(track);
		track.SetPlaybackPositionUpdateListener(new MarkerListener(sink), new Handler(Looper.MainLooper!));
		track.SetPositionNotificationPeriod(ChunkBytes / 2);
		Task.Factory.StartNew(() => Feed(sink), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		return sink;
	}

	private static partial void Write(Playback playback, byte[] pcm)
	{
		if (playback.Sink is not { Stopped: false } || playback.Queue.IsAddingCompleted) { return; }
		short[] samples = new short[pcm.Length / 2];
		Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
		playback.Queue.Add(samples);
	}

	private static partial void End(Playback playback)
	{
		if (!playback.Queue.IsAddingCompleted) { playback.Queue.CompleteAdding(); }
	}

	// The last open read ended: the sink takes no more segments; its feeder releases the track once what it holds has been heard.
	private static partial void ReadEnded()
	{
		if (_sink is not { } sink) { return; }
		_sink = null;
		lock (sink.Gate)
		{
			if (!sink.Segments.IsAddingCompleted) { sink.Segments.CompleteAdding(); }
		}
	}

	public static partial void Pause()
	{
		if (_current?.Sink is not { } sink) { return; }
		lock (sink.Gate)
		{
			if (sink.Stopped || sink.Track is null) { return; }
			sink.Paused = true;
			sink.Resumed.Reset();
			if (sink.Started) { sink.Track.Pause(); }
		}
	}

	public static partial void Resume()
	{
		if (_current?.Sink is not { } sink) { return; }
		lock (sink.Gate)
		{
			if (sink.Stopped || sink.Track is null) { return; }
			sink.Paused = false;
			if (sink.Started) { sink.Track.Play(); }
			sink.Resumed.Set();
		}
	}

	private static partial bool ApplyRate(float rate)
	{
		if ((_current is { Done: not null } current ? current.Sink : _sink) is not { Stopped: false } sink) { return true; }
		lock (sink.Gate)
		{
			if (sink.Stopped || sink.Track is null) { return false; }
			try
			{
				using PlaybackParams parameters = new();
				parameters.SetSpeed(rate);
				parameters.SetPitch(1f);
				sink.Track.PlaybackParams = parameters;
				// Setting nonzero playback parameters can start an AudioTrack; retain the explicit paused state.
				if (sink.Paused) { sink.Track.Pause(); }
				return Math.Abs((sink.Track.PlaybackParams?.Speed ?? 0) - rate) < 0.001f;
			}
			catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
			{
				return false;
			}
		}
	}

	// A playback still going: its sink's track stops (what is not heard yet is dropped) and the feeder releases it; a read's next
	// playback opens a new sink. A playback that has ended changes nothing.
	private static partial void StopNative(Playback playback)
	{
		if (playback.Done is null) { return; }
		playback.Stopped.Cancel();
		if (playback.Sink is { } sink)
		{
			lock (sink.Gate)
			{
				if (!sink.Stopped)
				{
					sink.Stopped = true;
					sink.Scheduled.Clear();
					if (!sink.Segments.IsAddingCompleted) { sink.Segments.CompleteAdding(); }
					try
					{
						sink.Track?.Pause();
						sink.Track?.Flush();
					}
					catch (Java.Lang.IllegalStateException ex)
					{
						Debug.WriteLine($"[AudioPlayer] stop failed: {ex.Message}");
					}
				}
			}
			sink.Resumed.Set();
			sink.Marked.Set();
		}
		if (!playback.Queue.IsAddingCompleted) { playback.Queue.CompleteAdding(); }
	}

	// The feeder: each segment's pieces in order, then its end (the marker, the silence when none follows, the wait); each segment ends
	// once heard. When the sink takes no more, is stopped, stalls or fails, the track is released and what is left ends.
	private static void Feed(Sink sink)
	{
		Exception? error = null;
		Playback? segment = null;
		try
		{
			foreach (Playback next in sink.Segments.GetConsumingEnumerable())
			{
				segment = next;
				if (!Play(sink, segment)) { break; }
				Finish(segment, null);
				segment = null;
			}
		}
		catch (Exception ex)
		{
			error = ex;
		}
		int underruns;
		lock (sink.Gate)
		{
			if (!sink.Segments.IsAddingCompleted) { sink.Segments.CompleteAdding(); }
			underruns = sink.Track?.UnderrunCount ?? 0;
			sink.Track?.Release();
			sink.Track = null;
		}
		Debug.WriteLine($"[AudioPlayer] track end frames {sink.Written} underruns {underruns}");
		if (segment is not null) { Finish(segment, error); }
		foreach (Playback left in sink.Segments.GetConsumingEnumerable()) { Finish(left, error); }
	}

	// One segment: true once it has been heard (or had no audio); false when the sink is stopped or its head stalled.
	private static bool Play(Sink sink, Playback segment)
	{
		foreach (short[] samples in segment.Queue.GetConsumingEnumerable())
		{
			if (!WriteAll(sink, samples, samples.Length)) { return false; }
			segment.Frames += samples.Length;
			sink.Written += samples.Length;
			lock (sink.Gate) { sink.Scheduled.Enqueue((sink.Written, segment)); }
		}
		if (sink.Stopped) { return false; }
		if (segment.Frames == 0) { return true; }
		long end = sink.Written;
		lock (sink.Gate)
		{
			if (sink.Stopped || sink.Track is null) { return false; }
			sink.Marked.Reset();
			sink.Track.SetNotificationMarkerPosition((int)end);
		}
		if (sink.Segments.Count == 0)
		{
			if (!WriteAll(sink, sink.Silence, sink.Silence.Length)) { return false; }
			sink.Written += sink.Silence.Length;
		}
		return WaitHeard(sink, end);
	}

	// Writes count samples; the first write that lands starts the track unless paused. False once the sink is stopped.
	private static bool WriteAll(Sink sink, short[] samples, int count)
	{
		AudioTrack track = sink.Track!;
		int offset = 0;
		while (offset < count)
		{
			if (sink.Stopped) { return false; }
			int written = track.Write(samples, offset, count - offset, WriteMode.Blocking);
			if (written < 0) { throw new InvalidOperationException($"AudioTrack write error {written}."); }
			offset += written;
			lock (sink.Gate)
			{
				if (!sink.Started && !sink.Paused && !sink.Stopped)
				{
					track.Play();
					sink.Started = true;
					Debug.WriteLine("[AudioPlayer] playing");
				}
			}
			if (offset < count) { sink.Resumed.Wait(); }
		}
		return !sink.Stopped;
	}

	// Until the playback head has reached the segment's end (MarkerReached; the head stands while paused): true. False when the sink is
	// stopped, or the head stands still for StallMs while playing (the stall ends the sink; the next playback opens a new one).
	private static bool WaitHeard(Sink sink, long end)
	{
		AudioTrack track = sink.Track!;
		long last = (uint)track.PlaybackHeadPosition;
		if (sink.Stopped) { return false; }
		if (last >= end) { return true; }
		while (true)
		{
			bool marked = sink.Marked.Wait(StallMs);
			if (sink.Stopped) { return false; }
			long head = (uint)track.PlaybackHeadPosition;
			if (head >= end) { return true; }
			if (marked)
			{
				// Ignore a late marker from the previous segment.
				sink.Marked.Reset();
				continue;
			}
			if (head == last && !sink.Paused)
			{
				Debug.WriteLine($"[AudioPlayer] head stalled at {head} of {end}");
				throw new SpeechStreamException(TtsFailureKind.Interrupted);
			}
			last = head;
		}
	}

	// Once per playback: done completes (with the feeder's error, if any).
	private static void Finish(Playback playback, Exception? error)
	{
		if (Interlocked.Exchange(ref playback.Done, null) is not { } done) { return; }
		playback.Stopped.Cancel();
		while (playback.Queue.TryTake(out _)) { }
		Interlocked.CompareExchange(ref _current, null, playback);
		if (error is null) { done.TrySetResult(); } else { done.TrySetException(error); }
	}
}
