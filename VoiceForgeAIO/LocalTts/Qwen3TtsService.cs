using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceForgeAIO.LocalTts;

/// <summary>Where the author's local Qwen3-TTS install is. Saved with the app.</summary>
public sealed class Qwen3TtsSettings
{
    /// <summary>The Python of the Qwen3-TTS environment. With WSL: the path inside Linux.</summary>
    public string PythonPath { get; set; } = "";
    public bool UseWsl { get; set; }
    public string WslDistro { get; set; } = "";
    /// <summary>Qwen3-TTS has one model per way of making a voice; only the one in use is loaded.</summary>
    public string DesignModel { get; set; } = "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign";
    public string CloneModel { get; set; } = "Qwen/Qwen3-TTS-12Hz-1.7B-Base";
    public string PresetModel { get; set; } = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice";
    /// <summary>Minutes of silence after which the model is unloaded to give the graphics card back (other GPU tools may need it).</summary>
    public int IdleMinutes { get; set; } = 10;
}

/// <summary>One voice the author can map an NPC to: described in free words, cloned from a short recording, or one of the model's preset speakers.</summary>
public sealed class Qwen3TtsProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"design" (from a description), "clone" (from a recording) or "preset" (one of the built-in speakers).</summary>
    public string Kind { get; set; } = "design";
    /// <summary>For "design": the voice in plain words. For "preset": an optional standing style. Any language, any wording.</summary>
    public string Instruct { get; set; } = "";
    /// <summary>For "preset": the speaker's name.</summary>
    public string Speaker { get; set; } = "Ryan";
    public string Language { get; set; } = "English";
    /// <summary>A recording in VoiceForge AIO's Qwen3-TTS voices folder, 3 to 10 seconds of clean speech.</summary>
    public string ReferenceFile { get; set; } = "";
    /// <summary>What the recording says. With it the clone is more accurate; without it only the sound of the voice is copied.</summary>
    public string ReferenceText { get; set; } = "";
}

/// <summary>Speaks lines with a local Qwen3-TTS. The model is loaded once, in a background Python process that stays up while lines are being made,
/// and is unloaded after a quiet spell. Requests go in as JSON lines and come back as WAV audio.</summary>
public sealed class Qwen3TtsService : IDisposable
{
    public static readonly string[] Speakers = ["Ryan", "Aiden", "Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ono_Anna", "Sohee"];
    public static readonly string[] Languages = ["English", "Chinese", "Japanese", "Korean", "German", "French", "Russian", "Portuguese", "Spanish", "Italian", "Auto"];

    /// <summary>How an emotion changes the sampling of a cloned voice (clones take no instruction): hotter and looser for energetic feelings, steadier for quiet ones.</summary>
    public static (double Temperature, double TopP) SamplingFor(string? emotion)
    {
        var e = (emotion ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0 || e == "neutral") return (0.9, 0.9);
        string[] high = ["excited", "enthusiastic", "elated", "euphoric", "triumphant", "amazed", "surprised", "angry", "mad", "outraged", "frustrated", "agitated", "panicked", "alarmed", "scared", "anxious", "threatened"];
        string[] low = ["sad", "dejected", "melancholic", "tired", "bored", "calm", "peaceful", "serene", "content", "resigned", "distant", "contemplative", "wistful", "nostalgic", "guilty", "hurt", "disappointed"];
        return high.Contains(e) ? (1.05, 0.95) : low.Contains(e) ? (0.7, 0.85) : (0.9, 0.9);
    }

    /// <summary>The instruction a designed or preset voice is given for one line: the voice itself plus how this line should be said.</summary>
    public static string InstructionFor(Qwen3TtsProfile voice, string? emotion)
    {
        var parts = new List<string>();
        if (voice.Instruct.Trim().Length > 0) parts.Add(voice.Instruct.Trim().TrimEnd('.'));
        if (!string.IsNullOrWhiteSpace(emotion) && !emotion.Equals("neutral", StringComparison.OrdinalIgnoreCase)) { var word = emotion.Trim().ToLowerInvariant(); parts.Add("speaking in " + ("aeiou".Contains(word[0]) ? "an " : "a ") + word + " tone"); }
        return string.Join(". ", parts);
    }

    private sealed class Store { public Qwen3TtsSettings Settings { get; set; } = new(); public List<Qwen3TtsProfile> Voices { get; set; } = []; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path, _voicesDirectory;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private Store _store = new();
    private Process? _process;
    private TaskCompletionSource<string>? _ready;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Queue<string> _stderr = new();
    private DateTime _lastUsed = DateTime.UtcNow;
    private System.Threading.Timer? _idle;
    private StreamWriter? _input;

