using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceForgeAIO;

/// <summary>
/// Persists app settings (incl. per-provider API keys) and the voice mapping
/// table to %AppData%\VoiceForgeAIO\. API keys are encrypted at rest with
/// Windows DPAPI, tied to the current Windows user account -- nobody else's
/// Windows login (even on the same machine) can decrypt them, unlike the
/// Base64 "obscuring" used in earlier versions of this tool.
/// </summary>
internal static class PersistenceService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceForgeAIO");

    private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");
    private static readonly string VoiceMapPath = Path.Combine(AppDataDir, "voice_map.json");
    private static readonly string VoiceCachePath = Path.Combine(AppDataDir, "voice_cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // DPAPI "entropy" -- an extra secret mixed into encryption so another app on
    // the same Windows account can't casually decrypt this app's saved keys.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoiceForgeAIO-DPAPI-v1");

    private static void EnsureDir() => Directory.CreateDirectory(AppDataDir);

    // ---- DPAPI encryption for secrets -----------------------------------

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] encrypted = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";
        try
        {
            byte[] encrypted = Convert.FromBase64String(cipherText);
            byte[] plainBytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Wrong Windows account, corrupted value, or pre-DPAPI legacy data.
            return "";
        }
    }

    // ---- Settings ---------------------------------------------------------

    public static AppSettingsModel LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettingsModel();
            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettingsModel>(json) ?? new AppSettingsModel();
        }
        catch
        {
            return new AppSettingsModel();
        }
    }

    public static void SaveSettings(AppSettingsModel settings)
    {
        EnsureDir();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    // ---- Voice mapping ------------------------------------------------

    public static List<VoiceMapEntry> LoadVoiceMap()
    {
        try
        {
            if (!File.Exists(VoiceMapPath)) return new List<VoiceMapEntry>();
            string json = File.ReadAllText(VoiceMapPath);
            return JsonSerializer.Deserialize<List<VoiceMapEntry>>(json) ?? new List<VoiceMapEntry>();
        }
        catch
        {
            return new List<VoiceMapEntry>();
        }
    }

    public static void SaveVoiceMap(List<VoiceMapEntry> map)
    {
        EnsureDir();
        // Write to a temp file and swap, so a crash mid-save can't leave a half-written (unreadable) mapping.
        string tmp = VoiceMapPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map, JsonOptions));
        File.Move(tmp, VoiceMapPath, true);
    }

    // ---- Remembered voice lists (cloud providers) -----------------------

    public static Dictionary<string, List<CachedVoice>> LoadVoiceCache()
    {
        try
        {
            if (!File.Exists(VoiceCachePath)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, List<CachedVoice>>>(File.ReadAllText(VoiceCachePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void SaveVoiceCache(Dictionary<string, List<CachedVoice>> cache)
    {
        EnsureDir();
        File.WriteAllText(VoiceCachePath, JsonSerializer.Serialize(cache, JsonOptions));
    }
}
