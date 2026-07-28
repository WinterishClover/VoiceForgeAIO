using System.Text;
using System.Text.Json;

namespace VoiceForgeAIO.Providers;

internal sealed class HumeOctaveProvider : ITtsProvider
{
    public string Id => "HumeOctave";
    public string DisplayName => "Hume Octave";
    // Octave doesn't take a modelId in the request body (voice/version is tied to
    // the voice itself) -- this is a placeholder so the model dropdown has an entry.
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "octave-2" };
    public string DefaultModel => "octave-2";
    // /v0/tts/file supports an explicit "format" field (mp3/pcm/wav), defaulting to
    // mp3 if omitted -- we request wav explicitly below, so this is WAV-capable.
    public bool SupportsWavOutput => true;

    public async Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.hume.ai/v0/tts/voices?provider=CUSTOM_VOICE");
            req.Headers.Add("X-Hume-Api-Key", credentials.ApiKey);
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);

        // Exact response shape isn't confirmed in public docs at time of writing,
        // so parse defensively: find any array of objects and pull out whichever
        // id-like/name-like fields are present, rather than a rigid typed DTO
        // that would silently return zero voices if a field name is off.
        var voices = new List<ProviderVoice>();
        using var doc = JsonDocument.Parse(body);
        foreach (var arr in FindArrays(doc.RootElement))
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = GetFirstString(item, "id", "voice_id", "generation_id") ?? "";
                string name = GetFirstString(item, "name", "voice_name", "display_name") ?? id;
                if (!string.IsNullOrEmpty(id))
                    voices.Add(new ProviderVoice { VoiceId = id, DisplayName = name });
            }
            if (voices.Count > 0) break;
        }
        return voices;
    }

    private static IEnumerable<JsonElement> FindArrays(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { yield return element; yield break; }
        if (element.ValueKind != JsonValueKind.Object) yield break;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
                yield return prop.Value;
            else if (prop.Value.ValueKind == JsonValueKind.Object)
                foreach (var nested in FindArrays(prop.Value))
                    yield return nested;
        }
    }

    private static string? GetFirstString(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    public async Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var payload = new
        {
            utterances = new[]
            {
                string.IsNullOrWhiteSpace(voiceId)
                    ? new { text } as object
                    : new { text, voice = new { id = voiceId } }
            },
            format = new { type = "wav" }
        };

        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.hume.ai/v0/tts/file");
            req.Headers.Add("X-Hume-Api-Key", credentials.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
