using VoiceForgeAIO.LocalTts;

namespace VoiceForgeAIO.Providers;

/// <summary>OmniVoice running on this computer: no key and no per-line cost. A "voice" is one of the profiles saved on the Local Voices page
/// (described in words, or cloned from a short recording); the model choice sets the number of steps (more steps, better and slower).</summary>
internal sealed class OmniVoiceProvider : ITtsProvider
{
    public const string Best = "OmniVoice · 32 steps (best)", Fast = "OmniVoice · 16 steps (fast)";
    public string Id => "OmniVoice";
    public string DisplayName => "OmniVoice (local)";
    public IReadOnlyList<string> AvailableModels { get; } = [Best, Fast];
    public string DefaultModel => Best;
    public bool SupportsWavOutput => true;
    public bool RequiresApiKey => false;

    private static OmniVoiceService Service => OmniVoiceService.Shared ?? throw new InvalidOperationException("OmniVoice is not available in this build.");

    public static int StepsFor(string modelId) => modelId.Contains("16") ? 16 : 32;

    public Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default) =>
        Task.FromResult(Service.Voices.Select(v => new ProviderVoice { VoiceId = v.Id, DisplayName = v.Name,
            Description = v.Kind == "clone" ? "cloned from a recording" : v.Kind == "auto" ? "chosen by the model" : v.Instruct }).ToList());

    public Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        var voice = Service.Voices.FirstOrDefault(v => v.Id == voiceId)
            ?? throw new InvalidOperationException("This OmniVoice voice no longer exists. Pick another voice in the mapping (Local Voices page → OmniVoice voices).");
        return Service.SpeakAsync(voice, text, StepsFor(modelId), ct);
    }
}
