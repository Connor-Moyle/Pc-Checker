using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Connections;

/// <summary>
/// <see cref="IConnectionService"/> on Windows: GetExtendedTcpTable and GetExtendedUdpTable (the *_OWNER_PID tables,
/// IPv4 and IPv6) read into byte buffers and parsed by <see cref="EndpointTableParser"/>; the program behind each
/// process id from the same native calls the Processes page uses (cached per run of a program); host names through
/// <see cref="Dns"/>; and SetTcpEntry with DELETE_TCB to close an IPv4 TCP connection.
/// </summary>
public sealed class WindowsConnectionService : IConnectionService
{
    private const string Module = "Connections";
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MibTcpStateDeleteTcb = 12;

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsConnectionService> _logger;
    private readonly ConcurrentDictionary<(int Pid, DateTimeOffset? Start), ConnectionProcess> _processes = new();
    private readonly ConcurrentDictionary<string, string?> _publishers = new(StringComparer.OrdinalIgnoreCase);

    public WindowsConnectionService(IChangeGuard guard, ILogger<WindowsConnectionService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public bool CanCloseConnections => PlatformGuard.IsWindows && Environment.IsPrivilegedProcess;

    private static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : @"C:\Windows";

    public Task<ConnectionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(ConnectionSnapshot.Empty("Connections can only be read on Windows."));
        }

        return Task.Run(() =>
        {
            var endpoints = new List<NetworkEndpoint>();
            int failed = 0;
            foreach (var (tcp, family, parse) in new (bool, int, Func<byte[], IReadOnlyList<NetworkEndpoint>>)[]
                     {
                         (true, AfInet, b => EndpointTableParser.ParseTcp4(b)),
                         (true, AfInet6, b => EndpointTableParser.ParseTcp6(b)),
                         (false, AfInet, b => EndpointTableParser.ParseUdp4(b)),
                         (false, AfInet6, b => EndpointTableParser.ParseUdp6(b)),
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = ReadTable(tcp, family);
                if (table is null)
                {
                    failed++;
                    continue;
                }

                endpoints.AddRange(parse(table));
            }

            var processes = ReadProcesses(endpoints.Select(e => e.Pid).Distinct());
            string? warning = failed == 4 ? "Windows did not hand out the connection tables." : failed > 0 ? "Part of the connection list could not be read." : null;
            return new ConnectionSnapshot(endpoints, processes, DateTimeOffset.Now, warning);
        }, cancellationToken);
    }

