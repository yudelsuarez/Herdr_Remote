using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Languages;

namespace HerdrRemote.Services;

// Provides voice settings, their option lists and text-to-speech playback.
// OptionsAsync and ReadAsync return already translated messages for pages to display unchanged.
public interface ITtsProvider
{
	string Id { get; }
	string Name { get; }
	bool NeedsKey { get; }
	bool CanPause { get; }

	// Summary displayed below the provider title in the Reader tab.
	string VoiceSummary();

	// Voice settings rows; each Key is passed to OptionsAsync and Save as rowKey.
	IReadOnlyList<Pages.InfoRow> Rows();

	Task<ChoiceList> OptionsAsync(string rowKey);
	Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
		=> OptionsAsync(rowKey);
	void RefreshCatalog() { }

	// Stores value and the selected display text; voice rows save the text as voice_name.
	void Save(string rowKey, string value, string text);

	// Returns null when finished or stopped, otherwise a popup message. Invokes playing when audio starts.
	Task<string?> ReadAsync(string text, Action playing, CancellationToken ct);

	// Starts fetching cloud speech immediately; device speech waits until playback. ct cancels preparation.
	IPreparedSpeech Prepare(string text, CancellationToken ct);
	SpeechSettings Capture();
	IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct);
}

// Synthesis configuration is frozen before any credential/locale await; credentials never participate in printing or equality.
public sealed class SpeechSettings(string providerId, string model, string voice, string voiceName = "", string endpoint = "",
	string region = "", string? language = null, int inputLimit = 1024, bool limitInBytes = true,
	bool verifiedLimit = false, Task<Locale?>? deviceLocale = null, Task<string?>? credential = null)
{
	private readonly Task<string?> _credential = credential ?? (providerId == "device" ? Task.FromResult<string?>(null) : App.GetApiKeyAsync(providerId));
	public string ProviderId { get; } = providerId;
	public string Model { get; } = model;
	public string Voice { get; } = voice;
	public string VoiceName { get; } = voiceName;
	public string Endpoint { get; } = endpoint;
	public string Region { get; } = region;
	public string? Language { get; } = language;
	public int InputLimit { get; } = inputLimit > 0 ? inputLimit : 1024;
	public bool LimitInBytes { get; } = inputLimit <= 0 || limitInBytes;
	public bool VerifiedLimit { get; } = inputLimit > 0 && verifiedLimit;
	internal Task<Locale?> DeviceLocale { get; } = deviceLocale ?? Task.FromResult<Locale?>(null);
	internal Task<string?> CredentialAsync(CancellationToken ct) => _credential.WaitAsync(ct);
}

public enum TtsFailureKind { Authentication, Permission, Credits, RateLimit, Transport, Unavailable, UnsupportedInput, Catalog, Audio, Interrupted, PlaybackSpeed, Unknown }

public static class TtsFailure
{
	public static TtsFailureKind Classify(int status, string? code) => code switch
	{
		"invalid_api_key" or "authentication_error" => TtsFailureKind.Authentication,
		"permission_denied" or "insufficient_permissions" => TtsFailureKind.Permission,
		"insufficient_quota" or "quota_exceeded" or "out_of_credits" => TtsFailureKind.Credits,
		"voice_not_found" or "model_not_found" or "invalid_voice_id" or "invalid_model_id" => TtsFailureKind.Unavailable,
		"unsupported_language" or "unsupported_input" or "text_too_long" => TtsFailureKind.UnsupportedInput,
		_ => status switch { 0 => TtsFailureKind.Transport, 401 => TtsFailureKind.Authentication, 403 => TtsFailureKind.Permission, 429 => TtsFailureKind.RateLimit, _ => TtsFailureKind.Unknown },
	};

