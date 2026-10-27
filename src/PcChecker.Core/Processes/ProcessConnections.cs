using System.Runtime.InteropServices;

namespace PcChecker.Core.Processes;

/// <summary>
/// Open network connections per process from the TCP and UDP tables (GetExtendedTcpTable / GetExtendedUdpTable, IPv4
/// and IPv6): TCP connections that are not just listening, plus UDP endpoints. Windows does not report how much data
/// each program sends without a trace session, so the Network column counts connections instead.
/// </summary>
internal static class ProcessConnections
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const int TcpStateListen = 2;
    private const int ErrorInsufficientBuffer = 122;

    // Row sizes and field offsets of MIB_TCPROW_OWNER_PID, MIB_TCP6ROW_OWNER_PID, MIB_UDPROW_OWNER_PID, MIB_UDP6ROW_OWNER_PID.
    private const int Tcp4RowSize = 24;
    private const int Tcp4StateOffset = 0;
    private const int Tcp4PidOffset = 20;
    private const int Tcp6RowSize = 56;
    private const int Tcp6StateOffset = 48;
    private const int Tcp6PidOffset = 52;
    private const int Udp4RowSize = 12;
    private const int Udp4PidOffset = 8;
    private const int Udp6RowSize = 28;
    private const int Udp6PidOffset = 24;

    /// <summary>Connections per process id, or null when no table could be read.</summary>
    public static IReadOnlyDictionary<int, int>? Count()
    {
        var counts = new Dictionary<int, int>();
        bool any = false;
        any |= Read(counts, tcp: true, AfInet, Tcp4RowSize, Tcp4PidOffset, Tcp4StateOffset);
        any |= Read(counts, tcp: true, AfInet6, Tcp6RowSize, Tcp6PidOffset, Tcp6StateOffset);
        any |= Read(counts, tcp: false, AfInet, Udp4RowSize, Udp4PidOffset, -1);
        any |= Read(counts, tcp: false, AfInet6, Udp6RowSize, Udp6PidOffset, -1);
        return any ? counts : null;
    }

    private static bool Read(Dictionary<int, int> counts, bool tcp, int family, int rowSize, int pidOffset, int stateOffset)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint status = tcp
                ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpTableOwnerPid, 0);
            for (int attempt = 0; attempt < 4 && status == ErrorInsufficientBuffer; attempt++)
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
                status = tcp
                    ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
                    : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);
            }

            if (status != 0 || buffer == IntPtr.Zero)
            {
                return false;
            }

            int rows = Marshal.ReadInt32(buffer);
            for (int i = 0; i < rows; i++)
            {
                int offset = 4 + (i * rowSize);
                if (offset + rowSize > size)
                {
                    break;
                }

                if (stateOffset >= 0 && Marshal.ReadInt32(buffer, offset + stateOffset) == TcpStateListen)
                {
                    continue;
                }

                int pid = Marshal.ReadInt32(buffer, offset + pidOffset);
                counts[pid] = counts.GetValueOrDefault(pid) + 1;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
