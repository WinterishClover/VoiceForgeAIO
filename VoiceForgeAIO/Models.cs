namespace VoiceForgeAIO;

/// <summary>One row of the VoiceType -> (Provider, Voice) mapping table.</summary>
internal sealed class VoiceMapEntry
{
    /// <summary>Skyrim voice type / folder name, e.g. "Kaylin".</summary>
    public string VoiceType { get; set; } = "";

    /// <summary>Provider Id (see Providers.TtsProviderRegistry), e.g. "ElevenLabs".</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>The provider-specific voice id to synthesize with for this VoiceType.</summary>
    public string VoiceId { get; set; } = "";

    /// <summary>Display name shown in the grid combo; not sent to any API.</summary>
    public string VoiceDisplay { get; set; } = "";
}

/// <summary>One row parsed from a batch CSV: EditorID,VoiceType,"Text".</summary>
internal sealed record CsvRow(string Id, string VoiceType, string Text);

/// <summary>Per-provider credentials as persisted to disk (encrypted at rest -- see PersistenceService).</summary>
internal sealed class StoredProviderCredentials
{
    public string ProviderId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string GroupId { get; set; } = ""; // only MiniMax uses this
}

/// <summary>One provider voice remembered from the last "Get Available Voices", so mappings work after a restart without another network call.</summary>
internal sealed class CachedVoice
{
    public string VoiceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class AppSettingsModel
{
    public List<StoredProviderCredentials> ProviderCredentials { get; set; } = new();

    public string LastCsvPath { get; set; } = "";
    public string LastVoiceFolderPath { get; set; } = "";
    public string LastProviderId { get; set; } = "Inworld";
    public string LastModelId { get; set; } = "";
    public int Concurrency { get; set; } = 3;

    public bool LipGenerationEnabled { get; set; } = false; // off by default, per design
    public string FaceFxWrapperPath { get; set; } = "";
    public string FonixDataPath { get; set; } = "";

    public bool FuzGenerationEnabled { get; set; } = false; // off by default; also requires LipGenerationEnabled
    public string XwmaEncodePath { get; set; } = "";
    public string LipFuzerPath { get; set; } = "";

    public bool HasSeenSplash { get; set; } = false;

    /// <summary>Skyrim's Data folder, where plugins are listed from. Empty = not set (auto-detect offered).</summary>
    public string SkyrimDataPath { get; set; } = "";
    /// <summary>Optional second place to look for plugins, e.g. Mod Organizer 2's "mods" folder (scanned one level deep).</summary>
    public string ExtraPluginFolder { get; set; } = "";
}
