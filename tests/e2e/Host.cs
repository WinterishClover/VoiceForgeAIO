using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Photino.NET;
using VoiceForgeAIO.LocalTts;

namespace VoiceForgeAIO;

internal static class Host
{
    static readonly ConcurrentQueue<string> Out = new();
    static readonly ConcurrentQueue<string> Dialogs = new();
    static async Task Main(string[] args)
    {
        int port = int.Parse(args[0]); string wwwroot = args[1]; string fakeBridge = args[2];
        PhotinoWindow.Outbox = m => Out.Enqueue(m);
        PhotinoWindow.Dialog = _ => Dialogs.TryDequeue(out var d) ? (d == "<cancel>" ? null : d) : null;
        OmniVoiceService.Shared = new OmniVoiceService() { BridgeScript = fakeBridge };
        Qwen3TtsService.Shared = new Qwen3TtsService() { BridgeScript = fakeBridge };
        var bridge = new ApiBridge(); var window = new PhotinoWindow(); bridge.Window = window;
        var l = new HttpListener(); l.Prefixes.Add($"http://localhost:{port}/"); l.Start();
        Console.WriteLine("READY"); Console.Out.Flush();
        while (true)
        {
            var ctx = await l.GetContextAsync();
            _ = Task.Run(async () =>
            {
                var req = ctx.Request; var res = ctx.Response; var path = req.Url!.AbsolutePath;
                try
                {
                    if (path == "/rpc") { var body = await new StreamReader(req.InputStream).ReadToEndAsync(); _ = bridge.HandleMessageAsync(body, window); res.StatusCode = 204; }
                    else if (path == "/poll") { var sb = new List<string>(); for (int i = 0; i < 50 && Out.IsEmpty; i++) await Task.Delay(20); while (Out.TryDequeue(out var m)) sb.Add(m); var json = "[" + string.Join(",", sb.Select(s => System.Text.Json.JsonSerializer.Serialize(s))) + "]"; var b = Encoding.UTF8.GetBytes(json); res.ContentType = "application/json"; await res.OutputStream.WriteAsync(b); }
                    else if (path == "/test/dialog") { Dialogs.Enqueue(req.QueryString["v"] ?? ""); res.StatusCode = 204; }
                    else if (path == "/test/fake") { FakeCloudProviderCtl(req.QueryString["voicesFail"]); res.StatusCode = 204; }
                    else if (path == "/test/calls") { var b = Encoding.UTF8.GetBytes(Providers.FakeCloudProvider.Calls.ToString()); await res.OutputStream.WriteAsync(b); }
                    else
                    {
                        var f = Path.Combine(wwwroot, path == "/" ? "index.html" : path.TrimStart('/'));
                        if (!File.Exists(f)) res.StatusCode = 404;
                        else { res.ContentType = Path.GetExtension(f) switch { ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", _ => "application/octet-stream" }; await res.OutputStream.WriteAsync(File.ReadAllBytes(f)); }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); res.StatusCode = 500; }
                finally { res.Close(); }
            });
        }
    }
    static void FakeCloudProviderCtl(string? v) => Providers.FakeCloudProvider.VoicesFail = v == "1";
}
