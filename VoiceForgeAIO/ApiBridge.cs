using System.Text.Json;
using System.Text.Json.Serialization;
using Photino.NET;
using VoiceForgeAIO.LocalTts;
using VoiceForgeAIO.PluginDialogue;
using VoiceForgeAIO.Providers;

namespace VoiceForgeAIO;

/// <summary>
/// Handles every message the web UI sends over Photino's WebMessage channel.
/// Protocol: JS sends {"id":"...", "method":"...", "args": {...}} via
/// window.external.sendMessage(...); this replies with
/// {"id":"...", "ok":true/false, "result":..., "error":"..."} via
/// window.SendWebMessage(...). Unsolicited pushes (log lines, batch progress) use
/// {"event":"...", "data":...} with no "id".
///
/// This class is intentionally just plumbing -- every actual TTS/LIP/FUZ/file
/// operation is delegated to the same backend classes the WinForms version used
/// (Providers/*, BatchProcessor, PersistenceService, FaceFxService, etc.), none of
/// which changed.
/// </summary>
internal sealed class ApiBridge
{
    public PhotinoWindow? Window { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private CancellationTokenSource? _batchCts;
    private ManualResetEventSlim _pauseGate = new(true);
    private bool _batchRunning;

    public async Task HandleMessageAsync(string rawMessage, PhotinoWindow window)
    {
        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(rawMessage);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            string method = root.GetProperty("method").GetString() ?? "";
            JsonElement args = root.TryGetProperty("args", out var a) ? a : default;

            object? result = await DispatchAsync(method, args, window);
            SendResponse(window, id, true, result, null);
        }
        catch (Exception ex)
        {
            SendResponse(window, id, false, null, ex.Message);
        }
    }

    private Task<object?> DispatchAsync(string method, JsonElement args, PhotinoWindow window) => method switch
    {
        "getInitialState" => Task.FromResult<object?>(GetInitialState()),
        "saveSettings" => Task.FromResult<object?>(SaveSettings(args)),
        "browseOpenFile" => Task.FromResult<object?>(BrowseOpenFile(args, window)),
        "browseOpenFolder" => Task.FromResult<object?>(BrowseOpenFolder(args, window)),
        "browseSaveFile" => Task.FromResult<object?>(BrowseSaveFile(args, window)),
        "refreshAllVoices" => RefreshAllVoicesAsync(args),
        "saveVoiceMap" => Task.FromResult<object?>(SaveVoiceMap(args)),
        "exportVoiceMapCsv" => Task.FromResult<object?>(ExportVoiceMapCsv(args)),
        "importVoiceMapCsv" => Task.FromResult<object?>(ImportVoiceMapCsv(args)),
        "generateSingleLine" => GenerateSingleLineAsync(args),
        "startBatch" => StartBatch(args, window),
        "pauseBatch" => Task.FromResult<object?>(TogglePause()),
        "stopBatch" => Task.FromResult<object?>(StopBatch()),
        "markSplashSeen" => Task.FromResult<object?>(MarkSplashSeen()),
        "openExternalUrl" => Task.FromResult<object?>(OpenExternalUrl(args)),
        "detectDataFolder" => Task.FromResult<object?>(DetectDataFolder()),
        "listPlugins" => Task.FromResult<object?>(ListPlugins(args)),
        "readPlugin" => ReadPluginAsync(args),
        "getPluginLines" => Task.FromResult<object?>(GetPluginLines(args)),
        "exportPluginCsv" => Task.FromResult<object?>(ExportPluginCsv(args)),
        "omnivoice.get" => Task.FromResult<object?>(OmniVoiceState()),
        "omnivoice.save" => Task.FromResult<object?>(OmniVoiceSave(args)),
        "omnivoice.check" => OmniVoiceCheckAsync(),
        "omnivoice.stop" => Task.FromResult<object?>(OmniVoiceStop()),
        "omnivoice.voice.save" => Task.FromResult<object?>(OmniVoiceSaveVoice(args)),
        "omnivoice.voice.delete" => Task.FromResult<object?>(OmniVoiceDeleteVoice(args)),
        "omnivoice.reference" => Task.FromResult<object?>(OmniVoiceAddReference(args)),
        "omnivoice.sample" => OmniVoiceSampleAsync(args),
        "qwen3tts.get" => Task.FromResult<object?>(Qwen3TtsState()),
        "qwen3tts.save" => Task.FromResult<object?>(Qwen3TtsSave(args)),
        "qwen3tts.check" => Qwen3TtsCheckAsync(),
        "qwen3tts.stop" => Task.FromResult<object?>(Qwen3TtsStop()),
        "qwen3tts.voice.save" => Task.FromResult<object?>(Qwen3TtsSaveVoice(args)),
        "qwen3tts.voice.delete" => Task.FromResult<object?>(Qwen3TtsDeleteVoice(args)),
        "qwen3tts.reference" => Task.FromResult<object?>(Qwen3TtsAddReference(args)),
        "qwen3tts.sample" => Qwen3TtsSampleAsync(args),
        _ => throw new InvalidOperationException($"Unknown method: {method}")
    };