	public static string Message(TtsFailureKind kind, bool catalog = false) => i18n.Translate(kind switch
	{
		TtsFailureKind.Authentication => "Check this provider's API key.",
		TtsFailureKind.Permission => "This key cannot use the requested voice or model.",
		TtsFailureKind.Credits => "This provider has no credits available.",
		TtsFailureKind.RateLimit => "Too many requests. Try again later.",
		TtsFailureKind.Transport => "Could not connect to the speech provider. Try again.",
		TtsFailureKind.Unavailable => "This voice or model is unavailable. Choose another.",
		TtsFailureKind.UnsupportedInput => "This voice could not read the text. Try another voice.",
		TtsFailureKind.Catalog => "Could not read this provider's list. Refresh to try again.",
		TtsFailureKind.Audio => "This provider returned audio the app cannot play.",
		TtsFailureKind.Interrupted => "Reading was interrupted.",
		TtsFailureKind.PlaybackSpeed => "This playback speed is unavailable.",
		_ => catalog ? "Could not load this provider's list." : "Reading could not continue. Try again or choose another voice.",
	});
	public static string? ListError(int status, string? code) => status is >= 200 and <= 299 && code is null ? null
		: Message(code == "catalog_schema" ? TtsFailureKind.Catalog : Classify(status, code), true);
}

// Speech prepared through ITtsProvider.Prepare.
public interface IPreparedSpeech
{
	// Completes without faulting when the first audio arrives or fetching ends without audio; the next prefetch can then start.
	Task Fetched { get; }
	Task Released => Fetched;

	// Plays incoming audio and invokes playing when audio starts. Returns null on completion or stop,
	// otherwise the same popup messages as ReadAsync.
	Task<string?> PlayAsync(Action playing, CancellationToken ct);
}

// Starts a cloud request immediately and queues incoming PCM, allowing prefetch while the current speech plays.
// PlayAsync consumes the queue as it fills. If a stream fails after a 2xx response, queued audio plays first,
// then the shared safe failure message is shown.
internal sealed class CloudSpeech : IPreparedSpeech
{
	private readonly Task<(SpeechReply? Reply, string? Error)> _open;
	// Provider class name used in safe diagnostics.
	private readonly string _provider;
	private readonly Channel<byte[]> _pcm = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(12) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
	private readonly TaskCompletionSource _fetched = new(TaskCreationOptions.RunContinuationsAsynchronously);
	// Read after the queue completes: the early stream failure, or null for a normal end.
	private Exception? _failure;
	private readonly Task _pump;

	public CloudSpeech(Task<(SpeechReply? Reply, string? Error)> open, string provider, CancellationToken ct)
	{
		_open = open;
		_provider = provider;
		_pump = PumpAsync(ct);
	}

	public Task Fetched => _fetched.Task;
	public Task Released => _pump;

	public async Task<string?> PlayAsync(Action playing, CancellationToken ct)
	{
		(_, string? error) = await _open;
		if (error is not null) { return error; }
		try
		{
			AudioPlayer.PrepareSession();
			await AudioPlayer.PlayAsync(_pcm.Reader.ReadAllAsync(ct), playing, ct);
		}
		catch (Exception) when (_failure is SpeechStreamException && !ct.IsCancellationRequested)
		{
			// Report the stream failure rather than the player's no-audio error when no audio arrived.
		}
		catch (SpeechStreamException ex) { return TtsFailure.Message(ex.Kind); }
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Debug.WriteLine($"[{_provider}] play failed: {ex.GetType().Name}");
			return i18n.Translate("The audio could not be played.");
		}
		return _failure switch
		{
			SpeechStreamException failure => TtsFailure.Message(failure.Kind),
			_ => null,
		};
	}

	// Returns a streaming reply or a displayable error; releases replies with HTTP errors.
	public static (SpeechReply? Reply, string? Error) Opened(SpeechReply reply)
	{
		if (reply.Status is >= 200 and <= 299) { return (reply, null); }
		reply.Dispose();
		return (null, TtsFailure.Message(TtsFailure.Classify(reply.Status, reply.Error)));
	}

	// Queues incoming PCM and releases the reply when its stream ends.
	private async Task PumpAsync(CancellationToken ct)
	{
		SpeechReply? reply = null;
		try
		{
			(reply, _) = await _open.ConfigureAwait(false);
			if (reply is null) { return; }
			await foreach (byte[] piece in reply.Pcm(ct).ConfigureAwait(false))
			{
				for (int offset = 0; offset < piece.Length; offset += AudioPlayer.ChunkBytes)
				{
					int length = Math.Min(AudioPlayer.ChunkBytes, piece.Length - offset);
					byte[] chunk = offset == 0 && length == piece.Length ? piece : piece.AsSpan(offset, length).ToArray();
					await _pcm.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);
					_fetched.TrySetResult();
				}
			}
		}
		catch (Exception ex)
		{
			_failure = ex;
		}
		finally
		{
			_pcm.Writer.TryComplete();
			if (ct.IsCancellationRequested) { while (_pcm.Reader.TryRead(out _)) { } }
			_fetched.TrySetResult();
			try { reply?.Dispose(); }
			catch (Exception ex) { Debug.WriteLine($"[{_provider}] reply release failed: {ex.GetType().Name}"); }
		}
	}
}

