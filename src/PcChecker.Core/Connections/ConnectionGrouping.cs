namespace PcChecker.Core.Connections;

/// <summary>
/// Groups connections by program (every process with the same program file is one program, so twelve Chrome
/// processes are one row) and filters the list. Pure.
/// </summary>
public static class ConnectionGrouping
{
    /// <summary>The group key of a process: its program path, else its name, lower case.</summary>
    public static string KeyOf(ConnectionProcess? process, int pid) =>
        process is null ? "pid:" + pid
        : (!string.IsNullOrWhiteSpace(process.Path) ? process.Path : process.Name).ToLowerInvariant();

    /// <summary>One group per program, the busiest first (programs that accept outside connections before others with the same count).</summary>
    public static IReadOnlyList<ConnectionGroup> Group(IEnumerable<NetworkEndpoint> endpoints, IReadOnlyDictionary<int, ConnectionProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(processes);
        return endpoints
            .GroupBy(e => KeyOf(processes.GetValueOrDefault(e.Pid), e.Pid))
            .Select(g =>
            {
                var list = g.ToList();
                var first = list.Select(e => processes.GetValueOrDefault(e.Pid)).FirstOrDefault(p => p is not null);
                bool windows = first?.IsWindows ?? false;
                return new ConnectionGroup(
                    g.Key,
                    first?.Name ?? (list[0].Pid == 0 ? "System Idle Process" : $"Process {list[0].Pid}"),
                    first?.Path,
                    list.Select(e => processes.GetValueOrDefault(e.Pid)?.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                    list.Select(e => e.Pid).Distinct().Order().ToList(),
                    list,
                    list.Count(e => e.Protocol == ConnectionProtocol.Tcp && e.State == TcpState.Listen),
                    list.Count(e => e.Protocol == ConnectionProtocol.Tcp && e.State == TcpState.Established),
                    list.Any(e => ConnectionRules.AcceptsFromOtherDevices(e, windows)),
                    windows);
            })
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.AcceptsFromOtherDevices)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The connections that pass the filter, the loopback switch and the search (program, PID, address, port, state or
    /// host name; every word must match).
    /// </summary>
    public static IReadOnlyList<NetworkEndpoint> Filter(IEnumerable<NetworkEndpoint> endpoints, IReadOnlyDictionary<int, ConnectionProcess> processes,
        ConnectionFilterKind filter, bool hideLoopback, string? search, Func<NetworkEndpoint, string?>? hostName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(processes);
        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return endpoints.Where(e =>
            {
                if (hideLoopback && ConnectionRules.IsLocalOnly(e))
                {
                    return false;
                }

                bool kind = filter switch
                {
                    ConnectionFilterKind.Listening => e.Protocol == ConnectionProtocol.Tcp && e.State == TcpState.Listen,
                    ConnectionFilterKind.Established => e.Protocol == ConnectionProtocol.Tcp && e.State == TcpState.Established,
                    _ => true,
                };
                if (!kind)
                {
                    return false;
                }

                if (words.Length == 0)
                {
                    return true;
                }

                var process = processes.GetValueOrDefault(e.Pid);
                var haystack = string.Join('\n', process?.Name, process?.Path, process?.Publisher, e.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ConnectionText.Protocol(e), ConnectionText.Endpoint(e.LocalAddress, e.LocalPort, friendly: false),
                    e.RemoteAddress is null ? null : ConnectionText.Endpoint(e.RemoteAddress, e.RemotePort, friendly: false),
                    ConnectionText.State(e), hostName?.Invoke(e));
                return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    /// <summary>The sort order inside the list: listeners that accept outside connections, then connected, then the rest.</summary>
    public static int Rank(NetworkEndpoint endpoint, bool windowsProcess)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (ConnectionRules.AcceptsFromOtherDevices(endpoint, windowsProcess))
        {
            return 0;
        }

        return endpoint.Protocol == ConnectionProtocol.Tcp
            ? endpoint.State switch
            {
                TcpState.Established => 1,
                TcpState.Listen => 2,
                _ => 3,
            }
            : 4;
    }
}
