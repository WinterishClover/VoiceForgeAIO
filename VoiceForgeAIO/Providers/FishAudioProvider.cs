using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceForgeAIO.Providers;

internal sealed class FishAudioProvider : ITtsProvider
{
    public string Id => "FishAudio";
    public string DisplayName => "Fish Audio";
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "s2.1-pro", "s2.1-pro-free", "s1" };
    public string DefaultModel => "s2.1-pro";
    // Fish Audio's "format" field explicitly supports "wav" as a native output.
    public bool SupportsWavOutput => true;

    private sealed class ModelsResponse { [JsonPropertyName("items")] public List<ModelEntry> Items { get; init; } = new(); }
    private sealed class ModelEntry
    {
        [JsonPropertyName("_id")] public string Id { get; init; } = "";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    public async Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.fish.audio/model?page_size=200&self=true");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiKey);
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<ModelsResponse>(body) ?? new ModelsResponse();
        return parsed.Items.Select(v => new ProviderVoice { VoiceId = v.Id, DisplayName = v.Title, Description = v.Description ?? "" }).ToList();
    }

    public async Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var payload = new { text, reference_id = voiceId, format = "wav" };

        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.fish.audio/v1/tts");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiKey);
            req.Headers.Add("model", modelId);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
