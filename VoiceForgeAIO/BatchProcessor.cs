using VoiceForgeAIO.Providers;

namespace VoiceForgeAIO;

internal sealed record BatchOptions(
    string VoiceFolderRoot,   // e.g. .../Sound/Voice/BHKS-TNG-The-Accorded.esp
    int Concurrency,
    bool Overwrite,
    bool GenerateLip,
    string FaceFxWrapperPath,
    string FonixDataPath,
    bool GenerateFuz,         // requires GenerateLip -- enforced by the UI, not here
    string XwmaEncodePath,
    string LipFuzerPath);

internal sealed record BatchProgress(
    int Completed,
    int Total,
    int Skipped,
    int Failed,
    string CurrentId,
    string Message);

/// <summary>
/// Runs a CSV batch: for each row, resolves VoiceType -> (Provider, Voice) via the
/// mapping table, ensures "<VoiceFolderRoot>/<VoiceType>/" exists, writes
/// "<VoiceFolderRoot>/<VoiceType>/<EditorID>.wav", and -- if GenerateLip is set --
/// shells out to FaceFXWrapper for a matching .lip file. If GenerateFuz is also set,
/// each WAV is additionally encoded to .xwm (via xWMAEncode), and once every row is
/// done, LipFuzer is run once per touched VoiceType folder to combine the .xwm+.lip
/// pairs into .fuz files (LipFuzer works on a whole directory at a time, not a
/// single file pair, so it's a one-shot pass per folder rather than per row).
/// Supports pause and cooperative cancellation; already-generated files are skipped
/// unless Overwrite is set, so a batch can always be safely re-run.
/// </summary>
internal sealed class BatchProcessor
{
    private readonly Dictionary<string, ProviderCredentials> _credentialsByProvider;

    public BatchProcessor(Dictionary<string, ProviderCredentials> credentialsByProvider) => _credentialsByProvider = credentialsByProvider;

    public async Task<(int Success, int Failed, int Skipped, List<(CsvRow Row, string Error)> Failures)> RunAsync(
        List<CsvRow> rows,
        Dictionary<string, VoiceMapEntry> voiceMapByType,
        BatchOptions options,
        IProgress<BatchProgress> progress,
        ManualResetEventSlim pauseGate,
        CancellationToken ct)
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<(CsvRow, string)>();
        int skipped = 0, completed = 0, failedCount = 0, succeeded = 0;
        int total = rows.Count;
        object progressLock = new();

        var pending = new List<(CsvRow Row, string WavPath, string LipPath, string XwmPath, string FuzPath, VoiceMapEntry Map, ITtsProvider Provider)>();
        var claimedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resolve mapping + skip-existing up front so "Total" reflects real work.
        foreach (var row in rows)
        {
            if (!voiceMapByType.TryGetValue(row.VoiceType, out var mapEntry) || string.IsNullOrWhiteSpace(mapEntry.VoiceId))
            {
                failures.Add((row, $"No voice mapped for VoiceType '{row.VoiceType}'. Add it in the Voice Mapping table."));
                failedCount++;
                continue;
            }

            if (!TtsProviderRegistry.TryGet(mapEntry.ProviderId, out var provider))
            {
                failures.Add((row, $"Unknown provider '{mapEntry.ProviderId}' for VoiceType '{row.VoiceType}'."));
                failedCount++;
                continue;
            }

            // Local providers (OmniVoice, Qwen3-TTS) run on this computer and have no key to check.
            if (provider.RequiresApiKey && (!_credentialsByProvider.TryGetValue(mapEntry.ProviderId, out var creds) || !creds.HasApiKey))
            {
                failures.Add((row, $"No API key configured for provider '{provider.DisplayName}' (VoiceType '{row.VoiceType}'). Add it in Settings."));
                failedCount++;
                continue;
            }

            string voiceDir = Path.Combine(options.VoiceFolderRoot, SanitizeFileName(row.VoiceType));
            string baseName = SanitizeFileName(row.Id);
            string wavPath = Path.Combine(voiceDir, baseName + ".wav");
            string lipPath = Path.Combine(voiceDir, baseName + ".lip");
            string xwmPath = Path.Combine(voiceDir, baseName + ".xwm");
            string fuzPath = Path.Combine(voiceDir, baseName + ".fuz");

            // Two lines with the same EditorID and VoiceType would write the same file; the second would
            // silently replace the first (or collide while both are written at once).
            if (!claimedFiles.Add(wavPath))
            {
                failures.Add((row, $"Duplicate EditorID '{row.Id}' for VoiceType '{row.VoiceType}': another line in this CSV writes the same file. Give each line its own EditorID."));
                failedCount++;
                continue;
            }

            bool wavExists = File.Exists(wavPath);
            bool lipDone = !options.GenerateLip || File.Exists(lipPath);
            bool fuzDone = !options.GenerateFuz || File.Exists(fuzPath);
            if (!options.Overwrite && wavExists && lipDone && fuzDone)
            {
                skipped++;
                continue;
            }

            pending.Add((row, wavPath, lipPath, xwmPath, fuzPath, mapEntry, provider));
        }

