using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HerdrRemote.Services;

// Shares one long-lived HttpClient across five cloud voice services, reusing connections with per-call timeouts.
// Requests signed 16-bit little-endian mono PCM at 24 kHz (AudioPlayer.SampleRate), wrapped in WAV for self-hosted servers.
// SpeechReply.Pcm yields audio as it arrives. Request failures become safe results; caller cancellation propagates.
// Keys go only in request headers. Logs never contain keys, headers, bodies or spoken text.
internal sealed class TtsClient
{
	private static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);
	private const string FishBase = "https://api.fish.audio";

	private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

	// Status 0 means a network error or timeout; Error contains only an allowlisted code, never a service message.
	public sealed record ApiReply<T>(int Status, T? Value, string? Error);

	internal static string ServerRoot(string baseUrl)
	{
		string root = baseUrl.TrimEnd('/');
		return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1";
	}

	public Task<ApiReply<ServerHealth>> ServerHealthAsync(string baseUrl, string? key, CancellationToken ct)
	{
		HttpRequestMessage request = new(HttpMethod.Get, ServerRoot(baseUrl) + "/health");
		if (!string.IsNullOrEmpty(key)) { request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}"); }
		return ListAsync(request, TtsJsonContext.Default.ServerHealth, ct);
	}

	public Task<ApiReply<ServerStyles>> ServerStylesAsync(string baseUrl, string? key, CancellationToken ct)
	{
		HttpRequestMessage request = new(HttpMethod.Get, ServerRoot(baseUrl) + "/styles");
		if (!string.IsNullOrEmpty(key)) { request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}"); }
		return ListAsync(request, TtsJsonContext.Default.ServerStyles, ct);
	}

	public Task<ApiReply<OpenAiModels>> OpenAiModelsAsync(string? key, CancellationToken ct, string baseUrl = "https://api.openai.com/v1")
	{
		HttpRequestMessage request = new(HttpMethod.Get, ServerRoot(baseUrl) + "/models");
		if (!string.IsNullOrEmpty(key)) { request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}"); }
		return ListAsync(request, TtsJsonContext.Default.OpenAiModels, ct);
	}

	// OpenAI PCM is fixed at 24 kHz: signed 16-bit little-endian samples without a header.
	public Task<SpeechReply> OpenAiSpeechAsync(string key, string model, string voice, string text, CancellationToken ct)
	{
		HttpRequestMessage request = Json(HttpMethod.Post, "https://api.openai.com/v1/audio/speech",
			new OpenAiSpeechRequest(model, text, voice, "pcm"), TtsJsonContext.Default.OpenAiSpeechRequest);
		request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
		return StreamAsync(request, SpeechFraming.Raw, ct);
	}

	// Requests WAV from a self-hosted OpenAI-compatible server; Supertonic supports wav, flac and ogg, not headerless PCM.
	// baseUrl may already end in /v1. A null language (auto) is omitted, and a key is sent only if saved.
	public Task<SpeechReply> SelfHostedSpeechAsync(string baseUrl, string? key, string model, string voice, double speed, string? lang, string text, CancellationToken ct)
	{
		string url = ServerRoot(baseUrl) + "/audio/speech";
		HttpRequestMessage request = Json(HttpMethod.Post, url,
			new SelfHostedSpeechRequest(model, text, voice, speed, "wav", lang), TtsJsonContext.Default.SelfHostedSpeechRequest);
		if (!string.IsNullOrEmpty(key)) { request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}"); }
		return StreamAsync(request, SpeechFraming.Wav, ct);
	}

	// The endpoint documents both raw audio and server-sent events; the response Content-Type selects the framing.
	public Task<SpeechReply> ElevenLabsSpeechAsync(string key, string voiceId, string model, string text, CancellationToken ct)
	{
		HttpRequestMessage request = Json(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}/stream?output_format=pcm_24000",
			new ElevenLabsSpeechRequest(text, model), TtsJsonContext.Default.ElevenLabsSpeechRequest);
		request.Headers.TryAddWithoutValidation("xi-api-key", key);
		return StreamAsync(request, SpeechFraming.ByContentType, ct);
	}

	public Task<ApiReply<ElevenLabsVoices>> ElevenLabsVoicesAsync(string key, CancellationToken ct = default, string? continuation = null, string? search = null, string? language = null)
	{
		string url = "https://api.elevenlabs.io/v2/voices?page_size=100";
		if (!string.IsNullOrEmpty(continuation)) { url += "&next_page_token=" + Uri.EscapeDataString(continuation); }
		if (!string.IsNullOrEmpty(search)) { url += "&search=" + Uri.EscapeDataString(search); }
		if (!string.IsNullOrEmpty(language)) { url += "&language=" + Uri.EscapeDataString(language); }
		HttpRequestMessage request = new(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("xi-api-key", key);
		return ListAsync(request, TtsJsonContext.Default.ElevenLabsVoices, ct);
	}

	public Task<ApiReply<List<ElevenLabsModel>>> ElevenLabsModelsAsync(string key, CancellationToken ct = default)
	{
		HttpRequestMessage request = new(HttpMethod.Get, "https://api.elevenlabs.io/v1/models");
		request.Headers.TryAddWithoutValidation("xi-api-key", key);
		return ListAsync(request, TtsJsonContext.Default.ListElevenLabsModel, ct);
	}

	// Streams newline-delimited JSON with base64 PCM in each line's result.audioContent (SpeechFraming.Ndjson).
	public Task<SpeechReply> InworldSpeechAsync(string key, string voiceId, string model, string text, CancellationToken ct)
	{
		HttpRequestMessage request = Json(HttpMethod.Post, "https://api.inworld.ai/tts/v1/voice:stream",
			new InworldSpeechRequest(text, voiceId, model, new InworldAudioConfig("PCM", AudioPlayer.SampleRate)), TtsJsonContext.Default.InworldSpeechRequest);
		request.Headers.TryAddWithoutValidation("Authorization", $"Basic {key}");
		return StreamAsync(request, SpeechFraming.Ndjson, ct);
	}

	public async Task<ApiReply<InworldVoices>> InworldVoicesAsync(string key, CancellationToken ct = default, string? continuation = null)
	{
		string url = "https://api.inworld.ai/voices/v1/voices?pageSize=100";
		if (!string.IsNullOrEmpty(continuation)) { url += "&pageToken=" + Uri.EscapeDataString(continuation); }
		HttpRequestMessage request = new(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("Authorization", $"Basic {key}");
		ApiReply<InworldVoices> reply = await ListAsync(request, TtsJsonContext.Default.InworldVoices, ct).ConfigureAwait(false);
		return reply with { Error = reply.Error ?? (reply.Status is >= 200 and <= 299 && reply.Value?.Voices is null ? "catalog_schema" : null) };
	}

	// A null referenceId omits the field to select Fish's default voice. Uses reduced-latency "balanced" mode
	// and chunk_length 100, the documented minimum, to start audio sooner.
	public Task<SpeechReply> FishSpeechAsync(string key, string model, string? referenceId, string text, CancellationToken ct)
	{
		HttpRequestMessage request = Json(HttpMethod.Post, $"{FishBase}/v1/tts",
			new FishSpeechRequest(text, referenceId, "pcm", AudioPlayer.SampleRate, "balanced", 100), TtsJsonContext.Default.FishSpeechRequest);
		request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
		request.Headers.TryAddWithoutValidation("model", model);
		return StreamAsync(request, SpeechFraming.Raw, ct);
	}

	public Task<ApiReply<FishVoices>> FishVoicesAsync(string key, CancellationToken ct = default, int page = 1, bool own = false, string? search = null, string? language = null)
	{
		if (page < 1) { throw new ArgumentOutOfRangeException(nameof(page)); }
		string url = $"{FishBase}/model?page_size=50&page_number={page}&sort_by=score&self={(own ? "true" : "false")}";
		if (!string.IsNullOrEmpty(search)) { url += "&title=" + Uri.EscapeDataString(search); }
		if (!string.IsNullOrEmpty(language)) { url += "&language=" + Uri.EscapeDataString(language); }
		HttpRequestMessage request = new(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
		return ListAsync(request, TtsJsonContext.Default.FishVoices, ct);
	}

	// Derives xml:lang from the voice's first two dash-separated parts (en-US-JennyMultilingualNeural -> en-US).
	// Escapes text before inserting it into SSML.
	public Task<SpeechReply> AzureSpeechAsync(string key, string region, string voice, string text, CancellationToken ct)
	{
		string locale = string.Join('-', voice.Split('-').Take(2));
		string ssml = $"<speak version='1.0' xml:lang='{locale}'><voice name='{voice}'>{System.Security.SecurityElement.Escape(text)}</voice></speak>";
		ByteArrayContent content = new(Encoding.UTF8.GetBytes(ssml));
		content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml");
		HttpRequestMessage request = new(HttpMethod.Post, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1") { Content = content };
		request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
		request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", "raw-24khz-16bit-mono-pcm");
		request.Headers.TryAddWithoutValidation("User-Agent", "HerdrRemote");
		return StreamAsync(request, SpeechFraming.Raw, ct);
	}

	public Task<ApiReply<List<AzureVoice>>> AzureVoicesAsync(string key, string region, CancellationToken ct = default)
	{
		HttpRequestMessage request = new(HttpMethod.Get, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
		request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
		return ListAsync(request, TtsJsonContext.Default.ListAzureVoice, ct);
	}

	// Uploads one recorded WAV (AudioRecorder: 16 kHz, mono, 16-bit) through the shared send path with the speech timeout.
	// Returns transcription text or an allowlisted error code. Uses OpenAI audio/transcriptions, ElevenLabs speech-to-text convert,
	// Azure fast transcription and Inworld stt/v1/transcribe endpoints.
	public async Task<ApiReply<string>> TranscribeAsync(string provider, string key, string model, string language, string azureRegion,
		string path, IReadOnlyList<string> terms, CancellationToken ct)
	{
		string shortLanguage = language.Length >= 2 ? language[..2].ToLowerInvariant() : "en";
		FileStream audio;
		try { audio = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
		catch (IOException) { return new(0, null, null); }
		long size = audio.Length;
		StreamContent file = new(audio);
		file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
		HttpRequestMessage request;
		switch (provider)
		{
			case "openai":
				MultipartFormDataContent openAi = new() { { file, "file", "dictation.wav" }, { new StringContent(model), "model" }, { new StringContent(shortLanguage), "language" } };
				if (terms.Count > 0) { openAi.Add(new StringContent(string.Join(", ", terms)), "prompt"); }
				request = new(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions") { Content = openAi };
				request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
				break;
			case "elevenlabs":
				MultipartFormDataContent eleven = new() { { file, "file", "dictation.wav" }, { new StringContent(model.Length > 0 ? model : "scribe_v2"), "model_id" }, { new StringContent(shortLanguage), "language_code" } };
				request = new(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = eleven };
				request.Headers.TryAddWithoutValidation("xi-api-key", key);
				break;
			case "azure":
				StringContent definition = new($"{{\"locales\":[\"{language}\"]}}", Encoding.UTF8, "application/json");
				MultipartFormDataContent azure = new() { { file, "audio", "dictation.wav" }, { definition, "definition" } };
				request = new(HttpMethod.Post, $"https://{azureRegion}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15") { Content = azure };
				request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
				break;
			case "inworld":
				// The 16 MiB request limit includes both configuration and audio.
				if (size > 16 * 1024 * 1024 - 4096) { file.Dispose(); return new(413, null, "unsupported_input"); }
				StringContent config = new($"{{\"modelId\":\"inworld/inworld-stt-1\",\"audioEncoding\":\"AUTO_DETECT\",\"language\":\"{shortLanguage}\"}}", Encoding.UTF8, "application/json");
				MultipartFormDataContent inworld = new() { { config, "transcribeConfig" }, { file, "file", "dictation.wav" } };
				request = new(HttpMethod.Post, "https://api.inworld.ai/stt/v1/transcribe") { Content = inworld };
				request.Headers.TryAddWithoutValidation("Authorization", $"Basic {key}");
				break;
			default:
				file.Dispose();
				return new(0, null, null);
		}
		using (request)
		{
			ApiReply<byte[]> reply = await SendAsync(request, SpeechTimeout, ct).ConfigureAwait(false);
			if (reply.Status is < 200 or > 299) { return new(reply.Status, null, reply.Error); }
			try
			{
				using JsonDocument doc = JsonDocument.Parse(reply.Value!);
				JsonElement root = doc.RootElement;
				string? text = provider switch
				{
					"azure" => root.TryGetProperty("combinedPhrases", out JsonElement phrases) && phrases.ValueKind == JsonValueKind.Array
						? string.Join(' ', phrases.EnumerateArray().Select(p => Text(p, "text")).Where(t => t is not null)) : null,
					"inworld" => root.TryGetProperty("transcription", out JsonElement transcription) ? Text(transcription, "transcript") : null,
					_ => Text(root, "text"),
				};
				return new(reply.Status, text ?? "", null);
			}
			catch (JsonException)
			{
				Debug.WriteLine("[Tts] transcription reply invalid");
				return new(reply.Status, null, "catalog_schema");
			}
		}
	}

	private static HttpRequestMessage Json<T>(HttpMethod method, string url, T body, JsonTypeInfo<T> info)
	{
		ByteArrayContent content = new(JsonSerializer.SerializeToUtf8Bytes(body, info));
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		return new HttpRequestMessage(method, url) { Content = content };
	}

	// Returns after headers arrive; SpeechReply.Pcm reads the body later with a timeout per read.
	// Non-2xx responses use ReadBodyAsync to extract a safe error. Caller cancellation propagates; transport failures and header
	// timeouts return Status 0. An oversized error body retains the HTTP status without an error code. The reply owns both HTTP objects.
	internal async Task<SpeechReply> StreamAsync(HttpRequestMessage request, SpeechFraming framing, CancellationToken ct)
	{
		HttpResponseMessage? response = null;
		int status = 0;
		try
		{
			using (CancellationTokenSource headers = CancellationTokenSource.CreateLinkedTokenSource(ct))
			{
				headers.CancelAfter(SpeechTimeout);
				response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
			}
			status = (int)response.StatusCode;
			if (status is >= 200 and <= 299) { return new SpeechReply(status, null, request, response, framing); }
			string? error = ErrorText(await ReadBodyAsync(response.Content, SpeechTimeout, ct).ConfigureAwait(false));
			response.Dispose();
			request.Dispose();
			return new SpeechReply(status, error, null, null, framing);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			response?.Dispose();
			request.Dispose();
			throw;
		}
		catch (InvalidDataException)
		{
			response?.Dispose();
			request.Dispose();
			return new SpeechReply(status, null, null, null, framing);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Tts] speech request failed: {ex.GetType().Name}");
			response?.Dispose();
			request.Dispose();
			return new SpeechReply(0, null, null, null, framing);
		}
	}

	// Reads a complete body within timeout, for non-2xx speech errors and all catalog responses.
	private static async Task<byte[]> ReadBodyAsync(HttpContent content, TimeSpan timeout, CancellationToken ct)
	{
		using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
		budget.CancelAfter(timeout);
		using Stream stream = await content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
		using MemoryStream body = new();
		byte[] buffer = new byte[8192];
		int read;
		while ((read = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) != 0)
		{
			if (body.Length + read > 1024 * 1024) { throw new InvalidDataException("Speech response body limit exceeded."); }
			body.Write(buffer, 0, read);
		}
		return body.ToArray();
	}

	// Treats malformed or null 2xx responses as schema failures, not successful empty catalogs.
	private async Task<ApiReply<T>> ListAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> info, CancellationToken ct)
	{
		using (request)
		{
			ApiReply<byte[]> reply = await SendAsync(request, ListTimeout, ct).ConfigureAwait(false);
			if (reply.Status is < 200 or > 299) { return new(reply.Status, default, reply.Error); }
			try
			{
				T? value = JsonSerializer.Deserialize(reply.Value!, info);
				return new(reply.Status, value, value is null ? "catalog_schema" : null);
			}
			catch (JsonException)
			{
				Debug.WriteLine("[Tts] catalog schema invalid");
				return new(reply.Status, default, "catalog_schema");
			}
		}
	}

	// Reads catalog bytes once through ReadBodyAsync with a linked timeout and extracts safe errors from non-2xx responses.
	// Caller cancellation propagates; transport failures and timeouts return Status 0. Oversized bodies retain the HTTP status,
	// with a schema error for 2xx responses.
	private async Task<ApiReply<byte[]>> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
	{
		int status = 0;
		try
		{
			using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(timeout);
			using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
			status = (int)response.StatusCode;
			byte[] bytes = await ReadBodyAsync(response.Content, timeout, cts.Token).ConfigureAwait(false);
			return status is >= 200 and <= 299 ? new(status, bytes, null) : new(status, null, ErrorText(bytes));
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (InvalidDataException) { return new(status, null, status is >= 200 and <= 299 ? "catalog_schema" : null); }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Tts] catalog request failed: {ex.GetType().Name}");
			return new(0, null, null);
		}
	}

	// Only structured, allowlisted codes survive; arbitrary provider messages never reach the UI or logs.
	internal static string? ErrorText(byte[] bytes)
	{
		if (bytes.Length == 0) { return null; }
		try
		{
			using JsonDocument doc = JsonDocument.Parse(bytes);
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) { return null; }
			JsonElement context = root;
			if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object) { context = error; }
			else if (root.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.Object) { context = detail; }
			string? code = Text(context, "code") ?? Text(context, "status") ?? Text(context, "type");
			return code is "invalid_api_key" or "authentication_error" or "permission_denied" or "insufficient_permissions"
				or "insufficient_quota" or "quota_exceeded" or "out_of_credits" or "voice_not_found" or "model_not_found"
				or "invalid_voice_id" or "invalid_model_id" or "unsupported_language" or "unsupported_input" or "text_too_long" ? code : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	internal static string? Text(JsonElement obj, string name) =>
		obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

// PCM framing: raw bytes, newline-delimited JSON with base64 chunks (Inworld), or WAV (self-hosted servers).
// ByContentType selects SSE base64 chunks for text/event-stream and raw bytes otherwise (ElevenLabs).
internal enum SpeechFraming { Raw, Ndjson, Sse, ByContentType, Wav }

// Represents a safe failure after headers arrive, distinguishing transport failures from local audio validation failures.
internal sealed class SpeechStreamException : Exception
{
	public TtsFailureKind Kind { get; }
	public SpeechStreamException(string? code) : this(TtsFailure.Classify(code is null ? 0 : 500, code)) { }
	public SpeechStreamException(TtsFailureKind kind) : base("The speech stream failed.") { Kind = kind; }
}

// A streaming reply with an allowlisted Error code; Status 0 means transport failure. Dispose releases HTTP resources once.
internal sealed class SpeechReply(int status, string? error, HttpRequestMessage? request, HttpResponseMessage? response, SpeechFraming framing) : IDisposable
{
	// Each body read has this idle timeout; there is no overall body timeout after headers arrive.
	private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);

	public int Status { get; } = status;

	public string? Error { get; } = error;
	private int _disposed;

	// Yields incoming audio in order as whole 16-bit samples. Failures after a 2xx response throw SpeechStreamException;
	// caller cancellation throws OperationCanceledException.
	public async IAsyncEnumerable<byte[]> Pcm([EnumeratorCancellation] CancellationToken ct)
	{
		if (response is null) { yield break; }
		SpeechFraming body = framing != SpeechFraming.ByContentType ? framing
			: response.Content.Headers.ContentType?.MediaType == "text/event-stream" ? SpeechFraming.Sse : SpeechFraming.Raw;
		PcmFramer framer = new(body);
		Stream stream = await Guarded(() => response.Content.ReadAsStreamAsync(ct), ct).ConfigureAwait(false);
		byte[] buffer = new byte[16 * 1024];
		while (true)
		{
			int read = await ReadAsync(stream, buffer, ct).ConfigureAwait(false);
			foreach (byte[] piece in read == 0 ? framer.End() : framer.Push(buffer, read)) { yield return piece; }
			if (read == 0) { yield break; }
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
		response?.Dispose();
		request?.Dispose();
	}

	// Gives each read its own timeout. Timeout or cancellation disposes the response to unblock platform HTTP reads.
	// Disposal runs on the thread pool and logs failures: cancellation callbacks run on the cancelling thread, so direct disposal
	// can throw into the UI-thread Stop call (Android can throw "Unbalanced enter/exit" during an active read).
	private async Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationToken ct)
	{
		using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
		budget.CancelAfter(BodyTimeout);
		using CancellationTokenRegistration abort = budget.Token.Register(() => _ = Task.Run(Abort));
		return await Guarded(() => stream.ReadAsync(buffer, budget.Token).AsTask(), ct).ConfigureAwait(false);
	}

	private void Abort()
	{
		try { Dispose(); }
		catch (Exception ex) { Debug.WriteLine($"[Tts] speech body abort failed: {ex.GetType().Name}"); }
	}

	// Preserves caller cancellation and maps other read failures to a safe transport failure.
	private static async Task<T> Guarded<T>(Func<Task<T>> read, CancellationToken ct)
	{
		try { return await read().ConfigureAwait(false); }
		catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
		catch (Exception ex)
		{
			Debug.WriteLine($"[Tts] speech body read failed: {ex.GetType().Name}");
			throw new SpeechStreamException(null);
		}
	}
}

// Converts incoming bytes to whole 16-bit PCM samples, carrying an odd byte to the next read. Raw/JSON streams drop a trailing
// odd byte; WAV rejects one. Ndjson reads base64 PCM from result.audioContent, skips lines without audio or errors, and accepts
// a final line without a newline. SSE reads base64 or JSON audio_base64/audio from "data:" lines and skips other lines.
// Error objects end the stream with a safe classified failure; malformed JSON or base64 is an audio failure. WAV buffers the
// header until the data chunk, then accepts only its declared bytes, or reads to EOF for the streaming sentinel.
internal sealed class PcmFramer(SpeechFraming framing)
{
	// Buffers an incomplete JSON/SSE line or WAV header.
	private readonly MemoryStream _line = new();
	private const int MaxLineBytes = 256 * 1024;
	private const int MaxHeaderBytes = 64 * 1024;
	private int _odd = -1;
	// Tracks validation of a 16-bit mono fmt chunk at AudioPlayer.SampleRate and arrival at the data chunk.
	private bool _wavFormat;
	private bool _wavData;
	private long _wavRemaining;

	public List<byte[]> Push(byte[] data, int count)
	{
		List<byte[]> pieces = [];
		if (framing == SpeechFraming.Raw)
		{
			Even(data.AsSpan(0, count), pieces);
			return pieces;
		}
		if (framing == SpeechFraming.Wav)
		{
			if (_wavData)
			{
				WavData(data.AsSpan(0, count), pieces);
				return pieces;
			}
			if (_line.Length + count > MaxHeaderBytes) { throw NotWav(); }
			_line.Write(data, 0, count);
			Header(pieces);
			return pieces;
		}
		int start = 0;
		for (int i = 0; i < count; i++)
		{
			if (data[i] != (byte)'\n') { continue; }
			if (_line.Length + i - start > MaxLineBytes) { throw new SpeechStreamException(TtsFailureKind.Audio); }
			_line.Write(data, start, i - start);
			Line(pieces);
			start = i + 1;
		}
		if (_line.Length + count - start > MaxLineBytes) { throw new SpeechStreamException(TtsFailureKind.Audio); }
		_line.Write(data, start, count - start);
		return pieces;
	}

	public List<byte[]> End()
	{
		List<byte[]> pieces = [];
		if (framing == SpeechFraming.Wav)
		{
			// Reject a partial WAV without a data chunk; leave an empty body for the player's no-audio message.
			if (!_wavData && _line.Length > 0) { throw NotWav(); }
			if (_wavData && (_wavRemaining > 0 || _odd >= 0)) { throw NotWav(); }
		}
		else if (framing != SpeechFraming.Raw) { Line(pieces); }
		return pieces;
	}

	private static SpeechStreamException NotWav() => new(TtsFailureKind.Audio);

	// Walks RIFF/WAVE chunks with even-byte padding, validates fmt through WavFormat, and skips other chunks such as LIST.
	// Incomplete chunks wait for the next read. The data size bounds PCM unless it is 0xFFFFFFFF, which streams to EOF.
	private void Header(List<byte[]> pieces)
	{
		ReadOnlySpan<byte> head = _line.GetBuffer().AsSpan(0, (int)_line.Length);
		if (head.Length < 12) { return; }
		if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8)) { throw NotWav(); }
		long at = 12;
		while (at + 8 <= head.Length)
		{
			ReadOnlySpan<byte> id = head.Slice((int)at, 4);
			long body = at + 8;
			if (id.SequenceEqual("data"u8))
			{
				if (!_wavFormat) { throw NotWav(); }
				uint length = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice((int)at + 4, 4));
				_wavRemaining = length == uint.MaxValue ? -1 : length;
				if (_wavRemaining >= 0 && (_wavRemaining & 1) != 0) { throw NotWav(); }
				_wavData = true;
				WavData(head[(int)body..], pieces);
				_line.SetLength(0);
				return;
			}
			long size = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice((int)at + 4, 4));
			if (body + size > head.Length) { return; }
			if (id.SequenceEqual("fmt "u8)) { WavFormat(head.Slice((int)body, (int)size)); }
			at = body + size + (size & 1);
		}
	}

	// Treats only the first data chunk as audio and ignores bytes beyond its finite declared length.
	private void WavData(ReadOnlySpan<byte> bytes, List<byte[]> pieces)
	{
		if (_wavRemaining >= 0)
		{
			int count = (int)Math.Min(_wavRemaining, bytes.Length);
			Even(bytes[..count], pieces);
			_wavRemaining -= count;
		}
		else { Even(bytes, pieces); }
	}

	// Requires mono 16-bit PCM at the player's rate: format 1, or 0xFFFE with 1 in the PCM sub-format GUID's first field.
	private void WavFormat(ReadOnlySpan<byte> fmt)
	{
		if (fmt.Length < 16) { throw NotWav(); }
		int format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
		int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
		uint rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
		int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
		bool pcm = format == 1 || (format == 0xFFFE && fmt.Length >= 40 && BinaryPrimitives.ReadUInt32LittleEndian(fmt[24..]) == 1);
		if (!pcm || channels != 1 || bits != 16) { throw NotWav(); }
		if (rate != AudioPlayer.SampleRate)
		{
			throw NotWav();
		}
		_wavFormat = true;
	}

	private void Line(List<byte[]> pieces)
	{
		string line = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).Trim();
		_line.SetLength(0);
		if (framing == SpeechFraming.Sse)
		{
			if (!line.StartsWith("data:", StringComparison.Ordinal)) { return; }
			line = line[5..].Trim();
			if (line.Length == 0 || line == "[DONE]") { return; }
			if (line[0] != '{')
			{
				Even(Base64(line), pieces);
				return;
			}
		}
		if (line.Length == 0) { return; }
		try
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) { return; }
			if ((root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
				|| (framing == SpeechFraming.Sse && root.TryGetProperty("detail", out _)))
			{
				throw new SpeechStreamException(TtsFailure.Classify(500, TtsClient.ErrorText(Encoding.UTF8.GetBytes(line))));
			}
			string? audio = framing == SpeechFraming.Ndjson
				? root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object ? TtsClient.Text(result, "audioContent") : null
				: TtsClient.Text(root, "audio_base64") ?? TtsClient.Text(root, "audio");
			if (audio is not null) { Even(Base64(audio), pieces); }
		}
		catch (JsonException)
		{
			throw new SpeechStreamException(TtsFailureKind.Audio);
		}
	}

	private static byte[] Base64(string text)
	{
		try { return Convert.FromBase64String(text); }
		catch (FormatException) { throw new SpeechStreamException(TtsFailureKind.Audio); }
	}

	// Emits whole samples and carries an odd byte to the next chunk.
	private void Even(ReadOnlySpan<byte> bytes, List<byte[]> pieces)
	{
		int total = bytes.Length + (_odd >= 0 ? 1 : 0);
		if (total < 2)
		{
			if (bytes.Length == 1) { _odd = bytes[0]; }
			return;
		}
		byte[] piece = new byte[total & ~1];
		int from = 0;
		if (_odd >= 0)
		{
			piece[0] = (byte)_odd;
			bytes[..(piece.Length - 1)].CopyTo(piece.AsSpan(1));
			from = piece.Length - 1;
		}
		else
		{
			bytes[..piece.Length].CopyTo(piece);
			from = piece.Length;
		}
		_odd = from < bytes.Length ? bytes[from] : -1;
		pieces.Add(piece);
	}
}

