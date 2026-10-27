using System.Net;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.Core.Connections;

/// <summary>
/// Open TCP and UDP endpoints per program, for the Connections page (a TCPView-style list): read the tables, look up
/// host names, and close a connection. Ending a program goes through <see cref="IProcessService"/> with its safety rules.
/// </summary>
public interface IConnectionService
{
    /// <summary>Reads the TCP and UDP tables (IPv4 and IPv6) and the program behind each process id.</summary>
    Task<ConnectionSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The reverse DNS name of a remote address (asks the PC's DNS server), or null.</summary>
    Task<string?> LookUpHostAsync(IPAddress address, CancellationToken cancellationToken);

    /// <summary>True when connections can be closed (PC Checker runs as administrator on Windows).</summary>
    bool CanCloseConnections { get; }

    /// <summary>Closes an IPv4 TCP connection (SetTcpEntry with DELETE_TCB). The program may connect again. Journaled.</summary>
    Task<OperationResult> CloseConnectionAsync(NetworkEndpoint endpoint, ConnectionProcess? process, CancellationToken cancellationToken);

    /// <summary>Opens File Explorer with the program file selected.</summary>
    OperationResult OpenFileLocation(ConnectionProcess process);

    /// <summary>Opens a web search for who owns a remote address.</summary>
    OperationResult SearchAddressOnline(IPAddress address);
}

/// <summary>Turns a program from the connection list into the process record the process service acts on. Pure.</summary>
public static class ConnectionProcesses
{
    public static ProcessInfo ToProcessInfo(ConnectionProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new ProcessInfo(process.Pid, 0, process.Name, process.Path, null, process.StartTime, 0, 0, 0, 0, null, null, SignatureInfo.Unknown);
    }

    /// <summary>
    /// Why "End process" is not offered for this program, or null. The same checks the process service makes before
    /// ending anything (Windows' critical processes, PC Checker itself, a process whose start time is unknown), made up
    /// front so the page can say why instead of refusing after the confirmation.
    /// </summary>
    public static string? EndRefusal(ConnectionProcess? process, int pid, int ownPid)
    {
        if (CriticalProcessRules.Reason(process?.Name ?? NameOf(process, pid), pid, ownPid) is { } reason)
        {
            return reason;
        }

        if (process is null)
        {
            return "This program has already closed.";
        }

        return process.StartTime is null
            ? $"PC Checker cannot tell {process.Name} apart from a newer process with the same ID, so it leaves it alone. Use Task Manager if you need to end it."
            : null;
    }

    /// <summary>The name to show for a process id that has no process record (it ended between two reads).</summary>
    public static string NameOf(ConnectionProcess? process, int pid) =>
        process?.Name ?? (pid switch
        {
            0 => "System Idle Process",
            4 => "System",
            _ => $"Process {pid}",
        });
}
