using System.Diagnostics;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;

namespace HerdrRemote.Services;

// AVAudioEngine half of AudioRecorder: the input tap's buffers are converted to 16 kHz mono 16-bit and written to the WAV.
internal sealed partial class AudioRecorder
{
	private MicEngine? _mic;
	private AVAudioConverter? _converter;
	private AVAudioFormat? _output;

	private partial bool StartNative()
	{
		_output = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, SampleRate, 1, true);
		_mic = new MicEngine();
		_mic.Interrupted += RaiseInterrupted;
		return _mic.Start((pcm, _) => Convert(pcm));
	}

	private partial void StopNative()
	{
		_mic?.Stop();
		_mic = null;
		_converter?.Dispose();
		_converter = null;
	}

	private void Convert(AVAudioPcmBuffer input)
	{
		if (_output is not { } output) { return; }
		_converter ??= new AVAudioConverter(input.Format, output);
		uint capacity = (uint)(input.FrameLength * SampleRate / input.Format.SampleRate) + 64;
		using AVAudioPcmBuffer converted = new(output, capacity);
		bool given = false;
		_converter.ConvertToBuffer(converted, out NSError? error, (uint _, out AVAudioConverterInputStatus status) =>
		{
			if (given) { status = AVAudioConverterInputStatus.NoDataNow; return null!; }
			given = true;
			status = AVAudioConverterInputStatus.HaveData;
			return input;
		});
		int frames = (int)converted.FrameLength;
		if (error is not null || frames == 0 || converted.Int16ChannelData == IntPtr.Zero) { return; }
		byte[] bytes = new byte[frames * 2];
		Marshal.Copy(Marshal.ReadIntPtr(converted.Int16ChannelData), bytes, 0, bytes.Length);
		Write(bytes);
	}
}