    private byte[]? ReadTable(bool tcp, int family)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint status = Call(tcp, IntPtr.Zero, ref size, family);
            for (int attempt = 0; attempt < 5 && status == ErrorInsufficientBuffer; attempt++)
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // The table can grow between the two calls: leave some room.
                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
                status = Call(tcp, buffer, ref size, family);
            }

            if (status != 0 || buffer == IntPtr.Zero)
            {
                _logger.LogDebug("Reading the {Protocol} table (family {Family}) failed with {Status}.", tcp ? "TCP" : "UDP", family, status);
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            _logger.LogDebug(ex, "The connection table could not be read.");
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static uint Call(bool tcp, IntPtr buffer, ref int size, int family) => tcp
        ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
        : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);

    private Dictionary<int, ConnectionProcess> ReadProcesses(IEnumerable<int> pids)
    {
        var names = ProcessNative.EnumerateProcesses().GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        var result = new Dictionary<int, ConnectionProcess>();
        foreach (int pid in pids)
        {
            if (pid == 0)
            {
                result[pid] = new ConnectionProcess(0, "System Idle Process", null, "Microsoft Corporation", null, true);
                continue;
            }

            if (!names.TryGetValue(pid, out var name))
            {
                continue; // Ended between the two reads.
            }

            var start = ProcessNative.TryGetStartTime(pid);
            result[pid] = _processes.GetOrAdd((pid, start), key =>
            {
                var (path, _) = pid == 4 ? (null, null) : ProcessNative.ReadIdentity(pid);
                bool windows = pid == 4 || ConnectionRules.IsWindowsProcess(name, path, WindowsDirectory);
                return new ConnectionProcess(pid, pid == 4 ? "System" : name, path, Publisher(path) ?? (windows ? "Microsoft Corporation" : null), key.Start, windows);
            });
        }

        // Forget programs that have ended.
        if (_processes.Count > 4096)
        {
            _processes.Clear();
        }

        return result;
    }

    private string? Publisher(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return _publishers.GetOrAdd(path, p =>
        {
            try
            {
                return File.Exists(p) ? FileVersionInfo.GetVersionInfo(p).CompanyName?.Trim() is { Length: > 0 } company ? company : null : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        });
    }

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The reverse DNS name of the address, or null. The system lookup cannot be stopped once it runs, so the wait for it
    /// is limited instead: after five seconds the answer is "no name" (and the cache remembers that), while the lookup
    /// finishes on its own in the background. Only cancellation by the caller throws.
    /// </summary>
    public async Task<string?> LookUpHostAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        Task<IPHostEntry> lookup;
        try
        {
            lookup = Dns.GetHostEntryAsync(address);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
        }

        try
        {
            var entry = await lookup.WaitAsync(LookupTimeout, cancellationToken).ConfigureAwait(false);
            return entry.HostName;
        }
        catch (TimeoutException)
        {
            Observe(lookup);
            return null;
        }
        catch (OperationCanceledException)
        {
            Observe(lookup);
            throw;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
        }
    }

    // A lookup left running after a timeout may still fail; read its error so it is not reported as unobserved.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public Task<OperationResult> CloseConnectionAsync(NetworkEndpoint endpoint, ConnectionProcess? process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (ConnectionRules.CloseRefusal(endpoint) is { } refusal)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        if (!CanCloseConnections)
        {
            return Task.FromResult(OperationResult.Fail("Closing a connection needs administrator rights."));
        }

        string program = ConnectionProcesses.NameOf(process, endpoint.Pid);
        string target = $"{program} (PID {endpoint.Pid}) {ConnectionText.Endpoint(endpoint.LocalAddress, endpoint.LocalPort, false)} → {ConnectionText.Endpoint(endpoint.RemoteAddress, endpoint.RemotePort, false)}";
        var options = new ChangeOptions(false, false, Module, target, false, "Nothing to undo: the program connects again by itself when it needs to.");
        return _guard.RunGuardedAsync($"Close connection of {target}", options, ct => Task.Run(() =>
        {
            var row = new MibTcpRow
            {
                State = MibTcpStateDeleteTcb,
                LocalAddress = EndpointTableParser.EncodeAddress(endpoint.LocalAddress),
                LocalPort = EndpointTableParser.EncodePort(endpoint.LocalPort),
                RemoteAddress = EndpointTableParser.EncodeAddress(endpoint.RemoteAddress!),
                RemotePort = EndpointTableParser.EncodePort(endpoint.RemotePort ?? 0),
            };
            uint status = SetTcpEntry(ref row);
            return status == 0
                ? OperationResult.Ok($"The connection of {program} was closed. The program may connect again.")
                : OperationResult.Fail($"Windows did not close the connection (error {status}). It may have closed already.");
        }, ct), null, cancellationToken);
    }

    public OperationResult OpenFileLocation(ConnectionProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(process.Path) || !File.Exists(process.Path))
        {
            return OperationResult.Fail($"The program file of {process.Name} is not known.");
        }

        return ShellLauncher.Open(Path.Combine(WindowsDirectory, "explorer.exe"), $"/select,\"{process.Path}\"");
    }

    public OperationResult SearchAddressOnline(IPAddress address) => ShellLauncher.Open(ConnectionText.SearchUrl(address));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public int State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetTcpEntry(ref MibTcpRow row);
}
