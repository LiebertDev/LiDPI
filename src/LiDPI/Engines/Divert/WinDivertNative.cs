using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace LiDPI.Engines.Divert;

/// <summary>
/// WINDIVERT_ADDRESS (WinDivert 2.2, windivert.h). 80 bytes:
/// INT64 Timestamp; UINT32 bitfield {Layer:8, Event:8, Sniffed:1, Outbound:1, Loopback:1, Impostor:1,
/// IPv6:1, IPChecksum:1, TCPChecksum:1, UDPChecksum:1, Reserved1:8}; UINT32 Reserved2;
/// union { WINDIVERT_DATA_NETWORK {UINT32 IfIdx; UINT32 SubIfIdx;} ...; UINT8 Reserved3[64]; }.
/// MSVC allocates bitfields from the least significant bit.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Bits;
    [FieldOffset(12)] public uint Reserved2;
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    public readonly byte Layer => (byte)(Bits & 0xFF);
    public readonly byte Event => (byte)((Bits >> 8) & 0xFF);
    public bool Sniffed { readonly get => Get(16); set => Set(16, value); }
    public bool Outbound { readonly get => Get(17); set => Set(17, value); }
    public bool Loopback { readonly get => Get(18); set => Set(18, value); }
    public bool Impostor { readonly get => Get(19); set => Set(19, value); }
    public bool IPv6 { readonly get => Get(20); set => Set(20, value); }
    public bool IPChecksum { readonly get => Get(21); set => Set(21, value); }
    public bool TCPChecksum { readonly get => Get(22); set => Set(22, value); }
    public bool UDPChecksum { readonly get => Get(23); set => Set(23, value); }

    private readonly bool Get(int bit) => (Bits & (1u << bit)) != 0;
    private void Set(int bit, bool v) => Bits = v ? Bits | (1u << bit) : Bits & ~(1u << bit);
}

