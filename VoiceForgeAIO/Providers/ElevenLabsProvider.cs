using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceForgeAIO.Providers;

internal sealed class ElevenLabsProvider : ITtsProvider
{
    public string Id => "ElevenLabs";
    public string DisplayName => "ElevenLabs";
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "eleven_v4", "eleven_v4_turbo", "eleven_v3", "eleven_multilingual_v2", "eleven_flash_v2_5", "eleven_turbo_v2_5" };
    public string DefaultModel => "eleven_multilingual_v2";
    // ElevenLabs can return a native WAV container directly via output_format=wav_*.
    // Per their docs, only the 44.1kHz variant is gated to the Pro tier and above --
    // 24kHz (and other non-44.1kHz rates) work on lower tiers, so that's what we
    // request. No WAV-wrapping needed on our end anymore.
    public bool SupportsWavOutput => true;

    private sealed class VoicesResponse { [JsonPropertyName("voices")] public List<VoiceEntry> Voices { get; init; } = new(); }
    private sealed class VoiceEntry
    {
        [JsonPropertyName("voice_id")] public string VoiceId { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    public async Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
            req.Headers.Add("xi-api-key", credentials.ApiKey);
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<VoicesResponse>(body) ?? new VoicesResponse();
        return parsed.Voices.Select(v => new ProviderVoice { VoiceId = v.VoiceId, DisplayName = v.Name, Description = v.Description ?? "" }).ToList();
    }

    private const int SampleRate = 24000;

    public async Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var payload = new { text, model_id = modelId };

        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=wav_{SampleRate}");
            req.Headers.Add("xi-api-key", credentials.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        try
        {
            await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("subscription_required"))
        {
            throw new InvalidOperationException(
                "ElevenLabs: this account's plan doesn't support the requested WAV output tier. Try a lower sample " +
                "rate, upgrade the ElevenLabs plan, or map this VoiceType to a different provider.", ex);
        }

        // Native WAV container from ElevenLabs -- no wrapping needed.
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
