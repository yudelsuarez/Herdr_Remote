using System.Diagnostics;
using Languages;

namespace HerdrRemote.Services;

// Uses an Azure Speech resource key and region; available voices come from that region's voices endpoint.
internal sealed class AzureTts : ITtsProvider
{
	// Regions supported by the documented Azure Speech endpoints.
	private static readonly List<string> Regions =
	[
		"australiaeast", "brazilsouth", "canadacentral", "canadaeast", "centralindia", "centralus", "eastasia", "eastus", "eastus2", "francecentral",
		"germanywestcentral", "italynorth", "japaneast", "japanwest", "koreacentral", "northcentralus", "northeurope", "norwayeast", "qatarcentral",
		"southafricanorth", "southcentralus", "southeastasia", "swedencentral", "switzerlandnorth", "switzerlandwest", "uaenorth", "uksouth", "ukwest",
		"westcentralus", "westeurope", "westus", "westus2", "westus3",
	];

	public string Id => "azure";
	public string Name => "Azure Speech";
	public bool NeedsKey => true;
	public bool CanPause => true;

	private static string Voice => App.Setting("tts.azure.voice", "en-US-JennyMultilingualNeural");
	private static string VoiceName => App.Setting("tts.azure.voice_name", "");
	private static string Region => App.Setting("tts.azure.region", "eastus");

	private static string VoiceText => VoiceName.Length > 0 ? VoiceName : Voice;

	public string VoiceSummary() => $"{VoiceText} · {Region}";

	public IReadOnlyList<Pages.InfoRow> Rows() =>
	[
		new("", i18n.Translate("Region"), Region, false, Chevron: true, Key: "region"),
		new("", i18n.Translate("Voice"), VoiceText, true, Chevron: true, Key: "voice"),
	];

	public Task<ChoiceList> OptionsAsync(string rowKey) => OptionsAsync(rowKey, CancellationToken.None);
	public async Task<ChoiceList> OptionsAsync(string rowKey, CancellationToken ct, string? continuation = null, string? search = null, string? language = null, bool own = false)
	{
		ct.ThrowIfCancellationRequested();
		if (rowKey == "region") { return new(i18n.Translate("Region"), [.. Regions.Select(r => new ChoiceOption(r, r))], Region, null); }
		string title = i18n.Translate("Voice");
		string region = Region;
		if (!Regions.Contains(region)) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		string? key = await App.GetApiKeyAsync(Id).WaitAsync(ct);
		if (string.IsNullOrEmpty(key)) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Authentication, true)); }
		var reply = await App.Tts.AzureVoicesAsync(key, region, ct);
		if (TtsFailure.ListError(reply.Status, reply.Error) is { } error) { return new(title, [], Voice, error); }
		if (reply.Value is not { } list || list.Any(v => v is null || string.IsNullOrEmpty(v.ShortName))) { return new(title, [], Voice, TtsFailure.Message(TtsFailureKind.Catalog)); }
		return new(title, [.. list.OrderBy(v => v.Locale).Select(v => new ChoiceOption(v.ShortName, v.LocalName ?? v.DisplayName ?? v.ShortName, v.ShortName, TtsProviders.ReportedLanguage(v.Locale)))], Voice, null);
	}

	public void Save(string rowKey, string value, string text)
	{
		if (rowKey == "region") { App.SaveSetting("tts.azure.region", value); TtsProviders.InvalidateCatalog(Id); return; }
		App.SaveSetting("tts.azure.voice", value);
		App.SaveSetting("tts.azure.voice_name", text);
	}

	public Task<string?> ReadAsync(string text, Action playing, CancellationToken ct) => Prepare(text, ct).PlayAsync(playing, ct);

	public SpeechSettings Capture() => new(Id, "", Voice, VoiceName, region: Region);

	public IPreparedSpeech Prepare(string text, CancellationToken ct) => Prepare(text, Capture(), ct);

	public IPreparedSpeech Prepare(string text, SpeechSettings settings, CancellationToken ct) => new CloudSpeech(OpenAsync(text, settings, ct), nameof(AzureTts), ct);

	// Returns the speech stream, or a displayable error if it cannot be opened.
	private async Task<(SpeechReply? Reply, string? Error)> OpenAsync(string text, SpeechSettings settings, CancellationToken ct)
	{
		string? key = await settings.CredentialAsync(ct);
		if (string.IsNullOrEmpty(key)) { return (null, string.Format(i18n.Translate("Save your {0} API key in Settings first."), Name)); }
		return CloudSpeech.Opened(await App.Tts.AzureSpeechAsync(key, settings.Region, settings.Voice, text, ct));
	}
}
