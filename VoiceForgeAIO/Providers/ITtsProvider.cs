namespace VoiceForgeAIO.Providers;

/// <summary>A voice as returned by a provider's voice-list endpoint, normalized for display.</summary>
internal sealed class ProviderVoice
{
    public string VoiceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    public override string ToString() =>
        string.IsNullOrEmpty(Description) ? DisplayName : $"{DisplayName} - {Description}";
}

/// <summary>
/// Credentials for one provider. Most providers only use ApiKey; MiniMax also
/// needs GroupId (passed as a query param alongside the bearer token).
/// </summary>
internal sealed class ProviderCredentials
{
    public string ApiKey { get; set; } = "";
    public string GroupId { get; set; } = "";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// One TTS backend. Implementations hide away each provider's own auth style,
/// request/response shape, and audio container -- callers just get bytes back.
/// </summary>
internal interface ITtsProvider
{
    /// <summary>Stable identifier used as a dictionary key and persisted in settings/mappings.</summary>
    string Id { get; }

    /// <summary>Display name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Model IDs offered in the model dropdown for this provider.</summary>
    IReadOnlyList<string> AvailableModels { get; }

    string DefaultModel { get; }

    /// <summary>
    /// True if this provider will hand back an uncompressed WAV/PCM file (native or via
    /// header-wrapping) for LIP generation. False means only compressed audio (e.g. mp3)
    /// is available in this version, so LIP generation must be skipped for its rows.
    /// </summary>
    bool SupportsWavOutput { get; }

    Task<List<ProviderVoice>> ListVoicesAsync(ProviderCredentials credentials, CancellationToken ct = default);

    Task<byte[]> SynthesizeAsync(ProviderCredentials credentials, string text, string voiceId, string modelId, CancellationToken ct = default);
}

/// <summary>Wraps raw 16-bit PCM samples in a standard 44-byte WAV/RIFF header.</summary>
internal static class WavUtil
{
    public static byte[] WrapPcm16(byte[] pcmData, int sampleRate, int channels = 1)
    {
        int bitsPerSample = 16;
        int blockAlign = channels * bitsPerSample / 8;
        int byteRate = sampleRate * blockAlign;
        int dataSize = pcmData.Length;

        using var ms = new MemoryStream(44 + dataSize);
        using (var w = new BinaryWriter(ms))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataSize);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write((short)blockAlign);
            w.Write((short)bitsPerSample);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize);
            w.Write(pcmData);
        }
        return ms.ToArray();
    }
}
