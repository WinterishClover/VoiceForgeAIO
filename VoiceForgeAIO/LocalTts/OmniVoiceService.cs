using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceForgeAIO.LocalTts;

/// <summary>Where the author's local OmniVoice install is. Saved with the app.</summary>
public sealed class OmniVoiceSettings
{
    /// <summary>The Python of the OmniVoice environment. With WSL: the path inside Linux.</summary>
    public string PythonPath { get; set; } = "";
    public bool UseWsl { get; set; }
    public string WslDistro { get; set; } = "";
    public string Model { get; set; } = "k2-fsa/OmniVoice";
    /// <summary>Minutes of silence after which the model is unloaded to give the graphics card back (other GPU tools may need it).</summary>
    public int IdleMinutes { get; set; } = 10;
}

/// <summary>One voice the author can map an NPC to: described in words ("female, low pitch, british accent") or cloned from a short recording.</summary>
public sealed class OmniVoiceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"design" (from a description), "clone" (from a recording) or "auto" (the model picks).</summary>
    public string Kind { get; set; } = "design";
    public string Instruct { get; set; } = "";
    /// <summary>A recording in VoiceForge AIO's OmniVoice voices folder, 3 to 10 seconds of clean speech.</summary>
    public string ReferenceFile { get; set; } = "";
    /// <summary>What the recording says; leave empty and OmniVoice transcribes it itself.</summary>
    public string ReferenceText { get; set; } = "";
    public double Speed { get; set; } = 1.0;
}

/// <summary>Speaks lines with a local OmniVoice. The model is loaded once, in a background Python process that stays up while lines are being made,
/// and is unloaded after a quiet spell. Requests go in as JSON lines and come back as WAV audio.</summary>
public sealed class OmniVoiceService : IDisposable
{
    public static readonly string[] SupportedTags = ["laughter", "sigh", "confirmation-en", "question-en", "question-ah", "question-oh", "question-ei", "question-yi", "surprise-ah", "surprise-oh", "surprise-wa", "surprise-yo", "dissatisfaction-hnn"];

    /// <summary>The only words OmniVoice accepts in a voice description, grouped the way the editor offers them (one choice per group).</summary>
    public static readonly (string Group, string[] Items)[] InstructGroups =
    [
        ("Gender", ["female", "male"]),
        ("Age", ["child", "teenager", "young adult", "middle-aged", "elderly"]),
        ("Pitch", ["very low pitch", "low pitch", "moderate pitch", "high pitch", "very high pitch"]),
        ("Accent", ["american accent", "australian accent", "british accent", "canadian accent", "chinese accent", "indian accent", "japanese accent", "korean accent", "portuguese accent", "russian accent"]),
        ("Style", ["whisper"]),
    ];

    /// <summary>Tidies a description ("Male,  Low pitch.") and refuses anything OmniVoice would reject, with the allowed words in the message.</summary>
    public static string NormalizeInstruct(string? text)
    {
        var allowed = InstructGroups.SelectMany(g => g.Items).ToHashSet();
        var items = (text ?? "").Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(i => Regex.Replace(i, @"\s+", " ").Trim().TrimEnd('.').Trim().ToLowerInvariant()).Where(i => i.Length > 0).Distinct().ToList();
        var bad = items.Where(i => !allowed.Contains(i)).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException("OmniVoice only understands these words in a voice description, not free text (" + string.Join("; ", bad) + "). Allowed: " + string.Join(", ", allowed) + ".");
        return string.Join(", ", items);
    }

    private sealed class Store { public OmniVoiceSettings Settings { get; set; } = new(); public List<OmniVoiceProfile> Voices { get; set; } = []; }

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
    public static OmniVoiceService? Shared { get; set; }
    public string BridgeScript { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "omnivoice_bridge.py");
    public OmniVoiceSettings Settings => _store.Settings;
    public IReadOnlyList<OmniVoiceProfile> Voices => _store.Voices;
    public bool Configured => Settings.PythonPath.Length > 0;
    public bool Running { get { lock (_gate) return _process is { HasExited: false }; } }

    public OmniVoiceService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceForgeAIO", "omnivoice.json");
        _voicesDirectory = Path.Combine(Path.GetDirectoryName(_path)!, "omnivoice-voices");
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

    public OmniVoiceSettings SaveSettings(OmniVoiceSettings settings)
    {
        settings.PythonPath = (settings.PythonPath ?? "").Trim().Trim('"');
        settings.WslDistro = (settings.WslDistro ?? "").Trim();
        settings.Model = string.IsNullOrWhiteSpace(settings.Model) ? "k2-fsa/OmniVoice" : settings.Model.Trim();
        settings.IdleMinutes = Math.Clamp(settings.IdleMinutes, 1, 240);
        var changed = settings.PythonPath != _store.Settings.PythonPath || settings.UseWsl != _store.Settings.UseWsl || settings.WslDistro != _store.Settings.WslDistro || settings.Model != _store.Settings.Model;
        _store.Settings = settings;
        Persist();
        if (changed) Stop();
        return settings;
    }

    // ---- voices ----

