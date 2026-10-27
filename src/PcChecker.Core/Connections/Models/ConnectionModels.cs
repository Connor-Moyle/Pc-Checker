using System.Net;

namespace PcChecker.Core.Connections;

public enum ConnectionProtocol
{
    Tcp,
    Udp,
}

/// <summary>TCP connection states as GetExtendedTcpTable reports them (MIB_TCP_STATE).</summary>
public enum TcpState
{
    Unknown = 0,
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
}

/// <summary>
/// One row of the TCP or UDP table: the local end, the remote end (TCP only) and the process that owns it. UDP has no
/// state and no remote end.
/// </summary>
public sealed record NetworkEndpoint(
    ConnectionProtocol Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int? RemotePort,
    TcpState State,
    int Pid)
{
    public bool IsIPv6 => LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    /// <summary>A key that stays the same for the same connection between two reads.</summary>
    public string Key => $"{Protocol}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}|{Pid}";
}

/// <summary>The program behind a process id, for the Connections page.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">File name ("chrome.exe"), or "System" and the like for kernel processes.</param>
/// <param name="Path">Full path of the program file, when Windows tells.</param>
/// <param name="Publisher">Company name from the file's version information, when known.</param>
/// <param name="StartTime">When this run of the program started (identifies it, as Windows reuses ids).</param>
/// <param name="IsWindows">True for Windows' own processes (System, svchost, lsass…, or a program in the Windows folder).</param>
public sealed record ConnectionProcess(int Pid, string Name, string? Path, string? Publisher, DateTimeOffset? StartTime, bool IsWindows);

/// <summary>One read of the connection tables.</summary>
public sealed record ConnectionSnapshot(
    IReadOnlyList<NetworkEndpoint> Endpoints,
    IReadOnlyDictionary<int, ConnectionProcess> Processes,
    DateTimeOffset Time,
    string? Warning)
{
    public static ConnectionSnapshot Empty(string? warning) => new([], new Dictionary<int, ConnectionProcess>(), DateTimeOffset.Now, warning);
}

/// <summary>The quick filters of the connection list.</summary>
public enum ConnectionFilterKind
{
    All,
    Listening,
    Established,
}

/// <summary>All connections of one program (every process with the same file), with counts for the program list.</summary>
public sealed record ConnectionGroup(
    string Key,
    string Name,
    string? Path,
    string? Publisher,
    IReadOnlyList<int> Pids,
    IReadOnlyList<NetworkEndpoint> Endpoints,
    int Listening,
    int Established,
    bool AcceptsFromOtherDevices,
    bool IsWindows)
{
    public int Count => Endpoints.Count;
}