// Defers device synthesis until PlayAsync calls the provider's ReadAsync.
internal sealed class DeviceSpeech(Func<Action, CancellationToken, float, Task<string?>> read) : IPreparedSpeech
{
	public Task Fetched => Task.CompletedTask;
	public float Rate { get; set; } = 1;

	public Task<string?> PlayAsync(Action playing, CancellationToken ct) => read(playing, ct, Rate);
}

// IDs are identity; language is returned voice metadata, never inferred from the label or prompt languages.
public sealed record ChoiceOption(string Id, string Text, string? Detail = null, string? Language = null,
	IReadOnlyList<string>? PromptLanguages = null, int? MaximumTextLength = null, string? Shutdown = null, bool? SpeechSupported = null,
	IReadOnlyList<string>? Languages = null);

public sealed record ChoiceList(string Title, List<ChoiceOption> Options, string? SelectedId, string? Error,
	string? Field = null, string? Hint = null, Func<string, string?>? Check = null,
	string? Continuation = null, bool Partial = false, string? Note = null, bool ReadOnly = false, string? RefreshError = null);

public static partial class TtsProviders
{
	public static readonly ITtsProvider Device = new DeviceTts();
	public static readonly ITtsProvider SelfHosted = new SelfHostedTts();
	public static readonly ITtsProvider OpenAi = new OpenAiTts();
	public static readonly ITtsProvider ElevenLabs = new ElevenLabsTts();
	public static readonly ITtsProvider Inworld = new InworldTts();
	public static readonly ITtsProvider Fish = new FishTts();
	public static readonly ITtsProvider Azure = new AzureTts();

	public static readonly IReadOnlyList<ITtsProvider> All = [Device, SelfHosted, OpenAi, ElevenLabs, Inworld, Fish, Azure];

	public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>
	{
		["en"] = "This is a voice sample. You can keep reading while you switch apps.",
		["es"] = "Esta es una muestra de voz. Puedes seguir escuchando mientras cambias de app.",
	};

	public static ITtsProvider Current => All.First(p => p.Id == App.Setting("tts.provider", "device"));

	// Never substitute another brand's glyph.
	public static string Glyph(string id) => id switch
	{
		"device" => "\uE92D", "selfhosted" => "\uE965", "openai" => "\uE963", "elevenlabs" => "\uE964", "azure" => "\uE968",
		"inworld" => "\uE96B", "fish" => "\uE96C", _ => "",
	};

	public static async Task<Dictionary<string, bool?>> SavedKeysAsync()
	{
		Dictionary<string, bool?> states = [];
		foreach (ITtsProvider provider in All.Where(p => p.NeedsKey))
		{
			try { states[provider.Id] = !string.IsNullOrEmpty(await App.GetApiKeyAsync(provider.Id)); }
			catch (Exception ex)
			{
				Debug.WriteLine($"[Voices] key state {provider.Id}: {ex.GetType().Name}");
				states[provider.Id] = null;
			}
		}
		return states;
	}

	public static string KeySummary(IReadOnlyDictionary<string, bool?> states)
	{
		if (states.Values.Any(saved => saved is null)) { return i18n.Translate("Key status unavailable"); }
		int count = states.Values.Count(saved => saved == true);
		return count switch { 0 => i18n.Translate("No keys saved"), 1 => i18n.Translate("1 key saved"), _ => string.Format(i18n.Translate("{0} keys saved"), count) };
	}

	private static readonly Dictionary<string, int> CatalogRevisions = [];
	private static readonly Dictionary<(string Provider, int Revision, string Endpoint, string Region, string Model, string Row, string Search, string Language, bool Own),
		(ChoiceList List, int Pages, HashSet<string> Cursors)> Catalogs = [];
	private static readonly Dictionary<(string Provider, int Revision, string Endpoint, string Region, string Model, string Row, string Search, string Language, bool Own), long> CatalogLoads = [];
	private static long _catalogLoad;
	internal static string? ReportedLanguage(string? language) => string.IsNullOrWhiteSpace(language)
		|| language.Equals("unknown", StringComparison.OrdinalIgnoreCase) || language.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : language;