// Wire records name each JSON field explicitly because services mix snake_case, camelCase and PascalCase.
internal sealed record OpenAiSpeechRequest(
	[property: JsonPropertyName("model")] string Model,
	[property: JsonPropertyName("input")] string Input,
	[property: JsonPropertyName("voice")] string Voice,
	[property: JsonPropertyName("response_format")] string ResponseFormat);

internal sealed record SelfHostedSpeechRequest(
	[property: JsonPropertyName("model")] string Model,
	[property: JsonPropertyName("input")] string Input,
	[property: JsonPropertyName("voice")] string Voice,
	[property: JsonPropertyName("speed")] double Speed,
	[property: JsonPropertyName("response_format")] string ResponseFormat,
	[property: JsonPropertyName("lang")] string? Lang);

internal sealed record ElevenLabsSpeechRequest(
	[property: JsonPropertyName("text")] string Text,
	[property: JsonPropertyName("model_id")] string ModelId);

internal sealed record ServerHealth([property: JsonPropertyName("model")] string? Model);
internal sealed record ServerStyles([property: JsonPropertyName("styles")] List<ServerStyle>? Styles);
internal sealed record ServerStyle([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("kind")] string? Kind);
internal sealed record OpenAiModels([property: JsonPropertyName("data")] List<OpenAiModel>? Data);
internal sealed record OpenAiModel([property: JsonPropertyName("id")] string? Id,
	[property: JsonPropertyName("shutdown_date")] string? ShutdownDate = null,
	[property: JsonPropertyName("capabilities")] ModelCapabilities? Capabilities = null);
