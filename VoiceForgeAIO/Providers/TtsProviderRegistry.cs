namespace VoiceForgeAIO.Providers;

/// <summary>Central list of every TTS provider VoiceForge AIO knows about.</summary>
internal static class TtsProviderRegistry
{
    public static readonly IReadOnlyList<ITtsProvider> All = new List<ITtsProvider>
    {
        new InworldProvider(),
        new ElevenLabsProvider(),
        new MiniMaxProvider(),
        new CartesiaProvider(),
        new FishAudioProvider(),
        new HumeOctaveProvider(),
    };

    private static readonly Dictionary<string, ITtsProvider> ById =
        All.ToDictionary(p => p.Id, p => p);

    public static ITtsProvider Get(string id) => ById.TryGetValue(id, out var p)
        ? p
        : throw new KeyNotFoundException($"Unknown provider id '{id}'.");

    public static bool TryGet(string id, out ITtsProvider provider) => ById.TryGetValue(id, out provider!);
}

internal static class AudioFormatUtil
{
    /// <summary>
    /// Every provider is asked for WAV/PCM (wrapped or native) in this version, so
    /// output is always .wav -- kept as a helper in case that changes per-provider later.
    /// </summary>
    public const string OutputExtension = ".wav";
}