        // Lines that were skipped or failed up front count as done, so progress reaches total/total.
        completed = skipped + failedCount;
        progress.Report(new BatchProgress(completed, total, skipped, failedCount, "",
            $"Resolved {pending.Count} line(s) to synthesize, {skipped} already exist, {failedCount} unmapped/misconfigured."));

        using var throttle = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var touchedFolders = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        var tasks = pending.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                pauseGate.Wait(ct);
                ct.ThrowIfCancellationRequested();

                Directory.CreateDirectory(Path.GetDirectoryName(item.WavPath)!);
                var creds = _credentialsByProvider.TryGetValue(item.Map.ProviderId, out var found) ? found : new ProviderCredentials();

                string statusSuffix = "";
                if (!File.Exists(item.WavPath) || options.Overwrite)
                {
                    byte[] audio = await item.Provider.SynthesizeAsync(creds, item.Row.Text, item.Map.VoiceId, item.Provider.DefaultModel, ct);
                    await File.WriteAllBytesAsync(item.WavPath, WavHeaderFixup.NormalizeHeader(audio), ct);
                }

                bool lipReady = false;
                if (options.GenerateLip)
                {
                    if (!item.Provider.SupportsWavOutput)
                    {
                        statusSuffix = " (LIP/FUZ skipped: provider doesn't return WAV/PCM audio)";
                    }
                    else
                    {
                        if (!File.Exists(item.LipPath) || options.Overwrite)
                            await FaceFxService.GenerateLipAsync(
                                options.FaceFxWrapperPath, options.FonixDataPath, item.WavPath, item.LipPath, item.Row.Text, ct);
                        lipReady = true;
                    }
                }

                if (options.GenerateFuz && lipReady)
                {
                    if (!File.Exists(item.XwmPath) || options.Overwrite)
                        await XwmaEncodeService.EncodeAsync(options.XwmaEncodePath, item.WavPath, item.XwmPath, ct);
                    touchedFolders.TryAdd(Path.GetDirectoryName(item.FuzPath)!, 0);
                }

                lock (progressLock)
                {
                    completed++;
                    succeeded++;
                    progress.Report(new BatchProgress(completed, total, skipped, failedCount, item.Row.Id, "OK" + statusSuffix));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add((item.Row, ex.Message));
                lock (progressLock)
                {
                    completed++;
                    failedCount++;
                    progress.Report(new BatchProgress(completed, total, skipped, failedCount, item.Row.Id, $"FAILED: {ex.Message}"));
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            progress.Report(new BatchProgress(completed, total, skipped, failedCount, "", "Cancelled."));
        }

        // LipFuzer works on a whole directory per call, matching .xwm+.lip pairs by
        // basename -- so run it once per VoiceType folder touched by this batch,
        // after every row's WAV/LIP/XWM generation has finished.
        if (options.GenerateFuz && !ct.IsCancellationRequested)
        {
            foreach (string folder in touchedFolders.Keys)
            {
                try
                {
                    progress.Report(new BatchProgress(completed, total, skipped, failedCount, "", $"Fuzing {folder}..."));
                    await LipFuzerService.FuzeDirectoryAsync(options.LipFuzerPath, folder, ct);
                    progress.Report(new BatchProgress(completed, total, skipped, failedCount, "", $"Fuzed: {folder}"));
                }
                catch (Exception ex)
                {
                    progress.Report(new BatchProgress(completed, total, skipped, failedCount, "", $"FUZ FAILED for {folder}: {ex.Message}"));
                }
            }
        }

        // Counted as lines finish, so a stopped batch reports what it really made.
        return (succeeded, failedCount, skipped, failures.ToList());
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