/// <summary>P/Invoke surface of WinDivert.dll 2.2.</summary>
internal static unsafe class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public const int LayerNetwork = 0;
    public const ulong FlagSniff = 0x0001;
    public const ulong FlagDrop = 0x0002;
    public const ulong FlagRecvOnly = 0x0004;
    public const ulong FlagSendOnly = 0x0008;
    public const int ShutdownRecv = 1;
    public const int ShutdownSend = 2;
    public const int ShutdownBoth = 3;

    /// <summary>WINDIVERT_MTU_MAX (40 + 0xFFFF).</summary>
    public const int MtuMax = 40 + 0xFFFF;

    public static readonly IntPtr InvalidHandle = new(-1);

    static WinDivertNative()
    {
        if (Marshal.SizeOf<WinDivertAddress>() != 80 || sizeof(WinDivertAddress) != 80)
            throw new InvalidOperationException("WINDIVERT_ADDRESS is not 80 bytes.");
    }

    [DllImport(Dll, SetLastError = true, ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern IntPtr WinDivertOpen([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecv(IntPtr handle, byte* packet, uint packetLen, uint* recvLen, WinDivertAddress* addr);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(IntPtr handle, byte* packet, uint packetLen, uint* sendLen, WinDivertAddress* addr);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertShutdown(IntPtr handle, int how);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCalcChecksums(byte* packet, uint packetLen, WinDivertAddress* addr, ulong flags);

    [DllImport(Dll, SetLastError = true, ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinDivertHelperCompileFilter([MarshalAs(UnmanagedType.LPStr)] string filter, int layer,
        byte* obj, uint objLen, sbyte** errorStr, uint* errorPos);

    /// <summary>Validates a filter in user mode (no driver / admin needed). Returns null if valid, else the error.</summary>
    public static string? CheckFilter(string filter)
    {
        sbyte* err = null;
        uint pos = 0;
        if (WinDivertHelperCompileFilter(filter, LayerNetwork, null, 0, &err, &pos)) return null;
        string msg = err != null ? new string(err) : "unknown error";
        return $"{msg} (position {pos})";
    }

    /// <summary>Recomputes all checksums and sends; returns false on failure.</summary>
    public static bool Send(IntPtr h, Span<byte> packet, ref WinDivertAddress addr, bool recalc)
    {
        fixed (byte* p = packet)
        fixed (WinDivertAddress* a = &addr)
        {
            if (recalc) WinDivertHelperCalcChecksums(p, (uint)packet.Length, a, 0);
            return WinDivertSend(h, p, (uint)packet.Length, null, a);
        }
    }

    /// <summary>Recomputes all checksums (sets the address *Checksum flags).</summary>
    public static void CalcChecksums(Span<byte> packet, ref WinDivertAddress addr)
    {
        fixed (byte* p = packet)
        fixed (WinDivertAddress* a = &addr)
            WinDivertHelperCalcChecksums(p, (uint)packet.Length, a, 0);
    }

    /// <summary>Sends without touching checksums.</summary>
    public static bool SendRaw(IntPtr h, ReadOnlySpan<byte> packet, ref WinDivertAddress addr)
    {
        fixed (byte* p = packet)
        fixed (WinDivertAddress* a = &addr)
            return WinDivertSend(h, p, (uint)packet.Length, null, a);
    }

    /// <summary>Explanation of a WinDivertOpen failure code in the UI language (keys "ErrOpen{code}").</summary>
    public static string DescribeOpenError(int code) =>
        LiDPI.UI.Strings.ErrOpen(code, () => new Win32Exception(code).Message);
}

/// <summary>
/// Extracts the embedded WinDivert binaries to %ProgramData%\LiDPI\bin (admin-only writable) and loads the DLL.
/// A per-user folder does not work: the kernel cannot resolve the driver path there (service start error 3),
/// and a user-writable folder for binaries loaded by an elevated process is a privilege-escalation risk.
/// </summary>
internal static class WinDivertLoader
{
    private const string DllResource = "LiDPI.native.WinDivert.dll";
    private const string SysResource = "LiDPI.native.WinDivert64.sys";
    private const string DllName = "WinDivert.dll";
    private const string SysName = "WinDivert64.sys";

    private static readonly object Gate = new();
    private static IntPtr _lib;

    public static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LiDPI", "bin");

    /// <summary>Old per-user extraction folder (pre-fix); removed best effort.</summary>
    private static string LegacyDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiDPI", "bin");

    /// <summary>Directory the DLL was loaded from (null until loaded).</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>Full path of the driver file next to the loaded DLL (null until loaded).</summary>
    public static string? SysPath => LoadedFrom is null ? null : Path.Combine(LoadedFrom, SysName);

    /// <summary>Extracts (if needed) and loads WinDivert.dll. Idempotent; throws on failure.</summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_lib != IntPtr.Zero) return;

            var asm = typeof(WinDivertLoader).Assembly;
            byte[] dll = ReadResource(asm, DllResource);
            byte[] sys = ReadResource(asm, SysResource);

            string dir = BaseDirectory;
            try
            {
                Extract(dir, dll, sys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Old files locked (e.g. driver still loaded from there): use a content-addressed folder.
                string tag = Convert.ToHexString(SHA256.HashData(dll.Concat(sys).ToArray()))[..12];
                dir = Path.Combine(BaseDirectory, tag);
                Extract(dir, dll, sys);
            }

            _lib = NativeLibrary.Load(Path.Combine(dir, DllName));
            LoadedFrom = dir;
            try
            {
                NativeLibrary.SetDllImportResolver(asm, (name, _, _) =>
                    name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase) ? _lib : IntPtr.Zero);
            }
            catch (InvalidOperationException)
            {
                // Another resolver is registered for this assembly; the already-loaded module
                // is still found by name by the OS loader.
            }

            try
            {
                if (Directory.Exists(LegacyDirectory)) Directory.Delete(LegacyDirectory, recursive: true);
            }
            catch
            {
                // best effort (files may be locked by an old driver instance)
            }
        }
    }

    private static void Extract(string dir, byte[] dll, byte[] sys)
    {
        PrepareSecureDirectory(dir);
        // Re-protect pre-existing files before trusting their hash (they may carry an older, looser ACL).
        // New files inherit the protected directory ACL.
        SecureFile(Path.Combine(dir, DllName));
        SecureFile(Path.Combine(dir, SysName));
        WriteIfDifferent(Path.Combine(dir, DllName), dll);
        WriteIfDifferent(Path.Combine(dir, SysName), sys);
    }

    /// <summary>
    /// Creates (or re-secures) a directory so only SYSTEM/Administrators can write and Users can read/execute.
    /// Junctions/symlinks planted in its place are removed first.
    /// </summary>
    internal static void PrepareSecureDirectory(string dir)
    {
        // Walk from %ProgramData%\LiDPI down to dir; refuse reparse points anywhere on that chain.
        string root = Path.GetDirectoryName(BaseDirectory)!; // ...\LiDPI
        string full = Path.GetFullPath(dir);
        string rel = Path.GetRelativePath(root, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw new ArgumentException("directory must be under " + root, nameof(dir));
        var chain = new List<string> { root };
        var cur = root;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            cur = Path.Combine(cur, part);
            chain.Add(cur);
        }
        foreach (var d in chain)
        {
            var info = new DirectoryInfo(d);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                info.Delete(); // removes the link itself, not the target
            if (!Directory.Exists(d))
                Directory.CreateDirectory(d);
            new DirectoryInfo(d).SetAccessControl(BuildDirectorySecurity());
        }
    }

    private static DirectorySecurity BuildDirectorySecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    private static void SecureFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete(); // planted symlink: drop it, a fresh copy is written next
            return;
        }
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    private static void WriteIfDifferent(string path, byte[] content)
    {
        if (File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (SHA256.HashData(fs).AsSpan().SequenceEqual(SHA256.HashData(content))) return;
            }
            catch (IOException) { /* unreadable: try to overwrite */ }
        }
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, content);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    private static byte[] ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(LiDPI.UI.Strings.ErrResourceMissing(name));
        using var ms = new MemoryStream((int)s.Length);
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Stops leftover WinDivert driver services via the Service Control Manager.</summary>
internal static class DriverServices
{
    private static readonly string[] Names = ["WinDivert", "WinDivert1.4", "WinDivert1.3", "WinDivert1.2", "WinDivert1.1", "WinDivert14"];

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x00010000;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr svc, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr svc, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr svc);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr h);

    /// <summary>Names of WinDivert services that are currently running.</summary>
    public static List<string> Running()
    {
        var result = new List<string>();
        Visit((name, svc) =>
        {
            if (QueryServiceStatus(svc, out var st) && st.CurrentState != ServiceStopped) result.Add(name);
        });
        return result;
    }

    /// <summary>True if the named WinDivert service exists and is not stopped.</summary>
    public static bool IsRunning(string serviceName)
    {
        bool running = false;
        Visit([serviceName], (_, svc) =>
            running = QueryServiceStatus(svc, out var st) && st.CurrentState != ServiceStopped);
        return running;
    }

    /// <summary>
    /// Asks a single WinDivert service to stop. Only call for a service this process started, after
    /// all of its handles are closed: other tools may share the same driver. Best effort; never throws.
    /// </summary>
    public static void Stop(string serviceName, Action<string>? log)
    {
        Visit([serviceName], (name, svc) =>
        {
            if (!QueryServiceStatus(svc, out var st) || st.CurrentState == ServiceStopped) return;
            if (ControlService(svc, ServiceControlStop, out _))
                log?.Invoke(LiDPI.UI.Strings.LogDriverStopped(name));
            // failure usually means another program still holds a handle: leave it alone
        });
    }

    /// <summary>
    /// Deletes the named driver service if it exists, is STOPPED, and its ImagePath points somewhere other than
    /// <paramref name="expectedSysPath"/> (e.g. the old per-user folder). WinDivertOpen then recreates it with
    /// the correct path. A running service is never touched (another tool may be using it). Never throws.
    /// </summary>
    public static void DeleteIfStale(string serviceName, string expectedSysPath, Action<string>? log)
    {
        try
        {
            string? image;
            using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}"))
                image = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (image is null) return;
            if (SamePath(image, expectedSysPath)) return;

            IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) return;
            try
            {
                IntPtr svc = OpenService(scm, serviceName, ServiceQueryStatus | Delete);
                if (svc == IntPtr.Zero) return;
                try
                {
                    if (!QueryServiceStatus(svc, out var st) || st.CurrentState != ServiceStopped) return;
                    if (DeleteService(svc))
                        log?.Invoke(LiDPI.UI.Strings.LogDriverStaleDeleted(serviceName, image.Trim()));
                    else
                        log?.Invoke(LiDPI.UI.Strings.LogDriverStaleDeleteFailed(serviceName, Marshal.GetLastPInvokeError()));
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }
        catch
        {
            // best effort
        }
    }

    private static bool SamePath(string imagePath, string expected)
    {
        string p = Environment.ExpandEnvironmentVariables(imagePath.Trim().Trim('"'));
        foreach (var prefix in new[] { @"\??\", @"\\?\" })
            if (p.StartsWith(prefix, StringComparison.Ordinal)) { p = p[prefix.Length..]; break; }
        try
        {
            return string.Equals(Path.GetFullPath(p), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void Visit(Action<string, IntPtr> action) => Visit(Names, action);

    private static void Visit(string[] names, Action<string, IntPtr> action)
    {
        try
        {
            IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) return;
            try
            {
                foreach (var name in names)
                {
                    IntPtr svc = OpenService(scm, name, ServiceQueryStatus | ServiceStop);
                    if (svc == IntPtr.Zero) continue;
                    try { action(name, svc); }
                    finally { CloseServiceHandle(svc); }
                }
            }
            finally { CloseServiceHandle(scm); }
        }
        catch
        {
            // best effort
        }
    }
}
