using System.Net;
using System.Net.Sockets;

namespace LiDPI.Core;

/// <summary>
/// "Hızlı DNS" (<see cref="DnsMode.UdpRedirect"/>): plain DNS queries are NAT-ed by the packet engine to a resolver
/// that listens on a non-standard port, which DPI boxes that rewrite port-53 traffic do not touch. Unencrypted.
/// </summary>
public static class FastDns
{
    /// <summary>Persisted <see cref="AppSettings.DohProviderName"/> of the "Hızlı DNS" chip.</summary>
    public const string ChipKey = "FastUdp";

    /// <summary>Yandex DNS, alternative port.</summary>
    public static IPEndPoint DefaultV4 { get; } = new(IPAddress.Parse("77.88.8.8"), 1253);

    /// <summary>Yandex DNS (IPv6), alternative port.</summary>
    public static IPEndPoint DefaultV6 { get; } = new(IPAddress.Parse("2a02:6b8::feed:0ff"), 1253);

    /// <summary>
    /// Parses "ip:port" / "[ipv6]:port" (port defaults to 53) of the given family. Rejects unspecified,
    /// loopback, broadcast and multicast addresses.
    /// </summary>
    public static bool TryParse(string? input, AddressFamily family, out IPEndPoint endpoint)
    {
        endpoint = family == AddressFamily.InterNetworkV6 ? DefaultV6 : DefaultV4;
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s) || !IPEndPoint.TryParse(s, out var ep)) return false;
        var a = ep.Address;
        if (a.AddressFamily != family) return false;
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.Broadcast) ||
            IPAddress.IsLoopback(a) || a.IsIPv6Multicast) return false;
        if (family == AddressFamily.InterNetwork && a.GetAddressBytes()[0] >= 224) return false;   // multicast / reserved
        endpoint = new IPEndPoint(a, ep.Port == 0 ? 53 : ep.Port);
        return true;
    }

    /// <summary>Configured endpoint, or the default when <paramref name="input"/> is empty / invalid.</summary>
    public static IPEndPoint Resolve(string? input, AddressFamily family) =>
        TryParse(input, family, out var ep) ? ep : family == AddressFamily.InterNetworkV6 ? DefaultV6 : DefaultV4;

    /// <summary>
    /// True if some up, non-loopback interface has an IPv6 default gateway and a global unicast (2000::/3) address.
    /// Never throws.
    /// </summary>
    public static bool HasGlobalIpv6()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6)) continue;
                foreach (var ua in props.UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily == AddressFamily.InterNetworkV6 && (a.GetAddressBytes()[0] & 0xE0) == 0x20) return true;
                }
            }
        }
        catch
        {
            // treat as "no IPv6"
        }
        return false;
    }
}
