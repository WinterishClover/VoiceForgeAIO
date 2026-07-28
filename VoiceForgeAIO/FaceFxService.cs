using System.Diagnostics;

namespace VoiceForgeAIO;

/// <summary>
/// Wraps FaceFXWrapper.exe (https://github.com/Nukem9/FaceFXWrapper) to generate a
/// .lip file from a WAV + its spoken text. Neither FaceFXWrapper.exe nor
/// FonixData.cdf are bundled with VoiceForge AIO -- both are third-party/Bethesda
/// files the person must already have locally (see Settings). This mirrors how
/// every other community tool wrapping these files handles it, for the same
/// licensing reasons.
///
/// CLI contract (from the FaceFXWrapper README) has two modes:
///   7-arg: FaceFXWrapper [Type] [Lang] [FonixDataPath] [WavPath] [ResampledWavPath] [LipPath] [Text]
///          -- FaceFXWrapper resamples WavPath itself into ResampledWavPath.
///   6-arg: FaceFXWrapper [Type] [Lang] [FonixDataPath] [ResampledWavPath] [LipPath] [Text]
///          -- you supply an already-16kHz/16-bit/mono file directly.
/// This service always uses the 6-arg mode: different TTS providers export at
/// different sample rates (Cartesia at 44.1kHz, MiniMax at 32kHz, etc.), and
/// FaceFXWrapper's own built-in resampling doesn't reliably handle all of them --
/// so we resample with NAudio ourselves into a throwaway temp file and hand that
/// straight to FaceFXWrapper, skipping its internal resample step entirely. The
/// actual WAV shipped with the mod (and later encoded to XWM for FUZ) is untouched
/// and keeps its original quality.
///
/// "Type" is hardcoded to "Skyrim" here since that's this tool's whole purpose;
/// FaceFXWrapper also supports "Fallout4" if that's ever needed later.
/// </summary>
internal static class FaceFxService
{
    public static async Task GenerateLipAsync(
        string exePath, string fonixDataPath, string wavPath, string lipPath, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("FaceFXWrapper.exe path isn't set or doesn't exist (check Settings).");
        if (string.IsNullOrWhiteSpace(fonixDataPath) || !File.Exists(fonixDataPath))
            throw new InvalidOperationException("FonixData.cdf path isn't set or doesn't exist (check Settings).");

        // Guard against a real-world race: our own WAV write has returned, but
        // something else (most often Windows Defender's real-time scan grabbing a
        // brief lock on a freshly-written file) can still be holding it just long
        // enough for the next open attempt to fail with "could not open file" --
        // even though the file is completely fine a moment later.
        await WaitUntilReadableAsync(wavPath, ct);

        string resampledPath = Path.Combine(Path.GetTempPath(), $"vfaio_resample_{Guid.NewGuid():N}.wav");

        try
        {
            // Resample ourselves (see class remarks for why) rather than trust
            // FaceFXWrapper's own internal resampling.
            WavResampler.ResampleTo16kHzMono(wavPath, resampledPath);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("Skyrim");
            psi.ArgumentList.Add("USEnglish");
            psi.ArgumentList.Add(fonixDataPath);
            psi.ArgumentList.Add(resampledPath); // already 16kHz/16-bit/mono -- 6-arg mode
            psi.ArgumentList.Add(lipPath);
            psi.ArgumentList.Add(text);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start FaceFXWrapper.exe.");

            string stdoutTask = await process.StandardOutput.ReadToEndAsync(ct);
            string stderrTask = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || !File.Exists(lipPath))
            {
                string details = string.IsNullOrWhiteSpace(stderrTask) ? stdoutTask : stderrTask;
                throw new InvalidOperationException($"FaceFXWrapper failed (exit {process.ExitCode}): {Truncate(details, 300)}");
            }
        }
        finally
        {
            try { if (File.Exists(resampledPath)) File.Delete(resampledPath); }
            catch { /* best-effort cleanup of the temp resample file */ }
        }
    }

    /// <summary>
    /// Retries opening the file exclusively for a short window, to ride out a
    /// transient lock (e.g. antivirus real-time scanning a freshly-written file)
    /// before handing it to an external process. Throws if it's still locked after
    /// all retries, rather than retrying forever.
    /// </summary>
    private static async Task WaitUntilReadableAsync(string path, CancellationToken ct, int maxAttempts = 10, int delayMs = 150)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return; // opened cleanly -- not locked
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(delayMs, ct);
            }
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(no output)" : (s.Length <= max ? s : s[..max] + "...");
}
