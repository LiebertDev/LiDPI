using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LiDPI.Core;
using LiDPI.Engines.Divert;
using Microsoft.Win32;

namespace LiDPI.UI.Services;

/// <summary>Everything the report needs from the view model, captured on the UI thread.</summary>
public sealed record DiagnosticSnapshot(
    string AppVersion,
    string Mode,
    string EngineState,
    BypassProfile Profile,
    string DohProvider,
    IReadOnlyList<KeyValuePair<string, string>> Settings,
    string LastHealth,
    string LastAutoFind,
    IReadOnlyList<LogEntry> Logs);

/// <summary>
/// Builds the plain-text diagnostic report attached to bug reports. Section labels are fixed English (the report
/// is read by the developer); log lines stay as written. Personal data is redacted by <see cref="Redact"/>.
/// <see cref="Build"/> queries processes, services and adapters: call it off the UI thread.
/// </summary>
public static partial class DiagnosticReport
{
    public static string DefaultFileName(DateTime now) => $"LiDPI-report-{now:yyyyMMdd-HHmm}.txt";

    public static string Build(DiagnosticSnapshot s)
    {
        var sb = new StringBuilder(32 * 1024);
        sb.AppendLine("LiDPI diagnostic report");
        sb.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        Section(sb, "System");
        Line(sb, "App version", s.AppVersion);
        Line(sb, "Windows", WindowsVersion());
        Line(sb, ".NET runtime", $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        Line(sb, "Administrator", Safe(() => DivertEngine.IsAdministrator() ? "yes" : "no"));
        Line(sb, "Windows UI language", System.Globalization.CultureInfo.CurrentUICulture.Name);
        Line(sb, "App language", LiDPI.Localization.Loc.Instance.Code);

        Section(sb, "Engine");
        Line(sb, "Mode", s.Mode);
        Line(sb, "State", s.EngineState);
        var p = s.Profile;
        Line(sb, "DNS mode", p.Dns.ToString());
        Line(sb, "DoH provider", s.DohProvider);
        Line(sb, "Profile", $"{p.DisplayName} (id: {p.Id})");
        Line(sb, "  SplitTlsHello", p.SplitTlsHello.ToString());
        Line(sb, "  SplitPosition", p.SplitPosition.ToString());
        Line(sb, "  SplitAtSni", p.SplitAtSni.ToString());
        Line(sb, "  DisorderSegments", p.DisorderSegments.ToString());
        Line(sb, "  FakePacket", p.FakePacket.ToString());
        Line(sb, "  FakeTtl", p.FakeTtl == 0 ? "0 (auto)" : p.FakeTtl.ToString());
        Line(sb, "  WrongChecksumFake", p.WrongChecksumFake.ToString());
        Line(sb, "  FakeWrongSeq", p.FakeWrongSeq.ToString());
        Line(sb, "  FakeRepeats", p.FakeRepeats.ToString());
        Line(sb, "  HttpHostTricks", p.HttpHostTricks.ToString());
        Line(sb, "  BlockQuic", p.BlockQuic.ToString());
        Line(sb, "  Dns", p.Dns.ToString());

        Section(sb, "Settings");
        foreach (var kv in s.Settings) Line(sb, kv.Key, kv.Value);

        Section(sb, "Environment");
        Line(sb, "Conflicting tools", Safe(ConflictingTools));
        Line(sb, "WinDivert service", Safe(WinDivertState));
        Line(sb, "IPv6 global connectivity", Safe(() => HasGlobalIpv6() ? "yes" : "no"));
        Line(sb, "Network available", Safe(() => NetworkInterface.GetIsNetworkAvailable() ? "yes" : "no"));

        Section(sb, "Game protection / other DPI tools");
        Line(sb, "Anti-cheat services installed", Safe(() => AntiCheatWatcher.InstalledServices() is { Count: > 0 } a ? string.Join(", ", a) : "none"));
        Line(sb, "Protected games running", Safe(() => AntiCheatWatcher.RunningTriggers() is { Count: > 0 } r ? string.Join(", ", r) : "none"));
        Line(sb, "DPI tool conflicts", Safe(() =>
        {
            // drivers are listed too; while LiDPI's own engine runs, a "WinDivert (driver)" entry is LiDPI itself
            var c = DpiConflicts.Scan(AntiCheatWatcher.ListProcessNames(), includeDrivers: true);
            return c.Count == 0 ? "none" : string.Join(", ", c.Select(i => $"{i.Kind}:{i.Name}"));
        }));

        Section(sb, "Last health check");
        sb.AppendLine(string.IsNullOrEmpty(s.LastHealth) ? "(none)" : s.LastHealth);

        Section(sb, "Last auto-find");
        sb.AppendLine(string.IsNullOrEmpty(s.LastAutoFind) ? "(none)" : s.LastAutoFind.TrimEnd());

        Section(sb, $"Log (last {s.Logs.Count} lines)");
        foreach (var l in s.Logs)
            sb.Append(l.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(l.Level).Append("] ").AppendLine(l.Message);

        return Redact(sb.ToString());
    }

    // ===================== redaction =====================

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%\w+)?(?![\w:])")]
    private static partial Regex Ipv6Regex();

    [GeneratedRegex(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.]*\d)")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?i)(\b[A-Z]:\\(?:Users|Documents and Settings)\\)[^\\/\r\n""<>|:*?]+")]
    private static partial Regex UserPathRegex();

    /// <summary>
    /// Removes personal data: MAC addresses, private / link-local IPs (kept partially: 192.168.x.x), the Windows user
    /// name (in and outside paths, → %USERNAME%) and the computer name (→ %COMPUTERNAME%).
    /// </summary>
    public static string Redact(string text)
    {
        text = MacRegex().Replace(text, "xx:xx:xx:xx:xx:xx");
        text = Ipv6Regex().Replace(text, m => RedactIpv6(m.Value));
        text = Ipv4Regex().Replace(text, RedactIpv4);
        text = UserPathRegex().Replace(text, "$1%USERNAME%");

        var user = Safe(() => Environment.UserName);
        if (user.Length >= 2 && !user.StartsWith('?'))
            text = Regex.Replace(text, $@"(?i)(?<![\w]){Regex.Escape(user)}(?![\w])", "%USERNAME%");
        var machine = Safe(() => Environment.MachineName);
        if (machine.Length >= 2 && !machine.StartsWith('?'))
            text = Regex.Replace(text, $@"(?i)(?<![\w-]){Regex.Escape(machine)}(?![\w-])", "%COMPUTERNAME%");
        return text;
    }

    private static string RedactIpv4(Match m)
    {
        var o = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(m.Groups[i + 1].Value, out o[i]) || o[i] > 255) return m.Value;
        bool priv = o[0] == 10
                    || (o[0] == 172 && o[1] is >= 16 and <= 31)
                    || (o[0] == 192 && o[1] == 168)
                    || (o[0] == 169 && o[1] == 254)
                    || (o[0] == 100 && o[1] is >= 64 and <= 127);   // carrier-grade NAT
        return priv ? $"{o[0]}.{o[1]}.x.x" : m.Value;
    }

    private static string RedactIpv6(string s)
    {
        var core = s.Split('%')[0];
        if (!IPAddress.TryParse(core, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return s;
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.IPv6Any)) return s;
        // every IPv6 address may identify the user's line (no NAT): keep only the first group
        var first = a.ToString().Split(':')[0];
        return $"{first}:x:x:x";
    }

    // ===================== probes =====================

    private static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
            var ubr = k?.GetValue("UBR") is int u ? $".{u}" : "";
            // ProductName still says "Windows 10" on Windows 11
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {display} (build {build}{ubr}, {RuntimeInformation.OSArchitecture})".Replace("  ", " ");
        }
        catch
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static string ConflictingTools()
    {
        var found = new List<string>();
        foreach (var name in DivertEngine.ConflictingTools)
        {
            Process[] procs = [];
            try { procs = Process.GetProcessesByName(name); } catch { /* ignore */ }
            if (procs.Length > 0) found.Add(name + ".exe");
            foreach (var pr in procs) pr.Dispose();
        }
        foreach (var svc in DriverServices.Running())
            if (!svc.Equals("WinDivert", StringComparison.OrdinalIgnoreCase)) found.Add($"driver service {svc}");
        return found.Count == 0 ? "none" : string.Join(", ", found);
    }

    private static string WinDivertState()
    {
        var running = DriverServices.Running();
        return running.Count == 0 ? "not running" : "running: " + string.Join(", ", running);
    }

    private static bool HasGlobalIpv6()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            bool hasV6Gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);
            if (!hasV6Gateway) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                var a = ua.Address;
                if (a.AddressFamily != AddressFamily.InterNetworkV6) continue;
                var b0 = a.GetAddressBytes()[0];
                if ((b0 & 0xE0) == 0x20) return true;   // 2000::/3 global unicast
            }
        }
        return false;
    }

    // ===================== helpers =====================

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.Append("== ").Append(title).AppendLine(" ==");
    }

    private static void Line(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(": ").AppendLine(value);

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return $"? ({ex.GetType().Name})"; }
    }
}