internal sealed record ModelCapabilities([property: JsonPropertyName("text_to_speech")] bool? TextToSpeech = null,
	[property: JsonPropertyName("speech")] bool? Speech = null);

internal sealed record ElevenLabsVoices([property: JsonPropertyName("voices")] List<ElevenLabsVoice>? Voices,
	[property: JsonPropertyName("next_page_token")] string? NextPageToken = null,
	[property: JsonPropertyName("has_more")] bool HasMore = false);

internal sealed record ElevenLabsVoice(
	[property: JsonPropertyName("voice_id")] string VoiceId,
	[property: JsonPropertyName("name")] string? Name,
	[property: JsonPropertyName("category")] string? Category,
	[property: JsonPropertyName("language")] string? Language = null,
	[property: JsonPropertyName("labels")] Dictionary<string, string>? Labels = null,
	[property: JsonPropertyName("verified_languages")] List<ElevenLabsVoiceLanguage>? VerifiedLanguages = null);
internal sealed record ElevenLabsVoiceLanguage([property: JsonPropertyName("language")] string? Language,
	[property: JsonPropertyName("locale")] string? Locale, [property: JsonPropertyName("model_id")] string? ModelId);

internal sealed record ElevenLabsModel(
	[property: JsonPropertyName("model_id")] string ModelId,
	[property: JsonPropertyName("name")] string? Name,
	[property: JsonPropertyName("can_do_text_to_speech")] bool CanDoTextToSpeech,
	[property: JsonPropertyName("maximum_text_length_per_request")] int? MaximumTextLengthPerRequest = null,
	[property: JsonPropertyName("description")] string? Description = null,
	[property: JsonPropertyName("languages")] List<ElevenLabsLanguage>? Languages = null);