	// Called after an explicit key/configuration change. No credential or credential hash enters a cache key.
	public static void InvalidateCatalog(string providerId)
	{
		CatalogRevisions[providerId] = CatalogRevisions.GetValueOrDefault(providerId) + 1;
		foreach (var key in Catalogs.Keys.Where(k => k.Provider == providerId).ToList()) { Catalogs.Remove(key); }
		foreach (var key in CatalogLoads.Keys.Where(k => k.Provider == providerId).ToList()) { CatalogLoads.Remove(key); }
	}

	internal static int? ModelInputLimit(string providerId, string model) => Catalogs
		.Where(c => c.Key.Provider == providerId && c.Key.Row == "model")
		.SelectMany(c => c.Value.List.Options).FirstOrDefault(o => o.Id == model)?.MaximumTextLength;

	// UI-thread catalogs: one explicit page at a time, at most five retained pages per query/configuration.
	public static async Task<ChoiceList> OptionsAsync(ITtsProvider provider, string rowKey, CancellationToken ct = default,
		bool refresh = false, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		ct.ThrowIfCancellationRequested();
		string prefix = $"tts.{provider.Id}.";
		string endpoint = App.Setting(prefix + "base_url", "").TrimEnd('/');
		string region = App.Setting(prefix + "region", "");
		string model = rowKey == "voice" ? App.Setting(prefix + "model", "") : "";
		if (refresh) { provider.RefreshCatalog(); }
		var key = (provider.Id, CatalogRevisions.GetValueOrDefault(provider.Id), endpoint, region, model, rowKey, search ?? "", language ?? "", own);
		Catalogs.TryGetValue(key, out var saved);
		if (saved.List is not null)
		{
			saved.List = saved.List with { SelectedId = App.Setting(prefix + rowKey, saved.List.SelectedId ?? ""),
				Field = saved.List.Field is null ? null : App.Setting(prefix + rowKey, saved.List.Field) };
		}
		if (!refresh && continuation is null && saved.List is not null)
		{
			return saved.List;
		}
		if (continuation is not null && (saved.List is null || continuation != saved.List.Continuation || saved.Pages >= 5 || saved.Cursors.Contains(continuation)))
		{
			return saved.List is null ? new ChoiceList("", [], null, TtsFailure.Message(TtsFailureKind.Catalog))
				: saved.List with { RefreshError = TtsFailure.Message(TtsFailureKind.Catalog) };
		}
		long load = ++_catalogLoad;
		CatalogLoads[key] = load;
		ChoiceList list;
		try { list = await provider.OptionsAsync(rowKey, ct, continuation, search, language, own); ct.ThrowIfCancellationRequested(); }
		catch
		{
			if (CatalogLoads.GetValueOrDefault(key) == load) { CatalogLoads.Remove(key); }
			throw;
		}
		if (CatalogLoads.GetValueOrDefault(key) != load || CatalogRevisions.GetValueOrDefault(provider.Id) != key.Item2
			|| endpoint != App.Setting(prefix + "base_url", "").TrimEnd('/') || region != App.Setting(prefix + "region", "")
			|| model != (rowKey == "voice" ? App.Setting(prefix + "model", "") : ""))
		{
			if (CatalogLoads.GetValueOrDefault(key) == load) { CatalogLoads.Remove(key); }
			throw new OperationCanceledException("Catalog superseded.");
		}
		CatalogLoads.Remove(key);
		if (list.Error is not null)
		{
			return saved.List is null ? list : saved.List with { Note = i18n.Translate("Saved list · Could not refresh"), RefreshError = list.Error };
		}
		if (list.Options.Any(o => o.Id is null || o.Text is null)
			|| (list.Continuation is not null && (list.Options.Count == 0 || list.Continuation == continuation || continuation is not null && saved.Cursors?.Contains(list.Continuation) == true)))
		{
			string error = TtsFailure.Message(TtsFailureKind.Catalog);
			return saved.List is null ? list with { Options = [], Error = error, Continuation = null } : saved.List with { RefreshError = error };
		}
		int pages = continuation is null ? 1 : saved.Pages + 1;
		HashSet<string> cursors = continuation is null ? [] : new(saved.Cursors ?? []);
		if (continuation is not null) { cursors.Add(continuation); }
		list = list with { Options = [.. (continuation is null ? list.Options : (saved.List?.Options ?? []).Concat(list.Options)).DistinctBy(o => o.Id)],
			Partial = list.Partial || list.Continuation is not null, Continuation = pages >= 5 ? null : list.Continuation };
		// Bound different search/configuration snapshots too, not just each query's pages.
		if (Catalogs.Count >= 32 && !Catalogs.ContainsKey(key)) { Catalogs.Remove(Catalogs.Keys.First()); }
		Catalogs[key] = (list, pages, cursors);
		return list;
	}

