using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PcChecker.Core.Connections;

/// <summary>Addresses, ports and states in the words the Connections page shows. Pure.</summary>
public static class ConnectionText
{
    /// <summary>"192.168.1.20:443", "[2a00:1450::200e]:443", "Any:3389" for 0.0.0.0 and "This PC:6742" for loopback.</summary>
    public static string Endpoint(IPAddress? address, int? port, bool friendly = true)
    {
        if (address is null)
        {
            return "—";
        }

        string host = friendly && ConnectionRules.IsAnyAddress(address) ? "Any"
            : friendly && ConnectionRules.IsLoopback(address) ? "This PC"
            : Address(address);
        string portText = port is { } p && p > 0 ? p.ToString(CultureInfo.InvariantCulture) : "*";
        bool bracket = address.AddressFamily == AddressFamily.InterNetworkV6 && host != "Any" && host != "This PC";
        return bracket ? $"[{host}]:{portText}" : $"{host}:{portText}";
    }

    /// <summary>The address as text; IPv4-mapped IPv6 addresses as plain IPv4.</summary>
    public static string Address(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    /// <summary>The remote end for the list: blank for UDP and listeners.</summary>
    public static string Remote(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Protocol == ConnectionProtocol.Udp || endpoint.State == TcpState.Listen || endpoint.RemoteAddress is null
            || ConnectionRules.IsAnyAddress(endpoint.RemoteAddress))
        {
            return string.Empty;
        }

        return Endpoint(endpoint.RemoteAddress, endpoint.RemotePort);
    }

    /// <summary>"Listening", "Connected", "Closing"… in plain words.</summary>
    public static string State(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Protocol == ConnectionProtocol.Udp)
        {
            return "Open (UDP)";
        }

        return endpoint.State switch
        {
            TcpState.Listen => "Listening",
            TcpState.Established => "Connected",
            TcpState.SynSent => "Connecting",
            TcpState.SynReceived => "Being connected",
            TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing or TcpState.LastAck => "Closing",
            TcpState.CloseWait => "Closed by the other side",
            TcpState.TimeWait => "Recently closed",
            TcpState.Closed or TcpState.DeleteTcb => "Closed",
            _ => "Unknown",
        };
    }

    /// <summary>The technical state name, for the tooltip ("ESTABLISHED", "TIME_WAIT").</summary>
    public static string TechnicalState(TcpState state) => state switch
    {
        TcpState.Listen => "LISTEN",
        TcpState.Established => "ESTABLISHED",
        TcpState.SynSent => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECEIVED",
        TcpState.FinWait1 => "FIN_WAIT_1",
        TcpState.FinWait2 => "FIN_WAIT_2",
        TcpState.CloseWait => "CLOSE_WAIT",
        TcpState.Closing => "CLOSING",
        TcpState.LastAck => "LAST_ACK",
        TcpState.TimeWait => "TIME_WAIT",
        TcpState.Closed => "CLOSED",
        TcpState.DeleteTcb => "DELETE_TCB",
        _ => "UNKNOWN",
    };

    /// <summary>"TCP", "TCPv6", "UDP", "UDPv6".</summary>
    public static string Protocol(NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var name = endpoint.Protocol == ConnectionProtocol.Tcp ? "TCP" : "UDP";
        return endpoint.IsIPv6 ? name + "v6" : name;
    }

    /// <summary>One line for the clipboard: program, PID, protocol, local, remote, state and host name.</summary>
    public static string CopyLine(NetworkEndpoint endpoint, string program, string? hostName)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var parts = new List<string>
        {
            program,
            "PID " + endpoint.Pid.ToString(CultureInfo.InvariantCulture),
            Protocol(endpoint),
            Endpoint(endpoint.LocalAddress, endpoint.LocalPort, friendly: false),
        };
        var remote = endpoint.RemoteAddress is null || endpoint.Protocol == ConnectionProtocol.Udp
            ? string.Empty
            : Endpoint(endpoint.RemoteAddress, endpoint.RemotePort, friendly: false);
        if (remote.Length > 0)
        {
            parts.Add(remote);
        }

        parts.Add(endpoint.Protocol == ConnectionProtocol.Tcp ? TechnicalState(endpoint.State) : "UDP");
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            parts.Add(hostName);
        }

        return string.Join("\t", parts);
    }

    /// <summary>A web search for a remote address ("who is 140.82.112.4").</summary>
    public static string SearchUrl(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(Address(address) + " ip address owner");
    }
}
