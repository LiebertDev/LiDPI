using System.Runtime.InteropServices;

namespace LiDPI.Dns;

/// <summary>Windows DNS Client (Dnscache) helpers.</summary>
public static class DnsCache
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DnsFlushResolverCacheNative();

    /// <summary>
    /// Flushes the system DNS resolver cache (like <c>ipconfig /flushdns</c>). Returns true on success. Never throws.
    /// </summary>
    public static bool Flush()
    {
        try { return DnsFlushResolverCacheNative() != 0; }
        catch { return false; }
    }
}
