using System.Buffers.Binary;
using System.Net;

namespace PcChecker.Core.Connections;

/// <summary>
/// Reads the tables GetExtendedTcpTable and GetExtendedUdpTable fill (the *_OWNER_PID classes) from their raw bytes:
/// a DWORD row count followed by fixed-size rows. Addresses are stored in network byte order, ports in the low 16 bits
/// of a DWORD in network byte order, everything else little-endian. Pure, so the layouts are unit-tested.
/// </summary>
public static class EndpointTableParser
{
    /// <summary>MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, PID.</summary>
    public const int Tcp4RowSize = 24;

    /// <summary>MIB_TCP6ROW_OWNER_PID: local address[16], scope, port, remote address[16], scope, port, state, PID.</summary>
    public const int Tcp6RowSize = 56;

    /// <summary>MIB_UDPROW_OWNER_PID: local address, local port, PID.</summary>
    public const int Udp4RowSize = 12;

    /// <summary>MIB_UDP6ROW_OWNER_PID: local address[16], scope, local port, PID.</summary>
    public const int Udp6RowSize = 28;

    public static IReadOnlyList<NetworkEndpoint> ParseTcp4(ReadOnlySpan<byte> table) =>
        Parse(table, Tcp4RowSize, row => new NetworkEndpoint(
            ConnectionProtocol.Tcp,
            new IPAddress(row.Slice(4, 4)),
            Port(row.Slice(8, 4)),
            new IPAddress(row.Slice(12, 4)),
            Port(row.Slice(16, 4)),
            State(BinaryPrimitives.ReadInt32LittleEndian(row)),
            BinaryPrimitives.ReadInt32LittleEndian(row.Slice(20, 4))));

    public static IReadOnlyList<NetworkEndpoint> ParseTcp6(ReadOnlySpan<byte> table) =>
        Parse(table, Tcp6RowSize, row => new NetworkEndpoint(
            ConnectionProtocol.Tcp,
            V6(row.Slice(0, 16), BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4))),
            Port(row.Slice(20, 4)),
            V6(row.Slice(24, 16), BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(40, 4))),
            Port(row.Slice(44, 4)),
            State(BinaryPrimitives.ReadInt32LittleEndian(row.Slice(48, 4))),
            BinaryPrimitives.ReadInt32LittleEndian(row.Slice(52, 4))));

    public static IReadOnlyList<NetworkEndpoint> ParseUdp4(ReadOnlySpan<byte> table) =>
        Parse(table, Udp4RowSize, row => new NetworkEndpoint(
            ConnectionProtocol.Udp,
            new IPAddress(row.Slice(0, 4)),
            Port(row.Slice(4, 4)),
            null,
            null,
            TcpState.Unknown,
            BinaryPrimitives.ReadInt32LittleEndian(row.Slice(8, 4))));

    public static IReadOnlyList<NetworkEndpoint> ParseUdp6(ReadOnlySpan<byte> table) =>
        Parse(table, Udp6RowSize, row => new NetworkEndpoint(
            ConnectionProtocol.Udp,
            V6(row.Slice(0, 16), BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4))),
            Port(row.Slice(20, 4)),
            null,
            null,
            TcpState.Unknown,
            BinaryPrimitives.ReadInt32LittleEndian(row.Slice(24, 4))));

    /// <summary>A port from its DWORD: the first two bytes in network (big-endian) order; the upper bytes are unused.</summary>
    public static int Port(ReadOnlySpan<byte> dword) => (dword[0] << 8) | dword[1];

    /// <summary>The DWORD value Windows expects for a port (inverse of <see cref="Port"/> on a little-endian PC).</summary>
    public static uint EncodePort(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    /// <summary>The DWORD value Windows expects for an IPv4 address (its bytes in network order, read little-endian).</summary>
    public static uint EncodeAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            throw new ArgumentException("Only IPv4 addresses fit in a MIB_TCPROW.", nameof(address));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private delegate NetworkEndpoint RowReader(ReadOnlySpan<byte> row);

    private static List<NetworkEndpoint> Parse(ReadOnlySpan<byte> table, int rowSize, RowReader read)
    {
        var result = new List<NetworkEndpoint>();
        if (table.Length < 4)
        {
            return result;
        }

        long count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (long i = 0; i < count; i++)
        {
            long offset = 4 + (i * rowSize);
            if (offset + rowSize > table.Length)
            {
                break; // A table that says it has more rows than fit: keep what is there.
            }

            result.Add(read(table.Slice((int)offset, rowSize)));
        }

        return result;
    }

    private static IPAddress V6(ReadOnlySpan<byte> bytes, uint scope) =>
        new(bytes, IsLinkLocal(bytes) ? scope : 0);

    private static bool IsLinkLocal(ReadOnlySpan<byte> bytes) => bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;

    private static TcpState State(int value) => value is >= 1 and <= 12 ? (TcpState)value : TcpState.Unknown;
}
