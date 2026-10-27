using System.Net;
using System.Net.Sockets;

namespace PcChecker.Core.Connections;

/// <summary>
/// Plain-language rules for the Connections page: loopback, listening on every network, the ports Windows itself
/// opens, and which programs accept connections from other devices. Pure.
/// </summary>
public static class ConnectionRules
{
    public const string ExposureText = "Accepts connections from other devices";

    // Ports Windows opens itself on every PC: RPC, NetBIOS, file sharing, Connected Devices, mDNS/LLMNR/SSDP/WS-Discovery,
    // Delivery Optimization, Remote Desktop and the WinRM/WS-Management listener.
    private static readonly HashSet<int> WindowsPorts = [135, 137, 138, 139, 445, 1900, 3389, 3702, 5040, 5050, 5353, 5355, 5357, 5358, 5985, 5986, 7680];

    // The dynamic RPC range Windows' own services (wininit, lsass, services, spoolsv, svchost) listen on.
    private const int DynamicRpcFirst = 49664;
    private const int DynamicRpcLast = 49999;

    private static readonly HashSet<string> WindowsProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "svchost.exe", "lsass.exe", "wininit.exe", "services.exe", "spoolsv.exe", "smss.exe", "csrss.exe",
        "winlogon.exe", "lsm.exe", "dasHost.exe", "SearchIndexer.exe", "MsMpEng.exe",
    };

    /// <summary>127.0.0.0/8 and ::1 (including IPv4-mapped loopback): traffic that never leaves the PC.</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(a);
    }

    /// <summary>0.0.0.0 or ::, the "every network adapter" address.</summary>
    public static bool IsAnyAddress(IPAddress? address) =>
        address is not null && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));

    /// <summary>True when the connection only talks to the PC itself (both ends loopback, or a loopback listener).</summary>
    public static bool IsLocalOnly(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsLoopback(endpoint.LocalAddress))
        {
            return false;
        }

        return endpoint.RemoteAddress is null || IsAnyAddress(endpoint.RemoteAddress) || IsLoopback(endpoint.RemoteAddress);
    }

    /// <summary>A TCP listener, or any UDP socket (UDP has no connections, so every UDP socket receives from anyone).</summary>
    public static bool IsListening(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Protocol == ConnectionProtocol.Tcp ? endpoint.State == TcpState.Listen : true;
    }

    /// <summary>A port Windows itself uses (so its being open is expected).</summary>
    public static bool IsWindowsPort(int port, bool windowsProcess) =>
        WindowsPorts.Contains(port) || (windowsProcess && port is >= DynamicRpcFirst and <= DynamicRpcLast);

    /// <summary>Windows' own process names, or a program in the Windows folder.</summary>
    public static bool IsWindowsProcess(string? name, string? path, string windowsDirectory = @"C:\Windows")
    {
        if (!string.IsNullOrWhiteSpace(name) && WindowsProcessNames.Contains(name))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var folder = windowsDirectory.Replace('/', '\\').TrimEnd('\\') + "\\";
        return path.Replace('/', '\\').StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    // Windows hands out outgoing (client) ports from 49152 up; an unconnected UDP socket there is almost always a
    // program's own client socket, not a service waiting for others.
    private const int FirstEphemeralPort = 49152;

    /// <summary>
    /// True for a TCP listener or a UDP socket on every network adapter (0.0.0.0 or ::) on a port Windows does not open
    /// itself: other devices on the network (and, without a firewall rule against it, the internet) can reach it. UDP
    /// sockets on Windows' client port range (49152 and up) are left out, since programs open those to send.
    /// </summary>
    public static bool AcceptsFromOtherDevices(NetworkEndpoint endpoint, bool windowsProcess)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsListening(endpoint) || !IsAnyAddress(endpoint.LocalAddress) || IsWindowsPort(endpoint.LocalPort, windowsProcess))
        {
            return false;
        }

        return endpoint.Protocol == ConnectionProtocol.Tcp || endpoint.LocalPort is > 0 and < FirstEphemeralPort;
    }

    /// <summary>Only IPv4 TCP connections that are not listening can be closed with SetTcpEntry.</summary>
    public static string? CloseRefusal(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Protocol != ConnectionProtocol.Tcp)
        {
            return "UDP has no connection to close. End the program instead.";
        }

        if (endpoint.LocalAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return "Windows can only close IPv4 connections from outside the program. End the program instead.";
        }

        if (endpoint.State == TcpState.Listen)
        {
            return "This is a program waiting for connections, not a connection. End the program to stop it.";
        }

        return endpoint.State is TcpState.TimeWait or TcpState.Closed or TcpState.DeleteTcb
            ? "This connection is already closing."
            : null;
    }
}
