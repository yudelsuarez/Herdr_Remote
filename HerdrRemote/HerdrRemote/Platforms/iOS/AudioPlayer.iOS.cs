using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;

namespace HerdrRemote.Services;

// Reuse one engine and player node, connected once with mono float buffers at SampleRate;
// buffer rates must match the node's output format. Schedule pieces in order and finish after the last plays.
// stop() also invokes pending buffer callbacks; ignore callbacks for completed playback.
// Keep the engine running within a read, but stop and reset it between standalone playbacks and after a read
// to release hardware and buffered audio. Restart after configuration changes; ReadingSession handles interruptions and routes.
public static partial class AudioPlayer
{
	private static AVAudioEngine? _engine;
	private static AVAudioPlayerNode? _node;
	private static AVAudioUnitTimePitch? _timePitch;
	private static AVAudioFormat? _format;
	private static Playback? _current;

	private sealed partial class Playback
	{
		public TaskCompletionSource? Done;
		public int Pending;
		public bool Ended;
		public bool Paused;
	}

	// Playback category: the ring/silent switch does not mute the reader.
	internal static partial void PrepareSession()
	{
		AVAudioSession session = AVAudioSession.SharedInstance();
		NSError? error = session.SetCategory(AVAudioSessionCategory.Playback);
		if (error is null) { session.SetActive(true, out error); }
		if (error is not null) { throw new SpeechStreamException(TtsFailureKind.Audio); }
	}

	private static partial Playback Open(TaskCompletionSource done)
	{
		if (_current is { } previous) { StopNative(previous); }
		(AVAudioEngine engine, AVAudioPlayerNode node) = Engine();
		Playback playback = new() { Done = done };
		_current = playback;
		if (!engine.Running && !engine.StartAndReturnError(out NSError? error))
		{
			Finish(playback, new InvalidOperationException(error?.LocalizedDescription ?? "AVAudioEngine did not start."));
			return playback;
		}
		Play(playback, node);
		return playback;
	}

	// iOS 27 replaced play() with a call that reports its error.
	private static void Play(Playback playback, AVAudioPlayerNode node)
	{
		if (OperatingSystem.IsIOSVersionAtLeast(27) || OperatingSystem.IsMacCatalystVersionAtLeast(27))
		{
			if (!node.Play(out _))
			{
				Finish(playback, new SpeechStreamException(TtsFailureKind.Interrupted));
				node.Stop();
			}
		}
		else
		{
			node.Play();
		}
	}

	private static partial void Write(Playback playback, byte[] pcm)
	{
		if (playback.Done is null || playback.Ended || _node is not { } node || _format is not { } format) { return; }
		int frames = pcm.Length / 2;
		if (frames == 0) { return; }
		ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, frames * 2));
		AVAudioPcmBuffer buffer = new(format, (uint)frames) { FrameLength = (uint)frames };
		// Write the mono channel in place through a native-memory reference, without unsafe blocks.
		Span<float> channel = MemoryMarshal.CreateSpan(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<float>(), Marshal.ReadIntPtr(buffer.FloatChannelData)), frames);
		for (int i = 0; i < frames; i++) { channel[i] = samples[i] / 32768f; }
		playback.Pending++;
		node.ScheduleBuffer(buffer, AVAudioPlayerNodeCompletionCallbackType.PlayedBack, _ => MainThread.BeginInvokeOnMainThread(() =>
		{
			buffer.Dispose();
			PlayedBack(playback);
		}));
	}

	private static partial void End(Playback playback)
	{
		playback.Ended = true;
		if (playback.Pending == 0) { Finish(playback, null); }
	}

	public static partial void Pause()
	{
		if (_current is not { } playback || _node is not { } node) { return; }
		playback.Paused = true;
		node.Pause();
	}

	public static partial void Resume()
	{
		if (_current is not { } playback || _node is not { } node) { return; }
		playback.Paused = false;
		Play(playback, node);
	}

	private static partial bool ApplyRate(float rate)
	{
		if (_timePitch is { } pitch) { pitch.Rate = rate; return Math.Abs(pitch.Rate - rate) < 0.001f; }
		return true;
	}

	private static partial void StopNative(Playback playback)
	{
		if (playback.Done is null) { return; }
		if (_current == playback) { _node?.Stop(); }
		Finish(playback, null);
	}

	// Create and connect the engine once, then observe configuration changes for its lifetime.
	private static (AVAudioEngine Engine, AVAudioPlayerNode Node) Engine()
	{
		if (_engine is { } engine && _node is { } node) { return (engine, node); }
		engine = new AVAudioEngine();
		node = new AVAudioPlayerNode();
		_format = new AVAudioFormat(SampleRate, 1);
		engine.AttachNode(node);
		AVAudioUnitTimePitch pitch = new() { Pitch = 0, Rate = _rate };
		engine.AttachNode(pitch);
		// iOS 27 replaced connect() with a call that reports its error.
		if (OperatingSystem.IsIOSVersionAtLeast(27) || OperatingSystem.IsMacCatalystVersionAtLeast(27))
		{
			if (!engine.Connect(node, pitch, _format, out NSError? error) || !engine.Connect(pitch, engine.MainMixerNode, _format, out error))
			{
				throw new InvalidOperationException("Audio engine connection failed.");
			}
		}
		else
		{
			engine.Connect(node, pitch, _format);
			engine.Connect(pitch, engine.MainMixerNode, _format);
		}
		_engine = engine;
		_node = node;
		_timePitch = pitch;
		AVAudioEngine.Notifications.ObserveConfigurationChange((_, _) => MainThread.BeginInvokeOnMainThread(ConfigurationChanged));
		return (engine, node);
	}

	// Route changes can stop the engine; restart active playback while preserving its paused state.
	private static void ConfigurationChanged()
	{
		if (_current is not { Done: not null } playback || _engine is not { } engine || _node is not { } node) { return; }
		if (!engine.StartAndReturnError(out NSError? error))
		{
			Finish(playback, new SpeechStreamException(TtsFailureKind.Interrupted));
			node.Stop();
			return;
		}
		if (!playback.Paused) { Play(playback, node); }
	}

	// A played or unscheduled buffer releases its slot; finish once all buffers of an ended playback are accounted for.
	private static void PlayedBack(Playback playback)
	{
		if (playback.Done is null) { return; }
		playback.Pending--;
		ReleaseSlot(playback);
		if (playback.Ended && playback.Pending == 0) { Finish(playback, null); }
	}

	// Once per playback; the engine is stopped while nothing plays, except between the playbacks of an open read.
	private static void Finish(Playback playback, Exception? error)
	{
		if (playback.Done is not { } done) { return; }
		playback.Done = null;
		playback.Stopped.Cancel();
		if (_current == playback)
		{
			_current = null;
			if (!Reading) { StopEngine(); }
		}
		if (error is null) { done.TrySetResult(); } else { done.TrySetException(error); }
	}

	// Stop an idle engine when the read ends or waits for text; active playback stops it when finished.
	private static partial void ReadEnded()
	{
		if (_current is null) { StopEngine(); }
	}

	// Stop and reset, never pause: the time-pitch unit otherwise retains audio that clicks at the next read's start,
	// and the audio hardware stays busy, preventing session deactivation.
	private static void StopEngine()
	{
		if (_engine is not { } engine) { return; }
		_node?.Stop();
		if (engine.Running) { engine.Stop(); }
		engine.Reset();
		Debug.WriteLine("[AudioPlayer] engine stopped");
	}
}
