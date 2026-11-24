using System.Buffers.Binary;
using System.Diagnostics;

namespace HerdrRemote.Services;

// Records microphone input to a WAV file (16 kHz, mono, 16-bit PCM) for cloud dictation and reports the input level.
// Platform implementations in Platforms/<os>/AudioRecorder.<os>.cs use an iOS AVAudioEngine input tap or Android AudioRecord.
// Ten minutes produces 19.2 MB, below every supported cloud engine's upload limit.
// StartNative and StopNative run on the main thread; the native side calls Write from its own thread.
internal sealed partial class AudioRecorder : IDisposable
{
	internal const int SampleRate = 16000;
	private const int HeaderBytes = 44;
	private static readonly string Prefix = "dictation-";

	private readonly object _gate = new();
	private FileStream? _file;
	private long _dataBytes;
	private long _levelAt;
	private double _sumSquares;
	private int _samples;
	private bool _stopped;

	public string Path { get; } = System.IO.Path.Combine(FileSystem.CacheDirectory, Prefix + Guid.NewGuid().ToString("N") + ".wav");

	// Reports levels from 0 to 1 on the native thread, at most 10 times per second.
	public event Action<float>? Level;

	// Preserves recorded audio when the system takes the microphone for a call, Siri or another audio-focus change.
	public event Action? Interrupted;

	internal void RaiseInterrupted() => Interrupted?.Invoke();

	// Opens the file and microphone; returns false and releases both if the microphone cannot start.
	public bool Start()
	{
		try
		{
			_file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
			_file.Write(new byte[HeaderBytes]);
			if (StartNative()) { return true; }
		}
		catch (Exception ex) { Debug.WriteLine($"[Dictation] recorder start failed: {ex.GetType().Name}"); }
		Delete();
		return false;
	}

	// Stops recording and finalizes the WAV; returns false if no audio was recorded.
	public bool Stop()
	{
		StopNative();
		lock (_gate)
		{
			_stopped = true;
			if (_file is not { } file) { return false; }
			_file = null;
			try
			{
				byte[] header = new byte[HeaderBytes];
				"RIFF"u8.CopyTo(header);
				BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), (int)(36 + _dataBytes));
				"WAVEfmt "u8.CopyTo(header.AsSpan(8));
				BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
				BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
				BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
				BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), SampleRate);
				BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), SampleRate * 2);
				BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
				BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
				"data"u8.CopyTo(header.AsSpan(36));
				BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), (int)_dataBytes);
				file.Position = 0;
				file.Write(header);
				file.Dispose();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Dictation] recorder finish failed: {ex.GetType().Name}");
				return false;
			}
			return _dataBytes > 0;
		}
	}

	public void Dispose()
	{
		Stop();
		Delete();
	}

	public void Delete()
	{
		lock (_gate) { _file?.Dispose(); _file = null; }
		try { File.Delete(Path); }
		catch (Exception ex) { Debug.WriteLine($"[Dictation] recording delete failed: {ex.GetType().Name}"); }
	}

	// Accepts 16-bit little-endian mono PCM from the native thread.
	internal void Write(ReadOnlySpan<byte> pcm)
	{
		lock (_gate)
		{
			if (_stopped || _file is null) { return; }
			_file.Write(pcm);
			_dataBytes += pcm.Length;
			for (int i = 0; i + 1 < pcm.Length; i += 2)
			{
				double s = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768.0;
				_sumSquares += s * s;
				_samples++;
			}
			long now = Environment.TickCount64;
			if (now - _levelAt < 100 || _samples == 0) { return; }
			_levelAt = now;
			float level = AudioLevel.FromRms(Math.Sqrt(_sumSquares / _samples));
			_sumSquares = 0;
			_samples = 0;
			Level?.Invoke(level);
		}
	}

	// Removes cached recordings left behind after a crash or process termination.
	internal static void DeleteStale()
	{
		try
		{
			foreach (string file in Directory.EnumerateFiles(FileSystem.CacheDirectory, Prefix + "*.wav")) { File.Delete(file); }
		}
		catch (Exception ex) { Debug.WriteLine($"[Dictation] stale recordings: {ex.GetType().Name}"); }
	}

	private partial bool StartNative();
	private partial void StopNative();
}

// Speech is typically around -30 to -10 dBFS. Map RMS logarithmically from -60 dB (0) to -10 dB (1).
internal static class AudioLevel
{
	public static float FromRms(double rms)
	{
		if (rms <= 0.000_001) { return 0; }
		double db = 20 * Math.Log10(rms);
		return (float)Math.Clamp((db + 60) / 50, 0, 1);
	}
}
