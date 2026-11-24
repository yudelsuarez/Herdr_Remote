using System.Diagnostics;
using AVFoundation;
using Foundation;

namespace HerdrRemote.Services;

// The microphone on iOS: one AVAudioEngine with a tap on its input node, shared by the on-device recognizer and the cloud recorder. The
// audio session is PlayAndRecord while it runs. At the end the session goes back to Playback when a reading is still held (paused), else
// it is deactivated with NotifyOthersOnDeactivation. Start and Stop run on the main thread; the tap's callback runs on the audio thread.
internal sealed class MicEngine
{
	private AVAudioEngine? _engine;
	private NSObject? _interruption;
	private long _levelAt;

	// The whole mic stopped by the system (a call, Siri): dictation ends and keeps its text.
	public event Action? Interrupted;

	public bool Start(Action<AVAudioPcmBuffer, float> buffer)
	{
		AVAudioSession session = AVAudioSession.SharedInstance();
		NSError? error = session.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth);
		if (error is null) { session.SetActive(true, out error); }
		if (error is not null) { Debug.WriteLine($"[Dictation] audio session failed: {error.Code}"); return false; }
		AVAudioEngine engine = new();
		AVAudioFormat format = engine.InputNode.GetBusOutputFormat(0);
		if (format.SampleRate <= 0 || format.ChannelCount == 0) { Release(); return false; }
		AVAudioNodeTapBlock tap = (pcm, _) =>
		{
			float level = 0;
			long now = Environment.TickCount64;
			if (now - _levelAt >= 100)
			{
				_levelAt = now;
				level = Level(pcm);
			}
			buffer(pcm, level);
		};
		// iOS 27 replaced the tap call with one that reports its error.
		if (OperatingSystem.IsIOSVersionAtLeast(27))
		{
			if (!engine.InputNode.InstallTapOnBus(0, 4096, format, out NSError? tapError, tap))
			{
				Debug.WriteLine($"[Dictation] audio tap failed: {tapError?.Code}");
				engine.Dispose();
				Release();
				return false;
			}
		}
		else { engine.InputNode.InstallTapOnBus(0, 4096, format, tap); }
		engine.Prepare();
		if (!engine.StartAndReturnError(out NSError? startError))
		{
			Debug.WriteLine($"[Dictation] audio engine failed: {startError?.Code}");
			engine.InputNode.RemoveTapOnBus(0);
			Release();
			return false;
		}
		_engine = engine;
		_interruption = AVAudioSession.Notifications.ObserveInterruption((_, e) =>
		{
			if (e.InterruptionType == AVAudioSessionInterruptionType.Began) { MainThread.BeginInvokeOnMainThread(() => Interrupted?.Invoke()); }
		});
		return true;
	}

	public void Stop()
	{
		_interruption?.Dispose();
		_interruption = null;
		if (_engine is { } engine)
		{
			_engine = null;
			engine.InputNode.RemoveTapOnBus(0);
			engine.Stop();
			engine.Dispose();
		}
		Release();
	}

	private static void Release()
	{
		AVAudioSession session = AVAudioSession.SharedInstance();
		if (App.Reading.IsReading)
		{
			session.SetCategory(AVAudioSessionCategory.Playback);
			session.SetActive(true, out _);
		}
		else { session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _); }
	}

	// 0..1 from the buffer's first channel (the input node's format is float32).
	private static float Level(AVAudioPcmBuffer pcm)
	{
		int frames = (int)pcm.FrameLength;
		if (frames == 0 || pcm.FloatChannelData == IntPtr.Zero) { return 0; }
		IntPtr channel = System.Runtime.InteropServices.Marshal.ReadIntPtr(pcm.FloatChannelData);
		if (channel == IntPtr.Zero) { return 0; }
		float[] samples = new float[frames];
		System.Runtime.InteropServices.Marshal.Copy(channel, samples, 0, frames);
		double sum = 0;
		foreach (float s in samples) { sum += s * s; }
		return AudioLevel.FromRms(Math.Sqrt(sum / frames));
	}
}
