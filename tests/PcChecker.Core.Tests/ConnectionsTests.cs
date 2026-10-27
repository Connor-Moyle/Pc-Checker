using System.Buffers.Binary;
using System.Net;
using PcChecker.Core.Connections;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Connections page: the raw TCP/UDP table layouts, port byte order, grouping, filters, exposure rule and host names.</summary>
public class ConnectionsTests
{
    private static readonly IReadOnlyDictionary<int, ConnectionProcess> Processes = new Dictionary<int, ConnectionProcess>
    {
        [4] = new(4, "System", null, "Microsoft Corporation", null, true),
        [1012] = new(1012, "svchost.exe", @"C:\Windows\System32\svchost.exe", "Microsoft Corporation", null, true),
        [9412] = new(9412, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", null, false),
        [9876] = new(9876, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", null, false),
        [11840] = new(11840, "java.exe", @"C:\Program Files\Java\bin\java.exe", "Oracle", null, false),
    };

    /// <summary>Builds a table: a DWORD row count, then the rows.</summary>
    private static byte[] Table(int rowSize, params byte[][] rows)
    {
        var table = new byte[4 + (rowSize * rows.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rowSize, rows[i].Length);
            rows[i].CopyTo(table, 4 + (i * rowSize));
        }

        return table;
    }

    private static void Dword(byte[] row, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(offset), value);

    /// <summary>A port as Windows stores it: network byte order in the first two bytes of the DWORD.</summary>
    private static void PortAt(byte[] row, int offset, int port)
    {
        row[offset] = (byte)(port >> 8);
        row[offset + 1] = (byte)(port & 0xFF);
    }

    [Fact]
    public void Reads_the_ipv4_tcp_table_layout()
    {
        // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid.
        var connected = new byte[24];
        Dword(connected, 0, 5);
        new byte[] { 192, 168, 1, 23 }.CopyTo(connected, 4);
        PortAt(connected, 8, 52114);
        new byte[] { 142, 250, 185, 78 }.CopyTo(connected, 12);
        PortAt(connected, 16, 443);
        Dword(connected, 20, 9412);

        var listening = new byte[24];
        Dword(listening, 0, 2);
        PortAt(listening, 8, 25565);
        Dword(listening, 20, 11840);

        var rows = EndpointTableParser.ParseTcp4(Table(EndpointTableParser.Tcp4RowSize, connected, listening));

        Assert.Equal(2, rows.Count);
        Assert.Equal(ConnectionProtocol.Tcp, rows[0].Protocol);
        Assert.Equal(IPAddress.Parse("192.168.1.23"), rows[0].LocalAddress);
        Assert.Equal(52114, rows[0].LocalPort);
        Assert.Equal(IPAddress.Parse("142.250.185.78"), rows[0].RemoteAddress);
        Assert.Equal(443, rows[0].RemotePort);
        Assert.Equal(TcpState.Established, rows[0].State);
        Assert.Equal(9412, rows[0].Pid);
        Assert.Equal(IPAddress.Any, rows[1].LocalAddress);
        Assert.Equal(TcpState.Listen, rows[1].State);
        Assert.Equal(25565, rows[1].LocalPort);
    }

    [Fact]
    public void Reads_the_ipv6_tcp_table_layout_with_link_local_scope()
    {
        // MIB_TCP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId,
        // dwRemotePort, dwState, dwOwningPid = 56 bytes.
        var row = new byte[56];
        IPAddress.Parse("fe80::1c2b:3d4e:5f60:7182").GetAddressBytes().CopyTo(row, 0);
        Dword(row, 16, 12);
        PortAt(row, 20, 445);
        IPAddress.Parse("2a00:1450:4001:82a::200e").GetAddressBytes().CopyTo(row, 24);
        Dword(row, 40, 0);
        PortAt(row, 44, 443);
        Dword(row, 48, 11);
        Dword(row, 52, 4);

        var e = Assert.Single(EndpointTableParser.ParseTcp6(Table(EndpointTableParser.Tcp6RowSize, row)));

        Assert.True(e.IsIPv6);
        Assert.Equal(12, e.LocalAddress.ScopeId);
        Assert.Equal(445, e.LocalPort);
        Assert.Equal(IPAddress.Parse("2a00:1450:4001:82a::200e"), e.RemoteAddress);
        Assert.Equal(443, e.RemotePort);
        Assert.Equal(TcpState.TimeWait, e.State);
        Assert.Equal(4, e.Pid);
        Assert.Equal("Recently closed", ConnectionText.State(e));
        Assert.Equal("[2a00:1450:4001:82a::200e]:443", ConnectionText.Remote(e));
    }

    [Fact]
    public void Reads_the_udp_table_layouts()
    {
        // MIB_UDPROW_OWNER_PID: dwLocalAddr, dwLocalPort, dwOwningPid.
        var udp4 = new byte[12];
        new byte[] { 0, 0, 0, 0 }.CopyTo(udp4, 0);
        PortAt(udp4, 4, 5353);
        Dword(udp4, 8, 9412);

        // MIB_UDP6ROW_OWNER_PID: ucLocalAddr[16], dwLocalScopeId, dwLocalPort, dwOwningPid.
        var udp6 = new byte[28];
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(udp6, 0);
        PortAt(udp6, 20, 50001);
        Dword(udp6, 24, 1012);

        var v4 = Assert.Single(EndpointTableParser.ParseUdp4(Table(EndpointTableParser.Udp4RowSize, udp4)));
        var v6 = Assert.Single(EndpointTableParser.ParseUdp6(Table(EndpointTableParser.Udp6RowSize, udp6)));

        Assert.Equal(ConnectionProtocol.Udp, v4.Protocol);
        Assert.Equal(5353, v4.LocalPort);
        Assert.Equal(9412, v4.Pid);
        Assert.Null(v4.RemoteAddress);
        Assert.Equal(IPAddress.IPv6Loopback, v6.LocalAddress);
        Assert.Equal(50001, v6.LocalPort);
        Assert.Equal(1012, v6.Pid);
        Assert.Equal("UDPv6", ConnectionText.Protocol(v6));
        Assert.Equal(string.Empty, ConnectionText.Remote(v4));
    }

    [Fact]
    public void A_table_that_claims_more_rows_than_it_holds_keeps_the_complete_ones()
    {
        var row = new byte[12];
        PortAt(row, 4, 53);
        var table = Table(EndpointTableParser.Udp4RowSize, row);
        BinaryPrimitives.WriteUInt32LittleEndian(table, 5);

        Assert.Single(EndpointTableParser.ParseUdp4(table));
        Assert.Empty(EndpointTableParser.ParseTcp4(new byte[2]));
    }

    [Theory]
    [InlineData(443)]
    [InlineData(80)]
    [InlineData(8080)]
    [InlineData(65535)]
    [InlineData(1)]
    public void Ports_round_trip_through_network_byte_order(int port)
    {
        var dword = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dword, EndpointTableParser.EncodePort(port));

        Assert.Equal((byte)(port >> 8), dword[0]);
        Assert.Equal((byte)(port & 0xFF), dword[1]);
        Assert.Equal(port, EndpointTableParser.Port(dword));
    }

    [Fact]
    public void Ipv4_addresses_encode_in_network_order()
    {
        var value = EndpointTableParser.EncodeAddress(IPAddress.Parse("192.168.1.23"));
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.Equal(new byte[] { 192, 168, 1, 23 }, bytes);
        Assert.Throws<ArgumentException>(() => EndpointTableParser.EncodeAddress(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Listening_on_every_network_on_an_unusual_port_accepts_connections_from_other_devices()
    {
        Assert.True(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.Any, 25565, 11840), windowsProcess: false));
        Assert.True(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.IPv6Any, 8080, 11840), windowsProcess: false));
        Assert.False(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.Loopback, 6742, 11840), windowsProcess: false));
        Assert.False(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.Parse("192.168.1.23"), 139, 4), windowsProcess: true));
        Assert.False(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.Any, 445, 4), windowsProcess: true));
        Assert.False(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.Any, 49664, 1012), windowsProcess: true));
        Assert.True(ConnectionRules.AcceptsFromOtherDevices(Listen(IPAddress.Any, 49664, 11840), windowsProcess: false));
        Assert.False(ConnectionRules.AcceptsFromOtherDevices(Udp(IPAddress.Any, 27036, 11840), windowsProcess: false));
        Assert.False(ConnectionRules.AcceptsFromOtherDevices(Connected(25565, "192.168.1.42", 51022, 11840), windowsProcess: false));
        Assert.True(ConnectionRules.IsWindowsProcess("svchost.exe", null));
        Assert.True(ConnectionRules.IsWindowsProcess("helper.exe", @"C:\WINDOWS\system32\helper.exe"));
        Assert.False(ConnectionRules.IsWindowsProcess("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }

    [Fact]
    public void Groups_by_program_with_counts_and_the_busiest_first()
    {
        var endpoints = new[]
        {
            Connected(52114, "142.250.185.78", 443, 9412),
            Connected(52115, "142.250.185.78", 443, 9876),
            Connected(52116, "140.82.112.25", 443, 9876),
            Listen(IPAddress.Any, 25565, 11840),
            Listen(IPAddress.Any, 135, 1012),
            Udp(IPAddress.Any, 5353, 1012),
            Udp(IPAddress.Any, 5355, 1012),
            Connected(50000, "10.0.0.5", 80, 31337),
        };

        var groups = ConnectionGrouping.Group(endpoints, Processes);

        Assert.Equal(["chrome.exe", "svchost.exe", "java.exe", "Process 31337"], groups.Select(g => g.Name));
        var chrome = groups[0];
        Assert.Equal(3, chrome.Count);
        Assert.Equal([9412, 9876], chrome.Pids);
        Assert.Equal(3, chrome.Established);
        Assert.Equal("Google LLC", chrome.Publisher);
        Assert.False(groups[1].AcceptsFromOtherDevices);
        Assert.True(groups[1].IsWindows);
        Assert.True(groups[2].AcceptsFromOtherDevices);
        Assert.Equal(1, groups[2].Listening);
    }

    [Fact]
    public void Filters_by_state_loopback_and_search()
    {
        var endpoints = new[]
        {
            Connected(52114, "142.250.185.78", 443, 9412),
            Listen(IPAddress.Any, 25565, 11840),
            Listen(IPAddress.Loopback, 6742, 11840),
            new NetworkEndpoint(ConnectionProtocol.Tcp, IPAddress.Loopback, 52190, IPAddress.Loopback, 6742, TcpState.Established, 9412),
            Udp(IPAddress.Any, 5353, 1012),
        };

        Assert.Equal(2, ConnectionGrouping.Filter(endpoints, Processes, ConnectionFilterKind.Listening, false, null).Count);
        Assert.Single(ConnectionGrouping.Filter(endpoints, Processes, ConnectionFilterKind.Listening, true, null));
        Assert.Equal(2, ConnectionGrouping.Filter(endpoints, Processes, ConnectionFilterKind.Established, false, null).Count);
        Assert.Equal(3, ConnectionGrouping.Filter(endpoints, Processes, ConnectionFilterKind.All, true, null).Count);
        Assert.Single(ConnectionGrouping.Filter(endpoints, Processes, ConnectionFilterKind.All, true, "chrome 443"));
        Assert.Single(ConnectionGrouping.Filter(endpoints, Processes, ConnectionFilterKind.All, true, "1e100",
            e => e.RemoteAddress?.ToString() == "142.250.185.78" ? "fra16s52-in-f14.1e100.net" : null));
        Assert.Equal(0, ConnectionGrouping.Rank(endpoints[1], false));
        Assert.Equal(1, ConnectionGrouping.Rank(endpoints[0], false));
    }

    [Fact]
    public void Only_ipv4_tcp_connections_can_be_closed()
    {
        Assert.Null(ConnectionRules.CloseRefusal(Connected(52114, "142.250.185.78", 443, 9412)));
        Assert.NotNull(ConnectionRules.CloseRefusal(Udp(IPAddress.Any, 5353, 1012)));
        Assert.NotNull(ConnectionRules.CloseRefusal(Listen(IPAddress.Any, 25565, 11840)));
        Assert.NotNull(ConnectionRules.CloseRefusal(new NetworkEndpoint(ConnectionProtocol.Tcp, IPAddress.IPv6Loopback, 1, IPAddress.IPv6Loopback, 2, TcpState.Established, 1)));
    }

    [Fact]
    public void Addresses_read_in_plain_words()
    {
        Assert.Equal("Any:3389", ConnectionText.Endpoint(IPAddress.Any, 3389));
        Assert.Equal("Any:445", ConnectionText.Endpoint(IPAddress.IPv6Any, 445));
        Assert.Equal("This PC:6742", ConnectionText.Endpoint(IPAddress.Loopback, 6742));
        Assert.Equal("127.0.0.1:6742", ConnectionText.Endpoint(IPAddress.Loopback, 6742, friendly: false));
        Assert.Equal("[::1]:6742", ConnectionText.Endpoint(IPAddress.IPv6Loopback, 6742, friendly: false));
        Assert.Equal("192.168.1.5:*", ConnectionText.Endpoint(IPAddress.Parse("192.168.1.5"), 0));
        Assert.Equal("10.0.0.1", ConnectionText.Address(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.True(ConnectionRules.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.Equal("Listening", ConnectionText.State(Listen(IPAddress.Any, 1, 1)));
        Assert.Equal("Connected", ConnectionText.State(Connected(1, "1.1.1.1", 443, 1)));
        Assert.Equal("chrome.exe\tPID 9412\tTCP\t192.168.1.23:52114\t142.250.185.78:443\tESTABLISHED\tfra16s52-in-f14.1e100.net",
            ConnectionText.CopyLine(Connected(52114, "142.250.185.78", 443, 9412), "chrome.exe", "fra16s52-in-f14.1e100.net"));
        Assert.Equal("System", ConnectionProcesses.NameOf(null, 4));
    }

    [Fact]
    public async Task Host_names_are_looked_up_once_and_cached()
    {
        int calls = 0;
        var resolved = new TaskCompletionSource();
        var cache = new HostNameCache((address, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(address.ToString() == "140.82.112.25" ? "lb-140-82-112-25-iad.github.com." : null);
        });
        cache.Resolved += (_, _) => resolved.TrySetResult();

        var github = IPAddress.Parse("140.82.112.25");
        Assert.Null(cache.Get(github, startLookup: true));
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lb-140-82-112-25-iad.github.com", cache.Get(github, startLookup: true));

        Assert.Null(cache.Get(IPAddress.Loopback, startLookup: true));
        Assert.Null(cache.Get(IPAddress.Any, startLookup: true));
        Assert.Equal(1, calls);
        Assert.Null(HostNameCache.Clean("140.82.112.25", github));
        Assert.Null(HostNameCache.Clean("  ", github));
    }

    private static NetworkEndpoint Listen(IPAddress address, int port, int pid) =>
        new(ConnectionProtocol.Tcp, address, port, IPAddress.Any, 0, TcpState.Listen, pid);

    private static NetworkEndpoint Connected(int localPort, string remote, int remotePort, int pid) =>
        new(ConnectionProtocol.Tcp, IPAddress.Parse("192.168.1.23"), localPort, IPAddress.Parse(remote), remotePort, TcpState.Established, pid);

    private static NetworkEndpoint Udp(IPAddress address, int port, int pid) =>
        new(ConnectionProtocol.Udp, address, port, null, null, TcpState.Unknown, pid);
}
