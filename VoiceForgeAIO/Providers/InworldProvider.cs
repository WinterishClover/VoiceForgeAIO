using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceForgeAIO.Providers;

internal sealed class InworldProvider : ITtsProvider
{
    public string Id => "Inworld";
    public string DisplayName => "Inworld";
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "inworld-tts-2", "inworld-tts-1.5-max", "inworld-tts-1.5-mini" };
    public string DefaultModel => "inworld-tts-2";
    public bool SupportsWavOutput => true;

    private sealed class VoicesResponse { [JsonPropertyName("voices")] public List<VoiceEntry> Voices { get; init; } = new(); }
    private sealed class VoiceEntry
    {
        [JsonPropertyName("voiceId")] public string VoiceId { get; init; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("description")] public string Description { get; init; } = "";
    }

    public async Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.inworld.ai/voices/v1/voices");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials.ApiKey);
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<VoicesResponse>(body) ?? new VoicesResponse();
        return parsed.Voices.Select(v => new ProviderVoice { VoiceId = v.VoiceId, DisplayName = v.DisplayName, Description = v.Description }).ToList();
    }

    private sealed class SynthReq
    {
        [JsonPropertyName("text")] public string Text { get; init; } = "";
        [JsonPropertyName("voiceId")] public string VoiceId { get; init; } = "";
        [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";
        [JsonPropertyName("audioConfig")] public AudioCfg AudioConfig { get; init; } = new();
        [JsonPropertyName("deliveryMode")] public string DeliveryMode { get; init; } = "BALANCED";
    }
    private sealed class AudioCfg
    {
        [JsonPropertyName("audioEncoding")] public string AudioEncoding { get; init; } = "WAV";
        [JsonPropertyName("sampleRateHertz")] public int SampleRateHertz { get; init; } = 44100;
    }
    private sealed class SynthResp { [JsonPropertyName("audioContent")] public string? AudioContent { get; init; } }

    public async Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var payload = new SynthReq { Text = text, VoiceId = voiceId, ModelId = modelId };

        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.inworld.ai/tts/v1/voice");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<SynthResp>(body);
        if (parsed?.AudioContent is null) throw new InvalidOperationException($"{DisplayName}: response had no audioContent.");
        return Convert.FromBase64String(parsed.AudioContent);
    }
}