	// Every reading entry uses this boundary; the original text stays in the reading plan for its marks.
	public static IPreparedSpeech? Prepare(ITtsProvider provider, string text, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		string? clean = Filter(text);
		return clean is null ? null : provider.Prepare(clean, ct);
	}

	// Lazy requests, but validate every text element before sending the first piece of this sentence.
	public static IEnumerable<IPreparedSpeech> Prepare(ITtsProvider provider, SpeechSettings settings, string text, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (Filter(text) is not { } clean) { yield break; }
		int[] starts = StringInfo.ParseCombiningCharacters(clean);
		int[] sizes = new int[starts.Length];
		for (int i = 0; i < starts.Length; i++)
		{
			int end = i + 1 < starts.Length ? starts[i + 1] : clean.Length;
			sizes[i] = settings.LimitInBytes ? Encoding.UTF8.GetByteCount(clean.AsSpan(starts[i], end - starts[i])) : end - starts[i];
			if (sizes[i] > settings.InputLimit) { throw new SpeechStreamException(TtsFailureKind.UnsupportedInput); }
		}
		for (int at = 0; at < starts.Length;)
		{
			ct.ThrowIfCancellationRequested();
			int next = at, used = 0, whitespace = -1;
			while (next < starts.Length && used + sizes[next] <= settings.InputLimit)
			{
				used += sizes[next];
				if (char.IsWhiteSpace(clean[starts[next]])) { whitespace = next + 1; }
				next++;
			}
			if (next < starts.Length && whitespace > at) { next = whitespace; }
			int end = next < starts.Length ? starts[next] : clean.Length;
			yield return provider.Prepare(clean[starts[at]..end], settings, ct);
			at = next;
		}
	}

	internal static string? Filter(string text)
	{
		text = SpeechSequences().Replace(text, " ");
		StringBuilder spoken = new(text.Length);
		Span<char> chars = stackalloc char[2];
		bool word = false, attached = false, image = false;
		foreach (Rune rune in text.EnumerateRunes())
		{
			int value = rune.Value;
			UnicodeCategory category = Rune.GetUnicodeCategory(rune);
			bool mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
			if (value == 0x10EEEE) { image = true; }
			else if (!mark) { image = false; }
			bool letterOrNumber = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
				or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
				or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
			bool keep = !image && (letterOrNumber || (mark && attached && value is not (>= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or 0x20E3))
				|| (value <= char.MaxValue && ".,;:!?¿¡…'\"‘’“”()[]{}-/\\_–—$€£¥%+−=<>@#&".Contains((char)value)));
			if (keep)
			{
				spoken.Append(chars[..rune.EncodeToUtf16(chars)]);
				word |= letterOrNumber;
			}
			else if (!mark && spoken.Length > 0 && spoken[^1] != ' ') { spoken.Append(' '); }
			if (!mark) { attached = keep && letterOrNumber; }
		}
		string clean = spoken.ToString().Trim().Normalize(NormalizationForm.FormC);
		return word ? clean : null;
	}

	// Strip terminal escape strings and whole keycaps before scalar categories can retain their digits or metadata.
	[GeneratedRegex(@"(?:\x1B\][^\x07\x1B]*(?:\x07|\x1B\\|$)|\x9D[^\x07\x9C]*(?:\x07|\x9C|$)|\x1B[P_X^][\s\S]*?(?:\x1B\\|$)|(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]|\x1B[ -/]*[@-~]|[0-9#*]\uFE0F?\u20E3)")]
	private static partial Regex SpeechSequences();
}
