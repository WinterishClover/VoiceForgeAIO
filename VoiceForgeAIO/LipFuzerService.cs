using System.Diagnostics;

namespace VoiceForgeAIO;

/// <summary>
/// Wraps LipFuzer.exe to combine matching &lt;name&gt;.xwm + &lt;name&gt;.lip pairs into
/// &lt;name&gt;.fuz -- Bethesda's own tool for this (found alongside LipGenerator.exe in
/// Skyrim SE's Tools\LipGen\LipGenerator folder). Not bundled, same reasoning as the
/// other shelled-out tools.
///
/// Unlike FaceFXWrapper/xWMAEncode (one file per call), LipFuzer operates on an
/// entire directory in one pass, matching files by basename. So this runs once per
/// VoiceType output folder after all of that folder's WAV/LIP/XWM generation is
/// done, rather than once per line.
///
/// CLI contract (from the tool's own -h output):
///   LIPFuzer [-s srcDir] [-d dstDir] [-a audioExt] [-l lipExt] [-f fuzedExt] [--norec] [-u] [-i incl] [-e excl] [-v verbosity]
/// Defaults (audioExt=xwm, lipExt=lip, fuzedExt=fuz) already match what this app
/// produces, so no overrides are needed beyond pointing -s/-d at the folder.
/// </summary>
internal static class LipFuzerService
{
    public static async Task FuzeDirectoryAsync(string exePath, string directory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("LipFuzer.exe path isn't set or doesn't exist (check Settings).");
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"Directory not found: {directory}");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(directory);
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(directory);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start LipFuzer.exe.");

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"LipFuzer failed (exit {process.ExitCode}): {Truncate(details, 300)}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(no output)" : (s.Length <= max ? s : s[..max] + "...");
}
