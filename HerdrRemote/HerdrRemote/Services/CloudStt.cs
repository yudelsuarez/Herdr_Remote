using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// Records a WAV through AudioRecorder and uploads it through TtsClient.TranscribeAsync after the user stops; transcription is not streamed.
// Uses the key saved for the same provider's voice through App.GetApiKeyAsync. Failed uploads keep the recording for retry;
// success, discard or the next recording start deletes it.
internal sealed class CloudStt(string id, string name) : ISpeechToText
{
	public string Id => id;
	public string Name => name;
	public bool NeedsKey => true;
	public bool Streams => false;

	public async Task<string?> CheckAsync(string language, CancellationToken ct)
	{
		string? key;
		try { key = await App.GetApiKeyAsync(id); }
		catch (Exception ex) { Debug.WriteLine($"[Dictation] key read {id}: {ex.GetType().Name}"); key = null; }
		return string.IsNullOrWhiteSpace(key) ? SttFailure.MissingKey(name) : null;
	}

	public IDictation Start(string language, IReadOnlyList<string> terms) => new CloudDictation(this, language, terms);

	private sealed class CloudDictation : IRetryable
	{
		private readonly CloudStt _provider;
		private readonly string _language;
		private readonly IReadOnlyList<string> _terms;
		private readonly AudioRecorder _recorder = new();
		private readonly TaskCompletionSource<SttFailureKind?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly CancellationTokenSource _cancel = new();
		private bool _stopped;

		public CloudDictation(CloudStt provider, string language, IReadOnlyList<string> terms)
		{
			_provider = provider;
			_language = language;
			_terms = terms;
			_recorder.Level += level => Level?.Invoke(level);
			_recorder.Interrupted += () => MainThread.BeginInvokeOnMainThread(() => _ = StopAsync());
			if (!_recorder.Start()) { _stopped = true; _ended.TrySetResult(SttFailureKind.Audio); }
		}

		public event Action<string>? Partial { add { } remove { } }
		public event Action<string>? Committed;
		public event Action<float>? Level;

		public Task<SttFailureKind?> Ended => _ended.Task;

		public bool HasRecording => File.Exists(_recorder.Path);

		public async Task StopAsync()
		{
			if (_stopped) { return; }
			_stopped = true;
			if (!_recorder.Stop())
			{
				_recorder.Delete();
				_ended.TrySetResult(null);
				return;
			}
			_ended.TrySetResult(await UploadAsync());
		}

		public Task<SttFailureKind?> RetryAsync() => UploadAsync();

		private async Task<SttFailureKind?> UploadAsync()
		{
			string? key;
			try { key = await App.GetApiKeyAsync(_provider.Id); }
			catch (Exception ex)
			{
				Debug.WriteLine($"[Dictation] key read {_provider.Id}: {ex.GetType().Name}");
				return SttFailureKind.Authentication;
			}
			if (string.IsNullOrWhiteSpace(key)) { return SttFailureKind.Authentication; }
			TtsClient.ApiReply<string> reply;
			try
			{
				reply = await App.Tts.TranscribeAsync(_provider.Id, key, SttProviders.Model(_provider.Id), _language,
					App.Setting("tts.azure.region", "eastus"), _recorder.Path, _terms, _cancel.Token);
			}
			catch (OperationCanceledException) { return null; }
			catch (Exception ex)
			{
				Debug.WriteLine($"[Dictation] upload {_provider.Id}: {ex.GetType().Name}");
				return SttFailureKind.Unknown;
			}
			if (reply.Status is < 200 or > 299 || reply.Value is null) { return SttFailure.Classify(reply.Status, reply.Error); }
			_recorder.Delete();
			if (!string.IsNullOrWhiteSpace(reply.Value)) { Committed?.Invoke(reply.Value); }
			return null;
		}

		public void Cancel()
		{
			_stopped = true;
			_cancel.Cancel();
			_recorder.Dispose();
			_ended.TrySetResult(null);
		}

		public void Discard() => _recorder.Delete();
	}
}