internal sealed record ElevenLabsLanguage([property: JsonPropertyName("language_id")] string? Id,
	[property: JsonPropertyName("name")] string? Name);

internal sealed record InworldSpeechRequest(
	[property: JsonPropertyName("text")] string Text,
	[property: JsonPropertyName("voiceId")] string VoiceId,
	[property: JsonPropertyName("modelId")] string ModelId,
	[property: JsonPropertyName("audioConfig")] InworldAudioConfig AudioConfig);

internal sealed record InworldAudioConfig(
	[property: JsonPropertyName("audioEncoding")] string AudioEncoding,
	[property: JsonPropertyName("sampleRateHertz")] int SampleRateHertz);

internal sealed record InworldVoices([property: JsonPropertyName("voices")] List<InworldVoice>? Voices,
	[property: JsonPropertyName("nextPageToken")] string? NextPageToken = null);

internal sealed record InworldVoice(
	[property: JsonPropertyName("voiceId")] string VoiceId,
	[property: JsonPropertyName("displayName")] string? DisplayName,
	[property: JsonPropertyName("languageCode")] string? LanguageCode,
	[property: JsonPropertyName("langCode")] string? LangCode = null,
	[property: JsonPropertyName("promptLanguages")] List<string>? PromptLanguages = null)
{
	internal string? VoiceLanguage => !string.IsNullOrWhiteSpace(LanguageCode) ? TtsProviders.ReportedLanguage(LanguageCode) : LangCode switch
	{
		"EN_US" => "en-US", "ZH_CN" => "zh-CN", "KO_KR" => "ko-KR", "JA_JP" => "ja-JP", "RU_RU" => "ru-RU",
		"IT_IT" => "it-IT", "ES_ES" => "es-ES", "PT_BR" => "pt-BR", "DE_DE" => "de-DE", "FR_FR" => "fr-FR",
		"AR_SA" => "ar-SA", "PL_PL" => "pl-PL", "NL_NL" => "nl-NL", "HI_IN" => "hi-IN", "HE_IL" => "he-IL", _ => null,
	};
}

