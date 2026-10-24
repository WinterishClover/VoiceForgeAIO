using VoiceForgeAIO.LocalTts;
namespace VoiceForgeAIO.Providers;

/// <summary>Fake cloud provider so the real batch/bridge code can run offline. Text containing FAIL throws; SLOW waits.</summary>
internal sealed class FakeCloudProvider : ITtsProvider
{
    public string Id => "Fake"; public string DisplayName => "Fake Cloud";
    public IReadOnlyList<string> AvailableModels { get; } = new[] { "fake-1", "fake-2" };
    public string DefaultModel => "fake-1"; public bool SupportsWavOutput => true;
    public static int Calls; public static volatile bool VoicesFail; public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Models = new();
    public Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials c, CancellationToken ct = default)
    {
        if (VoicesFail) throw new InvalidOperationException("Fake HTTP 401: bad key");
        if (c.ApiKey != "good") throw new InvalidOperationException("Fake HTTP 401: bad key");
        return Task.FromResult(new List<ProviderVoice> { new() { VoiceId = "f1", DisplayName = "Fiona", Description = "bright" }, new() { VoiceId = "f2", DisplayName = "Gruff", Description = "low" } });
    }
    public async Task<byte[]> SynthesizeAsync(ProviderCredentials c, string text, string voiceId, string modelId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls); Models.Enqueue(modelId);
        if (text.Contains("SLOW")) await Task.Delay(1500, ct);
        if (text.Contains("FAIL")) throw new InvalidOperationException("Fake HTTP 500: boom");
        var pcm = new byte[16000 * 2]; for (int i = 0; i < 16000; i++) { var v = (short)(Math.Sin(i * 0.05) * 8000); pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8); }
        return WavUtil.WrapPcm16(pcm, 16000);
    }
}
internal static class TtsProviderRegistry
{
    public static readonly IReadOnlyList<ITtsProvider> All = new List<ITtsProvider>
    { new InworldProvider(), new ElevenLabsProvider(), new MiniMaxProvider(), new CartesiaProvider(), new FishAudioProvider(), new HumeOctaveProvider(), new OmniVoiceProvider(), new Qwen3TtsProvider(), new FakeCloudProvider() };
    private static readonly Dictionary<string, ITtsProvider> ById = All.ToDictionary(p => p.Id, p => p);
    public static ITtsProvider Get(string id) => ById.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException($"Unknown provider id '{id}'.");
    public static bool TryGet(string id, out ITtsProvider provider) => ById.TryGetValue(id, out provider!);
}
internal static class AudioFormatUtil { public const string OutputExtension = ".wav"; }
