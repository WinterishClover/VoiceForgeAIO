using VoiceForgeAIO.LocalTts;

namespace VoiceForgeAIO.Providers;

/// <summary>Qwen3-TTS running on this computer: no key and no per-line cost. A "voice" is one of the profiles saved on the Local Voices page
/// (described in free words, cloned from a recording, or a built-in speaker).</summary>
internal sealed class Qwen3TtsProvider : ITtsProvider
{
    public const string Model = "Qwen3-TTS 1.7B (local)";
    public string Id => "Qwen3TTS";
    public string DisplayName => "Qwen3-TTS (local)";
    public IReadOnlyList<string> AvailableModels { get; } = [Model];
    public string DefaultModel => Model;
    public bool SupportsWavOutput => true;
    public bool RequiresApiKey => false;

    private static Qwen3TtsService Service => Qwen3TtsService.Shared ?? throw new InvalidOperationException("Qwen3-TTS is not available in this build.");

    public Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default) =>
        Task.FromResult(Service.Voices.Select(v => new ProviderVoice { VoiceId = v.Id, DisplayName = v.Name,
            Description = v.Kind == "clone" ? "cloned from a recording" : v.Kind == "preset" ? "preset speaker " + v.Speaker : v.Instruct }).ToList());

    public Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var voice = Service.Voices.FirstOrDefault(v => v.Id == voiceId)
            ?? throw new InvalidOperationException("This Qwen3-TTS voice no longer exists. Pick another voice in the mapping (Local Voices page → Qwen3-TTS voices).");
        return Service.SpeakAsync(voice, text, credentials.Emotion, ct);
    }
}
