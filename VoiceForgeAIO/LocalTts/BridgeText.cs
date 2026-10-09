namespace VoiceForgeAIO.LocalTts;

/// <summary>Helpers for talking to the local Python bridges (tools/omnivoice_bridge.py, tools/qwen3tts_bridge.py), which may run inside WSL.</summary>
internal static class BridgeText
{
    /// <summary>C:\Users\me\file.wav becomes /mnt/c/Users/me/file.wav, the way WSL sees the same file.</summary>
    public static string ToWslPath(string windowsPath)
    {
        var full = windowsPath.Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':') return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..];
        return full;
    }

    /// <summary>wsl.exe writes its own messages in UTF-16, which arrive as text with a zero byte between letters; drop them so the message is readable.</summary>
    public static string Clean(string line) => line.Replace("\0", "");

    /// <summary>The last meaningful lines of some output: progress bars (which redraw one line over and over) and blank lines are dropped.</summary>
    public static string Meaningful(string output, int count) => string.Join("\n", output.Split(['\r', '\n'])
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(l, @"\d+%\|") && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(Loading \.\.|\d+% done)")).TakeLast(count));

    /// <summary>Reads one line the bridge printed. Anything that is not a PROGRESS, RESULT or ERROR line is plain log output.</summary>
    public static (string Kind, string Text) ParseLine(string line)
    {
        foreach (var kind in new[] { "PROGRESS", "RESULT", "ERROR" })
            if (line.StartsWith(kind + " ", StringComparison.Ordinal)) return (kind, line[(kind.Length + 1)..].Trim());
        return ("LOG", line);
    }
}
