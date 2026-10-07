using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace LiDPI.Engines.Proxy;

/// <summary>
/// Sets / restores the per-user WinINet proxy (HKCU Internet Settings). The previous values are
/// persisted to %AppData%\LiDPI\proxy-backup.json before any change so a crash can be recovered.
/// </summary>
internal static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Bypass = "<local>;localhost;127.*;10.*;192.168.*";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private static readonly object Gate = new();

    public static string BackupPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiDPI", "proxy-backup.json");

    private sealed class Backup
    {
        public int? ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
        /// <summary>The value we wrote (e.g. "127.0.0.1:8881").</summary>
        public string OurServer { get; set; } = "";
    }

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);

    /// <summary>Points the system proxy at <paramref name="server"/>, backing up the current values first.</summary>
    public static void Enable(string server)
    {
        lock (Gate)
        {
            RestoreIfDirtyCore();   // never back up our own leftover values
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

            var backup = new Backup
            {
                ProxyEnable = key.GetValue("ProxyEnable") is int e ? e : null,
                ProxyServer = key.GetValue("ProxyServer") as string,
                ProxyOverride = key.GetValue("ProxyOverride") as string,
                OurServer = server,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            string tmp = BackupPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(backup));
            File.Move(tmp, BackupPath, overwrite: true);

            key.SetValue("ProxyServer", server, RegistryValueKind.String);
            key.SetValue("ProxyOverride", Bypass, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            Notify();
        }
    }

    /// <summary>Restores the backed-up values if the system proxy still points at us. Never throws.</summary>
    public static bool RestoreIfDirty()
    {
        lock (Gate)
        {
            try { return RestoreIfDirtyCore(); }
            catch { return false; }
        }
    }

    private static bool RestoreIfDirtyCore()
    {
        if (!File.Exists(BackupPath)) return false;

        Backup? b = null;
        try { b = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)); }
        catch { /* corrupt backup */ }

        bool restored = false;
        if (b is not null && b.OurServer.Length > 0)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            bool stillOurs = string.Equals(key.GetValue("ProxyServer") as string, b.OurServer, StringComparison.OrdinalIgnoreCase);
            if (stillOurs)
            {
                SetOrDelete(key, "ProxyEnable", b.ProxyEnable, RegistryValueKind.DWord);
                SetOrDelete(key, "ProxyServer", b.ProxyServer, RegistryValueKind.String);
                SetOrDelete(key, "ProxyOverride", b.ProxyOverride, RegistryValueKind.String);
                Notify();
                restored = true;
            }
        }
        else
        {
            // Corrupt / empty backup: the old values are lost, but at least don't leave the
            // system pointing at a dead LiDPI proxy.
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (LooksLikeOurs(key.GetValue("ProxyServer") as string))
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                Notify();
                restored = true;
            }
        }
        try { File.Delete(BackupPath); } catch { /* ignore */ }
        return restored;
    }

    /// <summary>True for "127.0.0.1:&lt;port&gt;" within the port range ProxyEngine binds to first.</summary>
    private static bool LooksLikeOurs(string? server)
    {
        const string prefix = "127.0.0.1:";
        if (server is null || !server.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(server.AsSpan(prefix.Length), out int port)
            && port >= ProxyEngine.DefaultPort && port < ProxyEngine.DefaultPort + ProxyEngine.PortSearchRange;
    }

    private static void SetOrDelete(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, kind);
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}
