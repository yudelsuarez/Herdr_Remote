using System.Diagnostics;
using Android.Media;

namespace HerdrRemote.Services;

// AudioRecord half of AudioRecorder: 16 kHz mono 16-bit from the microphone, read by one thread that writes the WAV.
internal sealed partial class AudioRecorder
{
	private AudioRecord? _record;
	private Thread? _thread;
	private volatile bool _running;

	private partial bool StartNative()
	{
		int minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
		if (minimum <= 0) { return false; }
		AudioRecord record = new(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minimum, SampleRate / 2 * 2));
		if (record.State != State.Initialized) { record.Release(); return false; }
		try { record.StartRecording(); }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Dictation] AudioRecord start failed: {ex.GetType().Name}");
			record.Release();
			return false;
		}
		_record = record;
		_running = true;
		_thread = new Thread(() =>
		{
			byte[] buffer = new byte[SampleRate / 5];
			while (_running)
			{
				int read = record.Read(buffer, 0, buffer.Length);
				if (read <= 0) { break; }
				Write(buffer.AsSpan(0, read));
			}
			if (_running) { RaiseInterrupted(); }
		}) { IsBackground = true, Name = "dictation-recorder" };
		_thread.Start();
		return true;
	}

	private partial void StopNative()
	{
		_running = false;
		if (_record is not { } record) { return; }
		_record = null;
		try { record.Stop(); } catch (Exception ex) { Debug.WriteLine($"[Dictation] AudioRecord stop failed: {ex.GetType().Name}"); }
		_thread?.Join(1000);
		_thread = null;
		record.Release();
	}
}
