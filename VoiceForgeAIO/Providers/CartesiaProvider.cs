using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceForgeAIO.Providers;

internal sealed class CartesiaProvider : ITtsProvider
{
    public string Id => "Cartesia";
    public string DisplayName => "Cartesia";
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "sonic-3", "sonic-2", "sonic-turbo" };
    public string DefaultModel => "sonic-2";
    // Cartesia returns a native WAV container when asked -- no wrapping needed.
    public bool SupportsWavOutput => true;

    // Cartesia requires this header on every call; it's a "tested against" date,
    // not a live version number, per their docs.
    private const string ApiVersion = "2025-04-16";
    private const int SampleRate = 44100;

    private sealed class VoicesResponse
    {
        [JsonPropertyName("data")] public List<VoiceEntry> Data { get; init; } = new();
        [JsonPropertyName("has_more")] public bool HasMore { get; init; }
    }
    private sealed class VoiceEntry
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("is_owner")] public bool IsOwner { get; init; }
    }

    public async Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        // The endpoint is paginated (default page size is small, ~10) and doesn't
        // segregate owned/cloned voices behind a separate filter -- they come back
        // mixed in with public ones across pages. Walk every page via
        // starting_after (a voice ID cursor, per Cartesia's own docs) rather than
        // just reading the first page, or "My Voices" entries never show up.
        var all = new List<ProviderVoice>();
        string? afterCursor = null;

        for (int page = 0; page < 50; page++) // hard cap so a pagination bug can't loop forever
        {
            string url = "https://api.cartesia.ai/voices?limit=100";
            if (!string.IsNullOrEmpty(afterCursor)) url += $"&starting_after={Uri.EscapeDataString(afterCursor)}";

            var response = await HttpHelper.SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiKey);
                req.Headers.Add("Cartesia-Version", ApiVersion);
                return req;
            }, ct);

            await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<VoicesResponse>(body) ?? new VoicesResponse();

            if (parsed.Data.Count == 0) break;

            all.AddRange(parsed.Data.Select(v => new ProviderVoice
            {
                VoiceId = v.Id,
                DisplayName = v.IsOwner ? $"{v.Name} (mine)" : v.Name,
                Description = v.Description ?? ""
            }));

            if (!parsed.HasMore) break;
            afterCursor = parsed.Data[^1].Id;
        }

        return all;
    }

    public async Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var payload = new
        {
            transcript = text,
            model_id = modelId,
            voice = new { mode = "id", id = voiceId },
            output_format = new { container = "wav", encoding = "pcm_s16le", sample_rate = SampleRate },
            language = "en"
        };

        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.cartesia.ai/tts/bytes");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiKey);
            req.Headers.Add("Cartesia-Version", ApiVersion);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