internal sealed record FishSpeechRequest(
	[property: JsonPropertyName("text")] string Text,
	[property: JsonPropertyName("reference_id")] string? ReferenceId,
	[property: JsonPropertyName("format")] string Format,
	[property: JsonPropertyName("sample_rate")] int SampleRate,
	[property: JsonPropertyName("latency")] string Latency,
	[property: JsonPropertyName("chunk_length")] int ChunkLength);

internal sealed record FishVoices(
	[property: JsonPropertyName("total")] int Total,
	[property: JsonPropertyName("items")] List<FishVoice>? Items,
	[property: JsonPropertyName("has_more")] bool? HasMore = null,
	[property: JsonPropertyName("max_offset")] int? MaxOffset = null,
	[property: JsonPropertyName("accessible_upper_bound")] int? AccessibleUpperBound = null,
	[property: JsonPropertyName("window_limited")] bool WindowLimited = false);

internal sealed record FishVoice(
	[property: JsonPropertyName("_id")] string Id,
	[property: JsonPropertyName("title")] string? Title,
	[property: JsonPropertyName("language")] string? Language = null,
	[property: JsonPropertyName("languages")] List<string>? Languages = null);

internal sealed record AzureVoice(
	[property: JsonPropertyName("DisplayName")] string? DisplayName,
	[property: JsonPropertyName("LocalName")] string? LocalName,
	[property: JsonPropertyName("ShortName")] string ShortName,
	[property: JsonPropertyName("Locale")] string? Locale,
	[property: JsonPropertyName("VoiceType")] string? VoiceType);

// Shares one source-generated JSON context across all wire records.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAiSpeechRequest))]
[JsonSerializable(typeof(OpenAiModels))]
[JsonSerializable(typeof(ServerHealth))]
[JsonSerializable(typeof(ServerStyles))]
[JsonSerializable(typeof(SelfHostedSpeechRequest))]
[JsonSerializable(typeof(ElevenLabsSpeechRequest))]
[JsonSerializable(typeof(ElevenLabsVoices))]
[JsonSerializable(typeof(List<ElevenLabsModel>))]
[JsonSerializable(typeof(InworldSpeechRequest))]
[JsonSerializable(typeof(InworldVoices))]
[JsonSerializable(typeof(FishSpeechRequest))]
[JsonSerializable(typeof(FishVoices))]
[JsonSerializable(typeof(List<AzureVoice>))]
internal sealed partial class TtsJsonContext : JsonSerializerContext;
