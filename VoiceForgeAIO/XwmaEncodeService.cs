using System.Diagnostics;

namespace VoiceForgeAIO;

/// <summary>
/// Wraps xWMAEncode.exe (legacy DirectX SDK / Xbox ADK tool) to convert a WAV file
/// to XWM (xWMA-compressed), which is what Skyrim's .fuz container actually expects
/// for its audio stream -- not raw WAV/PCM. Not bundled with VoiceForge AIO, same
/// reasoning as FaceFXWrapper/FonixData: point Settings at your own local copy.
///
/// CLI contract: xWMAEncode.exe input.wav output.xwm  (simple positional args).
/// </summary>
internal static class XwmaEncodeService
{
    public static async Task EncodeAsync(string exePath, string wavPath, string xwmPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("xWMAEncode.exe path isn't set or doesn't exist (check Settings).");
        if (!File.Exists(wavPath))
            throw new InvalidOperationException($"Source WAV not found: {wavPath}");

        // Same file-lock race as FaceFXWrapper: our write returned, but something
        // (commonly antivirus real-time scanning) can still briefly hold the file.
        await WaitUntilReadableAsync(wavPath, ct);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add(xwmPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start xWMAEncode.exe.");

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 || !File.Exists(xwmPath))
        {
            string details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"xWMAEncode failed (exit {process.ExitCode}): {Truncate(details, 300)}");
        }
    }

    private static async Task WaitUntilReadableAsync(string path, CancellationToken ct, int maxAttempts = 10, int delayMs = 150)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
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
