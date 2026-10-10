namespace Photino.NET
{
    public class PhotinoWindow
    {
        public static Action<string>? Outbox;
        public static Func<string, string?> Dialog = _ => null;
        public void SendWebMessage(string m) => Outbox?.Invoke(m);
        public string[] ShowOpenFile(string title, string? def, bool multi, (string, string[])[] filters) { var r = Dialog("openFile"); return r is null ? new string[0] : new[] { r }; }
        public string[] ShowOpenFolder(string title, string? def, bool multi) { var r = Dialog("openFolder"); return r is null ? new string[0] : new[] { r }; }
        public string? ShowSaveFile(string title, string? def, (string, string[])[] filters) => Dialog("saveFile");
    }
}
namespace System.Security.Cryptography
{
    public enum DataProtectionScope { CurrentUser, LocalMachine }
    // Test stand-in for Windows DPAPI (the real one throws PlatformNotSupported off Windows).
    public static class ProtectedData
    {
        public static byte[] Protect(byte[] d, byte[]? e, DataProtectionScope s) => d.Reverse().ToArray();
        public static byte[] Unprotect(byte[] d, byte[]? e, DataProtectionScope s) => d.Reverse().ToArray();
    }
}