    // ------------------------------------------------------------------
    // Initial state / settings
    // ------------------------------------------------------------------

    private object GetInitialState()
    {
        var s = PersistenceService.LoadSettings();
        var providerKeys = new Dictionary<string, object>();
        foreach (var provider in TtsProviderRegistry.All)
        {
            var stored = s.ProviderCredentials.FirstOrDefault(c => c.ProviderId == provider.Id);
            providerKeys[provider.Id] = new
            {
                apiKey = stored != null ? PersistenceService.Decrypt(stored.ApiKey) : "",
                groupId = stored != null ? PersistenceService.Decrypt(stored.GroupId) : ""
            };
        }

        return new
        {
            providers = TtsProviderRegistry.All.Select(p => new
            {
                id = p.Id,
                displayName = p.DisplayName,
                models = p.AvailableModels,
                defaultModel = p.DefaultModel,
                supportsWav = p.SupportsWavOutput,
                requiresApiKey = p.RequiresApiKey
            }),
            providerKeys,
            settings = new
            {
                faceFxWrapperPath = s.FaceFxWrapperPath,
                fonixDataPath = s.FonixDataPath,
                xwmaEncodePath = s.XwmaEncodePath,
                lipFuzerPath = s.LipFuzerPath,
                lastCsvPath = s.LastCsvPath,
                lastVoiceFolderPath = s.LastVoiceFolderPath,
                concurrency = s.Concurrency,
                lipGenerationEnabled = s.LipGenerationEnabled,
                fuzGenerationEnabled = s.FuzGenerationEnabled,
                lastProviderId = s.LastProviderId,
                defaultModels = s.DefaultModels,
                skyrimDataPath = s.SkyrimDataPath,
                extraPluginFolder = s.ExtraPluginFolder
            },
            voiceMap = PersistenceService.LoadVoiceMap().Select(ToWireVoiceMapEntry),
            cachedVoices = PersistenceService.LoadVoiceCache().ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(v => new { voiceId = v.VoiceId, displayName = v.DisplayName, description = v.Description })),
            hasSeenSplash = s.HasSeenSplash
        };
    }

    private sealed class SaveSettingsRequest
    {
        public Dictionary<string, ProviderKeyPair> ProviderKeys { get; set; } = new();
        public string FaceFxWrapperPath { get; set; } = "";
        public string FonixDataPath { get; set; } = "";
        public string XwmaEncodePath { get; set; } = "";
        public string LipFuzerPath { get; set; } = "";
        public string LastCsvPath { get; set; } = "";
        public string LastVoiceFolderPath { get; set; } = "";
        public int Concurrency { get; set; } = 3;
        public bool LipGenerationEnabled { get; set; }
        public bool FuzGenerationEnabled { get; set; }
        public string LastProviderId { get; set; } = "Inworld";
        public string SkyrimDataPath { get; set; } = "";
        public string ExtraPluginFolder { get; set; } = "";
        public Dictionary<string, string> DefaultModels { get; set; } = new();
    }
    private sealed class ProviderKeyPair { public string ApiKey { get; set; } = ""; public string GroupId { get; set; } = ""; }

    private object SaveSettings(JsonElement args)
    {
        var req = args.Deserialize<SaveSettingsRequest>(JsonOpts) ?? new SaveSettingsRequest();
        var s = PersistenceService.LoadSettings();

        s.ProviderCredentials = TtsProviderRegistry.All.Select(p =>
        {
            req.ProviderKeys.TryGetValue(p.Id, out var pair);
            pair ??= new ProviderKeyPair();
            return new StoredProviderCredentials
            {
                ProviderId = p.Id,
                ApiKey = PersistenceService.Encrypt(pair.ApiKey.Trim()),
                GroupId = PersistenceService.Encrypt(pair.GroupId.Trim())
            };
        }).ToList();

        s.FaceFxWrapperPath = req.FaceFxWrapperPath;
        s.FonixDataPath = req.FonixDataPath;
        s.XwmaEncodePath = req.XwmaEncodePath;
        s.LipFuzerPath = req.LipFuzerPath;
        s.LastCsvPath = req.LastCsvPath;
        s.LastVoiceFolderPath = req.LastVoiceFolderPath;
        s.Concurrency = req.Concurrency;
        s.LipGenerationEnabled = req.LipGenerationEnabled;
        s.FuzGenerationEnabled = req.FuzGenerationEnabled;
        s.LastProviderId = req.LastProviderId;
        s.DefaultModels = req.DefaultModels
            .Where(kv => TtsProviderRegistry.TryGet(kv.Key, out var p) && p.AvailableModels.Contains(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        s.SkyrimDataPath = req.SkyrimDataPath.Trim();
        s.ExtraPluginFolder = req.ExtraPluginFolder.Trim();

        PersistenceService.SaveSettings(s);
        return new { ok = true };
    }

    // ------------------------------------------------------------------
    // Native file/folder dialogs
    // ------------------------------------------------------------------

    private class BrowseFileRequest
    {
        public string Title { get; set; } = "";
        public string? DefaultPath { get; set; }
        public string FilterName { get; set; } = "Files";
        public string[] FilterExtensions { get; set; } = Array.Empty<string>();
    }

    private object? BrowseOpenFile(JsonElement args, PhotinoWindow window)
    {
        var req = args.Deserialize<BrowseFileRequest>(JsonOpts) ?? new BrowseFileRequest();
        var results = window.ShowOpenFile(req.Title, req.DefaultPath, false,
            new (string, string[])[] { (req.FilterName, req.FilterExtensions) });
        return results is { Length: > 0 } ? results[0] : null;
    }

    private object? BrowseOpenFolder(JsonElement args, PhotinoWindow window)
    {
        var req = args.Deserialize<BrowseFileRequest>(JsonOpts) ?? new BrowseFileRequest();
        var results = window.ShowOpenFolder(req.Title, req.DefaultPath, false);
        return results is { Length: > 0 } ? results[0] : null;
    }

    private sealed class BrowseSaveRequest : BrowseFileRequest { public string? DefaultFileName { get; set; } }

    private object? BrowseSaveFile(JsonElement args, PhotinoWindow window)
    {
        var req = args.Deserialize<BrowseSaveRequest>(JsonOpts) ?? new BrowseSaveRequest();
        return window.ShowSaveFile(req.Title, req.DefaultPath,
            new (string, string[])[] { (req.FilterName, req.FilterExtensions) });
    }

    // ------------------------------------------------------------------
    // Voices
    // ------------------------------------------------------------------

    private sealed class RefreshVoicesRequest { public Dictionary<string, ProviderKeyPair> ProviderKeys { get; set; } = new(); }

    private async Task<object?> RefreshAllVoicesAsync(JsonElement args)
    {
        var req = args.Deserialize<RefreshVoicesRequest>(JsonOpts) ?? new RefreshVoicesRequest();
        var byProvider = new Dictionary<string, object>();
        var errors = new Dictionary<string, string>();
        var cache = PersistenceService.LoadVoiceCache();

        foreach (var provider in TtsProviderRegistry.All)
        {
            // Local providers have no key; their voices are the ones made on the Local Voices page.
            req.ProviderKeys.TryGetValue(provider.Id, out var pair);
            pair ??= new ProviderKeyPair();
            if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(pair.ApiKey))
                continue;

            try
            {
                PushLog($"Fetching voices from {provider.DisplayName}...");
                var creds = new ProviderCredentials { ApiKey = pair.ApiKey, GroupId = pair.GroupId };
                var voices = await provider.ListVoicesAsync(creds);
                byProvider[provider.Id] = voices.Select(v => new { voiceId = v.VoiceId, displayName = v.DisplayName, description = v.Description });
                if (provider.RequiresApiKey)
                    cache[provider.Id] = voices.Select(v => new CachedVoice { VoiceId = v.VoiceId, DisplayName = v.DisplayName, Description = v.Description }).ToList();
                PushLog($"{provider.DisplayName}: {voices.Count} voice(s) loaded.");
            }
            catch (Exception ex)
            {
                errors[provider.Id] = ex.Message;
                PushLog($"{provider.DisplayName} failed: {ex.Message}");
            }
        }

        try { PersistenceService.SaveVoiceCache(cache); }
        catch (Exception ex) { PushLog($"Could not remember the voice lists: {ex.Message}"); }

        return new { voicesByProvider = byProvider, errors };
    }

    // ------------------------------------------------------------------
    // Voice mapping
    // ------------------------------------------------------------------

    private sealed class WireVoiceMapEntry
    {
        public string VoiceType { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string VoiceId { get; set; } = "";
        public string VoiceDisplay { get; set; } = "";
        public string ModelId { get; set; } = "";
    }

    private static object ToWireVoiceMapEntry(VoiceMapEntry e) => new
    {
        voiceType = e.VoiceType,
        providerId = e.ProviderId,
        voiceId = e.VoiceId,
        voiceDisplay = e.VoiceDisplay,
        modelId = e.ModelId
    };

    private object SaveVoiceMap(JsonElement args)
    {
        var entries = args.GetProperty("entries").Deserialize<List<WireVoiceMapEntry>>(JsonOpts) ?? new();
        var list = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.VoiceType))
            .Select(e => new VoiceMapEntry
            {
                VoiceType = e.VoiceType.Trim(),
                ProviderId = e.ProviderId,
                VoiceId = e.VoiceId,
                VoiceDisplay = e.VoiceDisplay,
                ModelId = e.ModelId ?? ""
            }).ToList();
        PersistenceService.SaveVoiceMap(list);
        return new { ok = true, count = list.Count };
    }

    private object ExportVoiceMapCsv(JsonElement args)
    {
        string path = args.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing path.");
        var entries = args.GetProperty("entries").Deserialize<List<WireVoiceMapEntry>>(JsonOpts) ?? new();

        var lines = new List<string> { "VoiceType,ProviderId,VoiceId,VoiceDisplay,ModelId" };
        lines.AddRange(entries.Select(e =>
            $"{CsvService.Escape(e.VoiceType.Trim())},{CsvService.Escape(e.ProviderId)},{CsvService.Escape(e.VoiceId)},{CsvService.Escape(e.VoiceDisplay)},{CsvService.Escape(e.ModelId ?? "")}"));
        File.WriteAllLines(path, lines);
        return new { ok = true, count = entries.Count };
    }

    /// <summary>The registered provider id for a name typed in any case ("elevenlabs" -> "ElevenLabs"), or null.</summary>
    private static string? CanonicalProviderId(string name) =>
        TtsProviderRegistry.All.FirstOrDefault(p => p.Id.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Reads a mapping CSV. Understands the current format (VoiceType,ProviderId,VoiceId,VoiceDisplay)
    /// and the older one without a Provider column (VoiceType,VoiceId,VoiceDisplay): for those rows the
    /// provider is left blank and flagged, because the file can't say which provider the voice belongs to.
    /// </summary>
    private object ImportVoiceMapCsv(JsonElement args)
    {
        string path = args.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing path.");
        var imported = new List<object>();
        var warnings = new List<string>();

        bool? headerHasProvider = null;
        bool firstRecord = true;
        foreach (var (lineNum, fields) in CsvService.ParseRecords(File.ReadAllText(path)))
        {
            if (fields.Count < 3) { warnings.Add($"Line {lineNum}: skipped (expected at least 3 columns)."); continue; }

            if (firstRecord && fields[0].Trim().Equals("VoiceType", StringComparison.OrdinalIgnoreCase))
            {
                headerHasProvider = fields[1].Trim().Equals("ProviderId", StringComparison.OrdinalIgnoreCase)
                                    || fields[1].Trim().Equals("Provider", StringComparison.OrdinalIgnoreCase);
                firstRecord = false;
                continue;
            }
            firstRecord = false;

            string voiceType = fields[0].Trim();
            if (voiceType.Length == 0) { warnings.Add($"Line {lineNum}: skipped (empty VoiceType)."); continue; }

            string? providerId = CanonicalProviderId(fields[1]);
            // With a header the format is known; without one, a recognised provider name in column 2 means the current format.
            bool currentFormat = headerHasProvider ?? (providerId != null && fields.Count >= 3);

            if (currentFormat)
            {
                if (providerId == null)
                    warnings.Add($"Line {lineNum}: '{voiceType}' uses an unknown provider '{fields[1].Trim()}'; pick its provider in the table.");
                imported.Add(new
                {
                    voiceType, providerId = providerId ?? "", voiceId = fields[2].Trim(),
                    voiceDisplay = fields.Count >= 4 ? fields[3].Trim() : "",
                    modelId = fields.Count >= 5 ? fields[4].Trim() : "", needsProvider = providerId == null
                });
            }
            else
            {
                warnings.Add($"Line {lineNum}: '{voiceType}' is in the older format with no Provider column; pick its provider in the table.");
                imported.Add(new
                {
                    voiceType, providerId = "", voiceId = fields[1].Trim(),
                    voiceDisplay = fields[2].Trim(), modelId = "", needsProvider = true
                });
            }
        }
        return new { entries = imported, warnings };
    }

    // ------------------------------------------------------------------
    // Plugin dialogue (read lines straight from a plugin; no xEdit, no CSV)
    // ------------------------------------------------------------------

    private volatile PluginDialogue.PluginDialogue? _loadedPlugin;
    private List<PluginFile> _knownPlugins = new();

    private static object DetectDataFolder()
    {
        var path = PluginDialogueService.DetectDataFolder();
        return new { path = path ?? "", found = path != null };
    }

    private sealed class ListPluginsRequest { public string DataPath { get; set; } = ""; public string ExtraPath { get; set; } = ""; public bool IncludeOfficial { get; set; } }

    private object ListPlugins(JsonElement args)
    {
        var req = args.Deserialize<ListPluginsRequest>(JsonOpts) ?? new ListPluginsRequest();
        if (!string.IsNullOrWhiteSpace(req.DataPath) && !Directory.Exists(req.DataPath.Trim()))
            throw new InvalidOperationException($"The Skyrim Data folder doesn't exist: {req.DataPath}");
        _knownPlugins = PluginDialogueService.ScanPlugins(req.DataPath, req.ExtraPath);
        var shown = _knownPlugins.Where(p => req.IncludeOfficial || !PluginDialogueService.IsOfficialFile(p.Name)).ToList();
        PushLog($"Found {_knownPlugins.Count} plugin(s) ({shown.Count} shown).");
        return new { plugins = shown.Select(p => new { name = p.Name, path = p.Path, source = p.Source, sizeKb = (p.Size + 1023) / 1024 }) };
    }

    private sealed class ReadPluginRequest { public string PluginPath { get; set; } = ""; public string DataPath { get; set; } = ""; public string ExtraPath { get; set; } = ""; }

    private async Task<object?> ReadPluginAsync(JsonElement args)
    {
        var req = args.Deserialize<ReadPluginRequest>(JsonOpts) ?? new ReadPluginRequest();
        if (string.IsNullOrWhiteSpace(req.PluginPath)) throw new InvalidOperationException("Choose a plugin first.");
        // Masters are looked up among the scanned plugins; rescan if the list is stale or empty.
        var available = _knownPlugins.Count > 0 ? _knownPlugins : PluginDialogueService.ScanPlugins(req.DataPath, req.ExtraPath);
        PushLog($"Reading {Path.GetFileName(req.PluginPath)}...");
        var dialogue = await Task.Run(() => PluginDialogueService.Read(req.PluginPath, available));
        _loadedPlugin = dialogue;
        foreach (var w in dialogue.Warnings) PushLog($"Warning: {w}");
        PushLog($"{dialogue.PluginName}: {dialogue.Lines.Count} line(s) across {dialogue.Lines.Select(l => l.VoiceType).Distinct(StringComparer.OrdinalIgnoreCase).Count()} VoiceType(s).");

        var voiceTypes = dialogue.Lines.GroupBy(l => l.VoiceType, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                name = g.Key,
                lines = g.Count(),
                speakers = g.Select(l => l.Speakers).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList()
            }).ToList();
        // Where the game looks for the finished files; offered as the batch output folder.
        string suggested = string.IsNullOrWhiteSpace(req.DataPath) ? "" : Path.Combine(req.DataPath.Trim(), "Sound", "Voice", dialogue.PluginName);
        return new { pluginName = dialogue.PluginName, lineCount = dialogue.Lines.Count, voiceTypes, warnings = dialogue.Warnings, suggestedVoiceFolder = suggested };
    }

    private sealed class PluginLinesRequest { public int Skip { get; set; } public int Take { get; set; } = 200; public string? Search { get; set; } public List<string>? VoiceTypes { get; set; } }

    private object GetPluginLines(JsonElement args)
    {
        var req = args.Deserialize<PluginLinesRequest>(JsonOpts) ?? new PluginLinesRequest();
        var loaded = _loadedPlugin ?? throw new InvalidOperationException("No plugin is loaded.");
        IEnumerable<DialogueLine> lines = loaded.Lines;
        if (req.VoiceTypes is { Count: > 0 })
        {
            var set = new HashSet<string>(req.VoiceTypes, StringComparer.OrdinalIgnoreCase);
            lines = lines.Where(l => set.Contains(l.VoiceType));
        }
        if (!string.IsNullOrWhiteSpace(req.Search))
            lines = lines.Where(l => l.Text.Contains(req.Search, StringComparison.OrdinalIgnoreCase) || l.Id.Contains(req.Search, StringComparison.OrdinalIgnoreCase)
                                     || l.Speakers.Contains(req.Search, StringComparison.OrdinalIgnoreCase));
        var all = lines.ToList();
        return new
        {
            total = all.Count,
            lines = all.Skip(Math.Max(0, req.Skip)).Take(Math.Clamp(req.Take, 1, 1000))
                .Select(l => new { id = l.Id, voiceType = l.VoiceType, speakers = l.Speakers, text = l.Text })
        };
    }

    private sealed class ExportPluginRequest { public string Path { get; set; } = ""; public List<string>? IncludeVoiceTypes { get; set; } }

    /// <summary>Writes the loaded plugin's lines as a CSV in the tool's format, for anyone who still wants the file (or another tool).</summary>
    private object ExportPluginCsv(JsonElement args)
    {
        var req = args.Deserialize<ExportPluginRequest>(JsonOpts) ?? new ExportPluginRequest();
        var loaded = _loadedPlugin ?? throw new InvalidOperationException("No plugin is loaded.");
        var include = req.IncludeVoiceTypes is { Count: > 0 } ? new HashSet<string>(req.IncludeVoiceTypes, StringComparer.OrdinalIgnoreCase) : null;
        var lines = loaded.Lines.Where(l => include is null || include.Contains(l.VoiceType)).ToList();
        File.WriteAllLines(req.Path, lines.Select(l => $"{CsvService.Escape(l.Id)},{CsvService.Escape(l.VoiceType)},\"{l.Text.Replace("\"", "\"\"")}\""));
        return new { ok = true, count = lines.Count };
    }

    // ------------------------------------------------------------------
    // Single-line generate
    // ------------------------------------------------------------------

    private sealed class SingleLineRequest
    {
        public string ProviderId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string VoiceId { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string Text { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string FileName { get; set; } = "";
    }

    private async Task<object?> GenerateSingleLineAsync(JsonElement args)
    {
        var req = args.Deserialize<SingleLineRequest>(JsonOpts) ?? throw new InvalidOperationException("Invalid request.");
        if (!TtsProviderRegistry.TryGet(req.ProviderId, out var provider))
            throw new InvalidOperationException($"Unknown provider '{req.ProviderId}'.");

        string fileName = string.IsNullOrWhiteSpace(req.FileName) ? "line1.wav" : req.FileName;
        if (!Path.HasExtension(fileName)) fileName += ".wav";
        string outPath = Path.Combine(req.OutputPath, fileName);

        var creds = new ProviderCredentials { ApiKey = req.ApiKey, GroupId = req.GroupId };
        byte[] audio = await provider.SynthesizeAsync(creds, req.Text, req.VoiceId, req.ModelId);
        Directory.CreateDirectory(req.OutputPath);
        await File.WriteAllBytesAsync(outPath, WavHeaderFixup.NormalizeHeader(audio));

        return new { ok = true, path = outPath };
    }

    // ------------------------------------------------------------------
    // CSV batch
    // ------------------------------------------------------------------

    private sealed class StartBatchRequest
    {
        public string CsvPath { get; set; } = "";
        /// <summary>"csv" (default) reads CsvPath; "plugin" uses the dialogue of the plugin loaded on the Plugin Dialogue page.</summary>
        public string Source { get; set; } = "csv";
        /// <summary>For the plugin source: only these VoiceTypes (null/empty = all).</summary>
        public List<string>? IncludeVoiceTypes { get; set; }
        /// <summary>The model chosen per provider in Settings (providerId -> model), as currently shown.</summary>
        public Dictionary<string, string> DefaultModels { get; set; } = new();
        public string VoiceFolder { get; set; } = "";
        public int Concurrency { get; set; } = 3;
        public bool Overwrite { get; set; }
        public bool GenerateLip { get; set; }
        public bool GenerateFuz { get; set; }
        public string FaceFxWrapperPath { get; set; } = "";
        public string FonixDataPath { get; set; } = "";
        public string XwmaEncodePath { get; set; } = "";
        public string LipFuzerPath { get; set; } = "";
        public Dictionary<string, ProviderKeyPair> ProviderKeys { get; set; } = new();
        public List<WireVoiceMapEntry> VoiceMap { get; set; } = new();
    }

    private Task<object?> StartBatch(JsonElement args, PhotinoWindow window)
    {
        if (_batchRunning) return Task.FromResult<object?>(new { ok = false, error = "A batch is already running." });

        var req = args.Deserialize<StartBatchRequest>(JsonOpts) ?? throw new InvalidOperationException("Invalid request.");

        List<CsvRow> rows;
        string sourceLabel;
        if (req.Source == "plugin")
        {
            var loaded = _loadedPlugin;
            if (loaded is null)
                return Task.FromResult<object?>(new { ok = false, error = "No plugin is loaded. Open the Plugin Dialogue page and read a plugin first." });
            var include = req.IncludeVoiceTypes is { Count: > 0 } ? new HashSet<string>(req.IncludeVoiceTypes, StringComparer.OrdinalIgnoreCase) : null;
            rows = loaded.Lines.Where(l => include is null || include.Contains(l.VoiceType)).Select(l => new CsvRow(l.Id, l.VoiceType, l.Text)).ToList();
            sourceLabel = loaded.PluginName;
            if (rows.Count == 0)
                return Task.FromResult<object?>(new { ok = false, error = "No lines are selected. Tick at least one VoiceType on the Plugin Dialogue page." });
        }
        else
        {
            try { rows = CsvService.ReadRows(req.CsvPath); }
            catch (Exception ex) { return Task.FromResult<object?>(new { ok = false, error = $"Failed to parse CSV: {ex.Message}" }); }
            sourceLabel = req.CsvPath;
        }

        var voiceMap = new Dictionary<string, VoiceMapEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in req.VoiceMap)
        {
            if (string.IsNullOrWhiteSpace(e.VoiceType) || string.IsNullOrWhiteSpace(e.VoiceId)) continue;
            voiceMap[e.VoiceType.Trim()] = new VoiceMapEntry { VoiceType = e.VoiceType.Trim(), ProviderId = e.ProviderId, VoiceId = e.VoiceId, VoiceDisplay = e.VoiceDisplay, ModelId = e.ModelId ?? "" };
        }

        var credentials = req.ProviderKeys.ToDictionary(
            kv => kv.Key,
            kv => new ProviderCredentials { ApiKey = kv.Value.ApiKey, GroupId = kv.Value.GroupId });

        var options = new BatchOptions(
            VoiceFolderRoot: req.VoiceFolder,
            Concurrency: Math.Max(1, req.Concurrency),
            Overwrite: req.Overwrite,
            GenerateLip: req.GenerateLip,
            FaceFxWrapperPath: req.FaceFxWrapperPath,
            FonixDataPath: req.FonixDataPath,
            GenerateFuz: req.GenerateFuz,
            XwmaEncodePath: req.XwmaEncodePath,
            LipFuzerPath: req.LipFuzerPath);

        _batchCts = new CancellationTokenSource();
        _pauseGate = new ManualResetEventSlim(true);
        _batchRunning = true;

        string pipelineNote = options.GenerateFuz ? " (with LIP + FUZ generation)" : options.GenerateLip ? " (with LIP generation)" : "";
        PushLog($"Starting batch: {rows.Count} row(s) from {sourceLabel}{pipelineNote}");
        PushEvent("batchStarted", new { total = rows.Count });

        // Run the batch on a background task, decoupled from this RPC call --
        // the frontend gets an immediate "started" response and then receives
        // batchProgress/batchDone events asynchronously as the batch runs.
        var cts = _batchCts;
        var pauseGate = _pauseGate;
        _ = Task.Run(async () =>
        {
            var processor = new BatchProcessor(credentials, req.DefaultModels);
            var progress = new Progress<BatchProgress>(p => PushEvent("batchProgress", new
            {
                completed = p.Completed,
                total = p.Total,
                skipped = p.Skipped,
                failed = p.Failed,
                currentId = p.CurrentId,
                message = p.Message
            }));

            try
            {
                var (success, failed, skipped, failures) = await processor.RunAsync(rows, voiceMap, options, progress, pauseGate, cts.Token);
                bool stopped = cts.IsCancellationRequested;
                PushLog(stopped
                    ? $"Batch stopped by user. Made: {success}, Failed: {failed}, Skipped: {skipped}. Run it again to continue; finished lines are skipped."
                    : $"Batch finished. Success: {success}, Failed: {failed}, Skipped: {skipped}.");

                string failLog = Path.Combine(req.VoiceFolder, "_failures.csv");
                if (failures.Count > 0)
                {
                    try
                    {
                        await CsvService.WriteFailuresAsync(failLog, failures);
                        PushLog($"Failure details written to: {failLog}");
                    }
                    catch (Exception ex) { PushLog($"Could not write failure log: {ex.Message}"); }
                }
                else if (!stopped && File.Exists(failLog))
                {
                    // A clean run: the failure list from an earlier run no longer applies.
                    try { File.Delete(failLog); } catch { /* best-effort */ }
                }

                PushEvent("batchDone", new { success, failed, skipped, failureCount = failures.Count, cancelled = stopped });
            }
            catch (OperationCanceledException)
            {
                PushLog("Batch stopped by user.");
                PushEvent("batchDone", new { success = 0, failed = 0, skipped = 0, failureCount = 0, cancelled = true });
            }
            catch (Exception ex)
            {
                PushLog($"Batch error: {ex.Message}");
                PushEvent("batchDone", new { success = 0, failed = 0, skipped = 0, failureCount = 0, error = ex.Message });
            }
            finally
            {
                _batchRunning = false;
            }
        });

        return Task.FromResult<object?>(new { ok = true, started = true });
    }

    private object TogglePause()
    {
        if (!_batchRunning) return new { ok = false };
        if (_pauseGate.IsSet) { _pauseGate.Reset(); PushLog("Paused."); return new { ok = true, paused = true }; }
        _pauseGate.Set(); PushLog("Resumed."); return new { ok = true, paused = false };
    }

    private object StopBatch()
    {
        _batchCts?.Cancel();
        return new { ok = true };
    }

    private object MarkSplashSeen()
    {
        var s = PersistenceService.LoadSettings();
        s.HasSeenSplash = true;
        PersistenceService.SaveSettings(s);
        return new { ok = true };
    }

    /// <summary>
    /// Opens a URL in the OS's actual default browser, not inside the app's own
    /// WebView -- a plain &lt;a href&gt; click would otherwise navigate the app's UI
    /// itself away to that page, which would break the app.
    /// </summary>
    private object OpenExternalUrl(JsonElement args)
    {
        string url = args.GetProperty("url").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(url)) return new { ok = false };

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    // ------------------------------------------------------------------
    // Local voices (OmniVoice, Qwen3-TTS)
    // ------------------------------------------------------------------

    private const string SampleLine = "The road north is long, and the nights grow cold.";

    private static string SampleText(string? text)
    {
        var sample = string.IsNullOrWhiteSpace(text) ? SampleLine : text.Trim();
        if (sample.Length > 300) throw new InvalidOperationException("Keep the sample under 300 characters.");
        return sample;
    }

    private static T Read<T>(JsonElement args, string whatIsMissing) =>
        args.ValueKind == JsonValueKind.Undefined ? throw new InvalidOperationException(whatIsMissing) : args.Deserialize<T>(JsonOpts) ?? throw new InvalidOperationException(whatIsMissing);

    private static OmniVoiceService Omni => OmniVoiceService.Shared!;
    private static Qwen3TtsService Qwen => Qwen3TtsService.Shared!;

    private static object OmniVoiceState() => new
    {
        settings = Omni.Settings, voices = Omni.Voices, configured = Omni.Configured, running = Omni.Running,
        tags = OmniVoiceService.SupportedTags,
        traits = OmniVoiceService.InstructGroups.Select(g => new { group = g.Group, items = g.Items }).ToArray()
    };

    private static object Qwen3TtsState() => new
    {
        settings = Qwen.Settings, voices = Qwen.Voices, configured = Qwen.Configured, running = Qwen.Running,
        speakers = Qwen3TtsService.Speakers, languages = Qwen3TtsService.Languages
    };

    private object OmniVoiceSave(JsonElement args) { Omni.SaveSettings(Read<OmniVoiceSettingsRequest>(args, "Give the OmniVoice settings.").Settings); return OmniVoiceState(); }
    private async Task<object?> OmniVoiceCheckAsync() => new { report = await Omni.CheckAsync() };
    private object OmniVoiceStop() { Omni.Stop(); return OmniVoiceState(); }
    private object OmniVoiceSaveVoice(JsonElement args) { Omni.SaveVoice(Read<OmniVoiceVoiceRequest>(args, "Give the voice.").Voice); return OmniVoiceState(); }
    private object OmniVoiceDeleteVoice(JsonElement args) { Omni.DeleteVoice(Read<OmniVoiceIdRequest>(args, "Choose a voice.").Id); return OmniVoiceState(); }
    private object OmniVoiceAddReference(JsonElement args) => new { fileName = Omni.AddReference(Read<OmniVoicePathRequest>(args, "Choose a recording.").Path) };

    private async Task<object?> OmniVoiceSampleAsync(JsonElement args)
    {
        var req = Read<OmniVoiceSampleRequest>(args, "Choose a voice.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var wav = await Omni.SpeakAsync(req.Voice, SampleText(req.Text), OmniVoiceProvider.StepsFor(OmniVoiceProvider.Best), timeout.Token);
        return new { audioData = Convert.ToBase64String(wav) };
    }

    private object Qwen3TtsSave(JsonElement args) { Qwen.SaveSettings(Read<Qwen3TtsSettingsRequest>(args, "Give the Qwen3-TTS settings.").Settings); return Qwen3TtsState(); }
    private async Task<object?> Qwen3TtsCheckAsync() => new { report = await Qwen.CheckAsync() };
    private object Qwen3TtsStop() { Qwen.Stop(); return Qwen3TtsState(); }
    private object Qwen3TtsSaveVoice(JsonElement args) { Qwen.SaveVoice(Read<Qwen3TtsVoiceRequest>(args, "Give the voice.").Voice); return Qwen3TtsState(); }
    private object Qwen3TtsDeleteVoice(JsonElement args) { Qwen.DeleteVoice(Read<Qwen3TtsIdRequest>(args, "Choose a voice.").Id); return Qwen3TtsState(); }
    private object Qwen3TtsAddReference(JsonElement args) => new { fileName = Qwen.AddReference(Read<Qwen3TtsPathRequest>(args, "Choose a recording.").Path) };

    private async Task<object?> Qwen3TtsSampleAsync(JsonElement args)
    {
        var req = Read<Qwen3TtsSampleRequest>(args, "Choose a voice.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var wav = await Qwen.SpeakAsync(req.Voice, SampleText(req.Text), req.Emotion, timeout.Token);
        return new { audioData = Convert.ToBase64String(wav) };
    }

    // ------------------------------------------------------------------
    // Wire helpers
    // ------------------------------------------------------------------

    private void PushLog(string message) => PushEvent("log", new { message });

    private void PushEvent(string eventName, object data)
    {
        if (Window is null) return;
        Window.SendWebMessage(JsonSerializer.Serialize(new { @event = eventName, data }, JsonOpts));
    }

    private static void SendResponse(PhotinoWindow window, string id, bool ok, object? result, string? error)
    {
        window.SendWebMessage(JsonSerializer.Serialize(new { id, ok, result, error }, JsonOpts));
    }
}
