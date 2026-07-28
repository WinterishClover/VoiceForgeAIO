using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceForgeAIO.Providers;

internal sealed class MiniMaxProvider : ITtsProvider
{
    public string Id => "MiniMax";
    public string DisplayName => "MiniMax";
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "speech-2.6-hd", "speech-2.6-turbo", "speech-02-hd", "speech-02-turbo" };
    public string DefaultModel => "speech-2.6-hd";
    // We request audio_setting.format="pcm" and wrap it ourselves.
    public bool SupportsWavOutput => true;

    private const int SampleRate = 32000;

    private sealed class BaseResp { [JsonPropertyName("status_code")] public int StatusCode { get; init; } [JsonPropertyName("status_msg")] public string StatusMsg { get; init; } = ""; }

    private sealed class VoiceEntry
    {
        [JsonPropertyName("voice_id")] public string VoiceId { get; init; } = "";
        [JsonPropertyName("voice_name")] public string VoiceName { get; init; } = "";
    }
    private sealed class VoicesResponse
    {
        [JsonPropertyName("system_voice")] public List<VoiceEntry>? SystemVoice { get; init; }
        [JsonPropertyName("voice_cloning")] public List<VoiceEntry>? VoiceCloning { get; init; }
        [JsonPropertyName("voice_generation")] public List<VoiceEntry>? VoiceGeneration { get; init; }
        [JsonPropertyName("base_resp")] public BaseResp? BaseResp { get; init; }
    }

    public async Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        var payload = new { voice_type = "all" };
        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.minimax.io/v1/get_voice?GroupId={Uri.EscapeDataString(credentials.GroupId)}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<VoicesResponse>(body) ?? new VoicesResponse();
        if (parsed.BaseResp is { StatusCode: not 0 })
            throw new InvalidOperationException($"{DisplayName}: {parsed.BaseResp.StatusMsg}");

        var all = new List<VoiceEntry>();
        if (parsed.SystemVoice != null) all.AddRange(parsed.SystemVoice);
        if (parsed.VoiceCloning != null) all.AddRange(parsed.VoiceCloning);
        if (parsed.VoiceGeneration != null) all.AddRange(parsed.VoiceGeneration);
        return all.Select(v => new ProviderVoice { VoiceId = v.VoiceId, DisplayName = v.VoiceName }).ToList();
    }

    private sealed class SynthResponse
    {
        [JsonPropertyName("data")] public DataObj? Data { get; init; }
        [JsonPropertyName("base_resp")] public BaseResp? BaseResp { get; init; }
    }
    private sealed class DataObj { [JsonPropertyName("audio")] public string? Audio { get; init; } }

    public async Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var payload = new
        {
            model = modelId,
            text,
            stream = false,
            voice_setting = new { voice_id = voiceId, speed = 1.0, vol = 1.0, pitch = 0 },
            audio_setting = new { sample_rate = SampleRate, bitrate = 128000, format = "pcm", channel = 1 }
        };

        var response = await HttpHelper.SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.minimax.io/v1/t2a_v2?GroupId={Uri.EscapeDataString(credentials.GroupId)}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }, ct);

        await HttpHelper.ThrowIfUnsuccessful(response, DisplayName);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<SynthResponse>(body);
        if (parsed?.BaseResp is { StatusCode: not 0 })
            throw new InvalidOperationException($"{DisplayName}: {parsed.BaseResp.StatusMsg}");
        if (parsed?.Data?.Audio is null)
            throw new InvalidOperationException($"{DisplayName}: response had no audio data.");

        byte[] pcm = Convert.FromHexString(parsed.Data.Audio);
        return WavUtil.WrapPcm16(pcm, SampleRate);
    }
}