    public OmniVoiceProfile SaveVoice(OmniVoiceProfile voice)
    {
        var existing = _store.Voices.FirstOrDefault(v => v.Id == voice.Id);
        voice.Name = (voice.Name ?? "").Trim();
        if (voice.Name.Length == 0) throw new InvalidOperationException("Give the voice a name.");
        voice.Kind = voice.Kind is "clone" or "auto" ? voice.Kind : "design";
        voice.Instruct = voice.Kind == "design" ? NormalizeInstruct(voice.Instruct) : "";
        voice.Speed = Math.Clamp(voice.Speed <= 0 ? 1.0 : voice.Speed, 0.6, 1.6);
        if (voice.Kind == "design" && voice.Instruct.Length == 0) throw new InvalidOperationException("Choose at least one trait for the voice (for example: female, low pitch, british accent), or choose another kind.");
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

    public string ReferencePath(OmniVoiceProfile voice) => Path.Combine(_voicesDirectory, voice.ReferenceFile);

    // ---- text ----

    private static readonly Dictionary<string, string> TagAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["laughs"] = "laughter", ["laughs softly"] = "laughter", ["laughing"] = "laughter", ["laugh"] = "laughter", ["chuckles"] = "laughter", ["chuckle"] = "laughter", ["giggles"] = "laughter", ["amused"] = "laughter",
        ["sighs"] = "sigh", ["sigh"] = "sigh", ["exhales"] = "sigh", ["wearily"] = "sigh",
        ["gasps"] = "surprise-ah", ["gasp"] = "surprise-ah", ["scoffs"] = "dissatisfaction-hnn", ["sniffs"] = "dissatisfaction-hnn", ["tsk"] = "dissatisfaction-hnn",
    };
    private static readonly Regex Phonemes = new(@"^[A-Z]{1,3}[0-9]?( [A-Z]{1,3}[0-9]?)*$", RegexOptions.Compiled);

    /// <summary>OmniVoice reads only its own non-verbal tags (and uppercase phonemes such as [B EY1 S]) in square brackets. Other tags, like the delivery cues
    /// other voice services accept, are turned into the nearest tag it knows (laughs → [laughter]) or dropped, so they are never read aloud.</summary>
    public static string PrepareText(string text) => Regex.Replace(Regex.Replace(text ?? "", @"\[([^\]\r\n]{1,40})\]", m =>
    {
        var inner = m.Groups[1].Value.Trim();
        if (SupportedTags.Contains(inner, StringComparer.OrdinalIgnoreCase)) return "[" + inner.ToLowerInvariant() + "]";
        if (Phonemes.IsMatch(inner)) return m.Value;
        return TagAliases.TryGetValue(inner, out var alias) ? "[" + alias + "]" : "";
    }), @"[ \t]{2,}", " ").Trim();

    // ---- the background process ----

    private ProcessStartInfo StartInfo(string command, string jobFile)
    {
        var script = Path.GetFullPath(BridgeScript);
        if (!File.Exists(script)) throw new FileNotFoundException("VoiceForge AIO's OmniVoice bridge script is missing: " + script);
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
        var file = Path.Combine(Path.GetTempPath(), "voiceforgeaio-omnivoice-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { model = Settings.Model, dtype = "float16", device = "cuda:0" }, JsonOptions));
        return file;
    }

    private void RequireConfigured() { if (!Configured) throw new InvalidOperationException("Set up OmniVoice first: give VoiceForge the Python of the OmniVoice environment."); }

    /// <summary>Loads PyTorch and OmniVoice once and reports the graphics card, so a bad install is found before any lines are spoken.</summary>
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
                throw new InvalidOperationException(ErrorMessage(error) is { Length: > 0 } m ? m : "OmniVoice stopped with exit code " + process.ExitCode + ". " + tail);
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
            var ended = new InvalidOperationException("OmniVoice stopped" + (tail.Length > 0 ? ": " + tail : " without a message (it may have run out of graphics memory or been stopped from outside)."));
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

    /// <summary>Speaks <paramref name="text"/> with a saved voice and returns a 16-bit mono WAV (24 kHz).</summary>
    public async Task<byte[]> SpeakAsync(OmniVoiceProfile voice, string text, int steps, CancellationToken cancel = default)
    {
        var prepared = PrepareText(text);
        var instruct = voice.Kind == "design" ? NormalizeInstruct(voice.Instruct) : "";
        if (prepared.Length == 0) throw new InvalidOperationException("There is nothing to say once the delivery tags are removed.");
        await _one.WaitAsync(cancel);
        try
        {
            await EnsureStartedAsync(cancel);
            var id = Guid.NewGuid().ToString("N");
            var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending[id] = waiting;
            var request = new Dictionary<string, object?>
            {
                ["id"] = id, ["text"] = prepared, ["mode"] = voice.Kind, ["instruct"] = instruct, ["steps"] = Math.Clamp(steps, 4, 64), ["speed"] = voice.Speed,
            };
            if (voice.Kind == "clone")
            {
                var path = ReferencePath(voice);
                request["refAudio"] = Settings.UseWsl ? BridgeText.ToWslPath(Path.GetFullPath(path)) : Path.GetFullPath(path);
                request["refText"] = voice.ReferenceText;
            }
            StreamWriter input; lock (_gate) input = _input ?? throw new InvalidOperationException("OmniVoice is not running.");
            try { await input.WriteLineAsync(JsonSerializer.Serialize(request)); }
            catch (IOException) { throw new InvalidOperationException("OmniVoice stopped before it could be asked. Try again."); }
            await using var _ = cancel.Register(() => waiting.TrySetCanceled());
            var answer = await waiting.Task;
            _lastUsed = DateTime.UtcNow;
            return Convert.FromBase64String(answer.GetProperty("wav").GetString()!);
        }
        finally { _lastUsed = DateTime.UtcNow; _one.Release(); }
    }

    public void Dispose() { Stop(); _idle?.Dispose(); }
}

public sealed record OmniVoiceSettingsRequest(OmniVoiceSettings Settings);
public sealed record OmniVoiceVoiceRequest(OmniVoiceProfile Voice);
public sealed record OmniVoiceSampleRequest(OmniVoiceProfile Voice, string? Text);
public sealed record OmniVoiceIdRequest(string Id);
public sealed record OmniVoicePathRequest(string Path);
