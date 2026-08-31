using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HaWinServer.Core;

public static class NetworkInfo
{
    /// <summary>
    /// Best-guess LAN IPv4 address: first "up", non-loopback interface that
    /// isn't a virtual/tunnel adapter. Good enough for a "copy this URL to
    /// reach HA from another device" convenience feature - not meant to be
    /// authoritative on multi-NIC machines (those get a Change... path via
    /// manual entry in a later version if it turns out to matter).
    /// </summary>
    public static string? TryGetLanIPv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .OrderBy(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1);

        foreach (var nic in candidates)
        {
            var ipv4 = nic.GetIPProperties().UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (ipv4 is not null)
            {
                return ipv4.ToString();
            }
        }

        return null;
    }

    public static bool IsPortListening(int port)
    {
        var listeners = System.Net.NetworkInformation.IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveTcpListeners();

        return listeners.Any(ep => ep.Port == port);
    }
}
