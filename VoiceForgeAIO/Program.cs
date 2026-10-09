using System.Text.Json;
using Photino.NET;
using VoiceForgeAIO.LocalTts;

namespace VoiceForgeAIO;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Same crash-reporting safety net as the WinForms version had: an exception
        // before the window ever shows would otherwise vanish silently, since a
        // WinExe double-clicked from Explorer has no console to print to.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        try
        {
            // Local text-to-speech (OmniVoice, Qwen3-TTS) keeps its model in a background Python process;
            // never leave one running behind a closed app.
            OmniVoiceService.Shared = new OmniVoiceService();
            Qwen3TtsService.Shared = new Qwen3TtsService();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { OmniVoiceService.Shared?.Dispose(); Qwen3TtsService.Shared?.Dispose(); };

            var bridge = new ApiBridge();

            var window = new PhotinoWindow()
                .SetTitle("VoiceForge AIO")
                .SetUseOsDefaultLocation(false)
                .SetSize(new System.Drawing.Size(1280, 800))
                .Center()
                .RegisterWebMessageReceivedHandler((sender, message) =>
                {
                    var win = (PhotinoWindow)sender!;
                    // Fire-and-forget: handlers are async (network calls, file I/O,
                    // long-running batches) and must never block Photino's message
                    // loop. Responses/events are pushed back via win.SendWebMessage
                    // from within HandleMessageAsync itself.
                    _ = bridge.HandleMessageAsync(message, win);
                });

            // Set as plain properties rather than chaining, since PhotinoWindow's
            // fluent wrapper set (Set*) doesn't necessarily cover every property --
            // both are confirmed to exist as properties in Photino's own source,
            // so this is the safer way to set them.
            window.MinSize = new System.Drawing.Point(1000, 680);
            // icon.ico ships next to the exe (see the csproj) -- this drives the
            // title bar and taskbar icon while the app is running. The exe file's
            // own icon (shown in Explorer before you've even run it) is set
            // separately via <ApplicationIcon> in the csproj.
            window.IconFile = "icon.ico";

            bridge.Window = window;
            window.Load("wwwroot/index.html");
            window.WaitForClose();
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
        }
    }

    private static void ReportCrash(Exception? ex)
    {
        string message = ex?.ToString() ?? "Unknown error (no exception object was provided).";
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceForgeAIO", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now}]{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* best-effort */ }

        try
        {
            // Direct Win32 call rather than Photino's own dialog API: this needs to
            // work even if Photino itself is what crashed, so it can't depend on
            // Photino being in a working state.
            MessageBoxW(IntPtr.Zero, Truncate(message, 1500), "VoiceForge AIO -- Unhandled Error", 0x10 /* MB_ICONERROR */);
        }
        catch { /* if even this fails, the crash.log above is the fallback */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