    /// <summary>The one service the TTS provider list uses.</summary>
    public static Qwen3TtsService? Shared { get; set; }
    public string BridgeScript { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "qwen3tts_bridge.py");
    public Qwen3TtsSettings Settings => _store.Settings;
    public IReadOnlyList<Qwen3TtsProfile> Voices => _store.Voices;
    public bool Configured => Settings.PythonPath.Length > 0;
    public bool Running { get { lock (_gate) return _process is { HasExited: false }; } }

    public Qwen3TtsService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceForgeAIO", "qwen3tts.json");
        _voicesDirectory = Path.Combine(Path.GetDirectoryName(_path)!, "qwen3tts-voices");
        if (!File.Exists(_path)) return;
        try { _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path), JsonOptions) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { _store = new(); }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_store, JsonOptions));
        File.Move(_path + ".tmp", _path, true);
    }

    public Qwen3TtsSettings SaveSettings(Qwen3TtsSettings settings)
    {
        settings.PythonPath = (settings.PythonPath ?? "").Trim().Trim('"');
        settings.WslDistro = (settings.WslDistro ?? "").Trim();
        var defaults = new Qwen3TtsSettings();
        settings.DesignModel = string.IsNullOrWhiteSpace(settings.DesignModel) ? defaults.DesignModel : settings.DesignModel.Trim();
        settings.CloneModel = string.IsNullOrWhiteSpace(settings.CloneModel) ? defaults.CloneModel : settings.CloneModel.Trim();
        settings.PresetModel = string.IsNullOrWhiteSpace(settings.PresetModel) ? defaults.PresetModel : settings.PresetModel.Trim();
        settings.IdleMinutes = Math.Clamp(settings.IdleMinutes, 1, 240);
        var changed = settings.PythonPath != _store.Settings.PythonPath || settings.UseWsl != _store.Settings.UseWsl || settings.WslDistro != _store.Settings.WslDistro || settings.DesignModel != _store.Settings.DesignModel || settings.CloneModel != _store.Settings.CloneModel || settings.PresetModel != _store.Settings.PresetModel;
        _store.Settings = settings;
        Persist();
        if (changed) Stop();
        return settings;
    }

    // ---- voices ----

    public Qwen3TtsProfile SaveVoice(Qwen3TtsProfile voice)
    {
        var existing = _store.Voices.FirstOrDefault(v => v.Id == voice.Id);
        voice.Name = (voice.Name ?? "").Trim();
        if (voice.Name.Length == 0) throw new InvalidOperationException("Give the voice a name.");
        voice.Kind = voice.Kind is "clone" or "preset" ? voice.Kind : "design";
        voice.Instruct = Regex.Replace(voice.Instruct ?? "", @"\s+", " ").Trim();
        voice.Language = Languages.Contains(voice.Language ?? "", StringComparer.OrdinalIgnoreCase) ? Languages.First(l => l.Equals(voice.Language, StringComparison.OrdinalIgnoreCase)) : "English";
        voice.Speaker = Speakers.Contains(voice.Speaker ?? "") ? voice.Speaker! : "Ryan";
        voice.ReferenceText = (voice.ReferenceText ?? "").Trim();
        if (voice.Kind == "design" && voice.Instruct.Length == 0) throw new InvalidOperationException("Describe the voice in your own words (for example: a gruff old man with a slow, tired voice), or choose another kind.");
        if (existing is not null) { voice.ReferenceFile = voice.ReferenceFile.Length > 0 ? voice.ReferenceFile : existing.ReferenceFile; _store.Voices.Remove(existing); }
        if (voice.Kind == "clone" && voice.ReferenceFile.Length == 0) throw new InvalidOperationException("Choose a recording of the voice to clone (3 to 10 seconds of clean speech).");
        _store.Voices.Add(voice);
        _store.Voices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Persist();
        return voice;
    }

    public void DeleteVoice(string id)
    {
        var voice = _store.Voices.FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("That voice no longer exists.");
        _store.Voices.Remove(voice);
        Persist();
        if (voice.ReferenceFile.Length > 0 && !_store.Voices.Any(v => v.ReferenceFile == voice.ReferenceFile)) try { File.Delete(Path.Combine(_voicesDirectory, voice.ReferenceFile)); } catch (IOException) { }
    }

    /// <summary>Copies a recording into VoiceForge AIO's own folder, so the voice keeps working if the original moves.</summary>
    public string AddReference(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new InvalidOperationException("That recording does not exist.");
        Directory.CreateDirectory(_voicesDirectory);
        var name = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, Path.Combine(_voicesDirectory, name));
        return name;
    }

    public string ReferencePath(Qwen3TtsProfile voice) => Path.Combine(_voicesDirectory, voice.ReferenceFile);

    // ---- the background process ----

    private ProcessStartInfo StartInfo(string command, string jobFile)
    {
        var script = Path.GetFullPath(BridgeScript);
        if (!File.Exists(script)) throw new FileNotFoundException("VoiceForge AIO's Qwen3-TTS bridge script is missing: " + script);
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (Settings.UseWsl)
        {
            info.FileName = "wsl.exe";
            if (Settings.WslDistro.Length > 0) { info.ArgumentList.Add("-d"); info.ArgumentList.Add(Settings.WslDistro); }
            info.ArgumentList.Add("--"); info.ArgumentList.Add(Settings.PythonPath);
            info.ArgumentList.Add("-u"); info.ArgumentList.Add("-X"); info.ArgumentList.Add("faulthandler");
            info.ArgumentList.Add(BridgeText.ToWslPath(script)); info.ArgumentList.Add(command); info.ArgumentList.Add(BridgeText.ToWslPath(jobFile));
        }
        else
        {
            info.FileName = Settings.PythonPath;
            info.ArgumentList.Add("-u"); info.ArgumentList.Add("-X"); info.ArgumentList.Add("faulthandler");
            info.ArgumentList.Add(script); info.ArgumentList.Add(command); info.ArgumentList.Add(jobFile);
            info.Environment["PYTHONIOENCODING"] = "utf-8";
        }
        return info;
    }

    private string WriteJob()
    {
        var file = Path.Combine(Path.GetTempPath(), "voiceforgeaio-qwen3tts-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { designModel = Settings.DesignModel, cloneModel = Settings.CloneModel, presetModel = Settings.PresetModel, dtype = "bfloat16", device = "cuda:0" }, JsonOptions));
        return file;
    }

    private void RequireConfigured() { if (!Configured) throw new InvalidOperationException("Set up Qwen3-TTS first: give VoiceForge the Python of the Qwen3-TTS environment."); }

    /// <summary>Loads PyTorch and Qwen3-TTS once and reports the graphics card, so a bad install is found before any lines are spoken.</summary>
    public async Task<string> CheckAsync(CancellationToken cancel = default)
    {
        RequireConfigured();
        var job = WriteJob();
        try
        {
            using var process = new Process { StartInfo = StartInfo("check", job) };
            try { process.Start(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { throw new InvalidOperationException("Python could not be started from '" + Settings.PythonPath + "': " + ex.Message); }
            try { process.StandardInput.Close(); } catch (IOException) { }
            string result = "", error = ""; var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(BridgeText.Clean(e.Data)); };
            process.BeginErrorReadLine();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancel)) is not null)
            {
                var (kind, text) = BridgeText.ParseLine(BridgeText.Clean(line).TrimEnd());
                if (kind == "RESULT") result = text; else if (kind == "ERROR") error = text;
            }
            await process.WaitForExitAsync(cancel);
            process.WaitForExit();
            if (process.ExitCode != 0 || error.Length > 0)
            {
                string tail; lock (stderr) tail = BridgeText.Meaningful(stderr.ToString(), 8);
                throw new InvalidOperationException(ErrorMessage(error) is { Length: > 0 } m ? m : "Qwen3-TTS stopped with exit code " + process.ExitCode + ". " + tail);
            }
            return result;
        }
        finally { try { File.Delete(job); } catch (IOException) { } }
    }

    private static string ErrorMessage(string json)
    {
        if (json.Length == 0) return "";
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : json; } catch (JsonException) { return json; }
    }

    private async Task EnsureStartedAsync(CancellationToken cancel)
    {
        TaskCompletionSource<string> ready;
        lock (_gate)
        {
            if (_process is { HasExited: false } && _ready is not null) { ready = _ready; goto wait; }
            RequireConfigured();
            var job = WriteJob();
            _stderr.Clear();
            var process = new Process { StartInfo = StartInfo("serve", job), EnableRaisingEvents = true };
            try { process.Start(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { try { File.Delete(job); } catch (IOException) { } throw new InvalidOperationException("Python could not be started from '" + Settings.PythonPath + "': " + ex.Message); }
            _process = process; _ready = ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _input = process.StandardInput; _input.AutoFlush = true;
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_gate) { _stderr.Enqueue(BridgeText.Clean(e.Data)); while (_stderr.Count > 60) _stderr.Dequeue(); } };
            process.BeginErrorReadLine();
            _ = Task.Run(() => ReadAnswersAsync(process, ready, job));
            _idle ??= new System.Threading.Timer(_ => { if (_pending.Count == 0 && DateTime.UtcNow - _lastUsed > TimeSpan.FromMinutes(Settings.IdleMinutes)) Stop(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        wait:
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(10)); // the first start downloads the model
        await using (timeout.Token.Register(() => ready.TrySetCanceled()))
            await ready.Task;
    }

    private async Task ReadAnswersAsync(Process process, TaskCompletionSource<string> ready, string job)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                var clean = BridgeText.Clean(line).TrimEnd();
                if (clean.StartsWith("READY", StringComparison.Ordinal)) { ready.TrySetResult(clean[5..].Trim()); continue; }
                var (kind, text) = BridgeText.ParseLine(clean);
                if (kind is not ("RESULT" or "ERROR")) continue;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var id = document.RootElement.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    TaskCompletionSource<JsonElement>? waiting; lock (_gate) { _pending.Remove(id, out waiting); }
                    if (kind == "ERROR")
                    {
                        var failure = new InvalidOperationException(ErrorMessage(text));
                        if (waiting is not null) waiting.TrySetException(failure);
                        else if (!ready.Task.IsCompleted) ready.TrySetException(failure);
                    }
                    else waiting?.TrySetResult(document.RootElement.Clone());
                }
                catch (JsonException) { }
            }
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        finally
        {
            string tail; lock (_gate) tail = BridgeText.Meaningful(string.Join("\n", _stderr), 8);
            var ended = new InvalidOperationException("Qwen3-TTS stopped" + (tail.Length > 0 ? ": " + tail : " without a message (it may have run out of graphics memory or been stopped from outside)."));
            ready.TrySetException(ended); FailPending(ended);
            lock (_gate) { if (ReferenceEquals(_process, process)) { _process = null; _ready = null; _input = null; } }
            try { File.Delete(job); } catch (IOException) { }
        }
    }

    private void FailPending(Exception error) { lock (_gate) { foreach (var p in _pending.Values) p.TrySetException(error); _pending.Clear(); } }

    /// <summary>Unloads the model and ends the background process (the graphics card is free again).</summary>
    public void Stop()
    {
        Process? process; lock (_gate) { process = _process; _process = null; _ready = null; _input = null; }
        try { process?.StandardInput.Close(); process?.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Speaks <paramref name="text"/> with a saved voice and returns a 16-bit mono WAV. <paramref name="emotion"/> is the delivery the AI director chose for this line, if any.</summary>
    public async Task<byte[]> SpeakAsync(Qwen3TtsProfile voice, string text, string? emotion = null, CancellationToken cancel = default)
    {
        var prepared = Regex.Replace(Regex.Replace(text ?? "", @"\[[^\]\r\n]{1,40}\]", " "), @"\s{2,}", " ").Trim(); // bracketed cues are not words
        if (prepared.Length == 0) throw new InvalidOperationException("There is nothing to say once the delivery cues are removed.");
        await _one.WaitAsync(cancel);
        try
        {
            await EnsureStartedAsync(cancel);
            var id = Guid.NewGuid().ToString("N");
            var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending[id] = waiting;
            var request = new Dictionary<string, object?> { ["id"] = id, ["text"] = prepared, ["mode"] = voice.Kind, ["language"] = voice.Language };
            if (voice.Kind == "clone")
            {
                var path = ReferencePath(voice);
                request["refAudio"] = Settings.UseWsl ? BridgeText.ToWslPath(Path.GetFullPath(path)) : Path.GetFullPath(path);
                request["refText"] = voice.ReferenceText;
                var (temperature, topP) = SamplingFor(emotion);
                request["temperature"] = temperature; request["topP"] = topP;
            }
            else
            {
                request["instruct"] = InstructionFor(voice, emotion);
                if (voice.Kind == "preset") request["speaker"] = voice.Speaker;
            }
            StreamWriter input; lock (_gate) input = _input ?? throw new InvalidOperationException("Qwen3-TTS is not running.");
            try { await input.WriteLineAsync(JsonSerializer.Serialize(request)); }
            catch (IOException) { throw new InvalidOperationException("Qwen3-TTS stopped before it could be asked. Try again."); }
            await using var _ = cancel.Register(() => waiting.TrySetCanceled());
            var answer = await waiting.Task;
            _lastUsed = DateTime.UtcNow;
            return Convert.FromBase64String(answer.GetProperty("wav").GetString()!);
        }
        finally { _lastUsed = DateTime.UtcNow; _one.Release(); }
    }

    public void Dispose() { Stop(); _idle?.Dispose(); }
}

public sealed record Qwen3TtsSettingsRequest(Qwen3TtsSettings Settings);
public sealed record Qwen3TtsVoiceRequest(Qwen3TtsProfile Voice);
public sealed record Qwen3TtsSampleRequest(Qwen3TtsProfile Voice, string? Text, string? Emotion);
public sealed record Qwen3TtsIdRequest(string Id);
public sealed record Qwen3TtsPathRequest(string Path);
