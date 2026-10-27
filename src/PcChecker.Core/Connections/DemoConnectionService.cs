using System.Net;
using PcChecker.Core.Common;

namespace PcChecker.Core.Connections;

/// <summary>
/// A believable connection list for demo mode and screenshots: a browser with many connections, Steam, Discord and
/// Spotify, Windows' own listeners, a local Minecraft server and Steam's local transfer port that accept connections
/// from other devices, and loopback-only helpers. Connections come and go a little between reads. Closing a
/// connection only changes the in-memory list.
/// </summary>
public sealed class DemoConnectionService : IConnectionService
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private static readonly Dictionary<string, string> HostNames = new()
    {
        ["142.250.185.78"] = "fra16s52-in-f14.1e100.net",
        ["142.250.186.67"] = "fra24s06-in-f3.1e100.net",
        ["140.82.112.25"] = "lb-140-82-112-25-iad.github.com",
        ["35.186.224.25"] = "25.224.186.35.bc.googleusercontent.com",
        ["23.48.172.97"] = "a23-48-172-97.deploy.static.akamaitechnologies.com",
        ["52.113.194.132"] = "ecs.office.trafficmanager.net",
        ["192.168.1.1"] = "router.home",
        ["192.168.1.42"] = "laptop-anna.home",
        ["13.107.42.14"] = "onedrive.live.com",
    };

    private readonly object _gate = new();
    private readonly HashSet<string> _closed = [];
    private readonly Random _random = DemoData.CreateRandom(4071);
    private int _tick;

    public bool CanCloseConnections => true;

    public async Task<ConnectionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 40, 120).ConfigureAwait(false);
        lock (_gate)
        {
            _tick++;
            var endpoints = Build(_tick, _random).Where(e => !_closed.Contains(e.Key)).ToList();
            return new ConnectionSnapshot(endpoints, Processes, DateTimeOffset.Now, null);
        }
    }

    public async Task<string?> LookUpHostAsync(IPAddress address, CancellationToken cancellationToken)
    {
        await Task.Delay(Random.Shared.Next(80, 400), cancellationToken).ConfigureAwait(false);
        return HostNames.TryGetValue(address.ToString(), out var name) ? name : null;
    }

    public async Task<OperationResult> CloseConnectionAsync(NetworkEndpoint endpoint, ConnectionProcess? process, CancellationToken cancellationToken)
    {
        if (ConnectionRules.CloseRefusal(endpoint) is { } refusal)
        {
            return OperationResult.Fail(refusal);
        }

        await DemoData.DelayAsync(cancellationToken, 60, 200).ConfigureAwait(false);
        lock (_gate)
        {
            _closed.Add(endpoint.Key);
        }

        return OperationResult.Ok($"The connection of {ConnectionProcesses.NameOf(process, endpoint.Pid)} was closed. The program may connect again.");
    }

    public OperationResult OpenFileLocation(ConnectionProcess process) =>
        OperationResult.Ok($"Demo mode: File Explorer would open at {process.Path ?? process.Name}.");

    public OperationResult SearchAddressOnline(IPAddress address) => OperationResult.Ok($"Demo mode: a web search would open for {address}.");

    private static readonly DateTimeOffset Boot = DateTimeOffset.Now.AddHours(-3);

    private static readonly IReadOnlyDictionary<int, ConnectionProcess> Processes = new Dictionary<int, ConnectionProcess>
    {
        [4] = new(4, "System", null, "Microsoft Corporation", Boot, true),
        [1012] = new(1012, "svchost.exe", @"C:\Windows\System32\svchost.exe", "Microsoft Corporation", Boot, true),
        [1388] = new(1388, "svchost.exe", @"C:\Windows\System32\svchost.exe", "Microsoft Corporation", Boot, true),
        [868] = new(868, "lsass.exe", @"C:\Windows\System32\lsass.exe", "Microsoft Corporation", Boot, true),
        [744] = new(744, "wininit.exe", @"C:\Windows\System32\wininit.exe", "Microsoft Corporation", Boot, true),
        [2864] = new(2864, "spoolsv.exe", @"C:\Windows\System32\spoolsv.exe", "Microsoft Corporation", Boot, true),
        [9412] = new(9412, "chrome.exe", Chrome, "Google LLC", Boot.AddMinutes(12), false),
        [9876] = new(9876, "chrome.exe", Chrome, "Google LLC", Boot.AddMinutes(12), false),
        [10224] = new(10224, "chrome.exe", Chrome, "Google LLC", Boot.AddMinutes(13), false),
        [6120] = new(6120, "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", "Valve", Boot.AddMinutes(1), false),
        [7344] = new(7344, "Discord.exe", @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9163\Discord.exe", "Discord Inc.", Boot.AddMinutes(1), false),
        [8012] = new(8012, "Spotify.exe", @"C:\Users\Alex\AppData\Roaming\Spotify\Spotify.exe", "Spotify AB", Boot.AddMinutes(2), false),
        [5560] = new(5560, "OneDrive.exe", @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe", "Microsoft Corporation", Boot.AddMinutes(1), false),
        [11840] = new(11840, "java.exe", @"C:\Program Files\Eclipse Adoptium\jdk-21.0.4.7-hotspot\bin\java.exe", "Eclipse Adoptium", Boot.AddMinutes(40), false),
        [4420] = new(4420, "NVIDIA Web Helper.exe", @"C:\Program Files (x86)\NVIDIA Corporation\NvNode\NVIDIA Web Helper.exe", "NVIDIA Corporation", Boot.AddMinutes(1), false),
        [3988] = new(3988, "OpenRGB.exe", @"C:\Program Files\OpenRGB\OpenRGB.exe", "OpenRGB", Boot.AddMinutes(1), false),
    };

    private static List<NetworkEndpoint> Build(int tick, Random random)
    {
        var any4 = IPAddress.Any;
        var any6 = IPAddress.IPv6Any;
        var lan = IPAddress.Parse("192.168.1.23");
        var loop = IPAddress.Loopback;
        NetworkEndpoint Listen(IPAddress address, int port, int pid) => new(ConnectionProtocol.Tcp, address, port, address.AddressFamily == any6.AddressFamily ? any6 : any4, 0, TcpState.Listen, pid);
        NetworkEndpoint Tcp(int localPort, string remote, int remotePort, TcpState state, int pid, IPAddress? local = null) =>
            new(ConnectionProtocol.Tcp, local ?? lan, localPort, IPAddress.Parse(remote), remotePort, state, pid);
        NetworkEndpoint Udp(IPAddress address, int port, int pid) => new(ConnectionProtocol.Udp, address, port, null, null, TcpState.Unknown, pid);

        var list = new List<NetworkEndpoint>
        {
            // Windows' own listeners: expected, not highlighted.
            Listen(any4, 135, 1012), Listen(any6, 135, 1012), Listen(any4, 445, 4), Listen(any6, 445, 4), Listen(any4, 5040, 1388),
            Listen(any4, 49664, 868), Listen(any4, 49665, 744), Listen(any4, 49667, 2864),
            Listen(lan, 139, 4),

            // Programs that accept connections from other devices.
            Listen(any4, 25565, 11840),
            Listen(any4, 27036, 6120),
            Listen(any4, 57621, 8012),

            // Loopback-only helpers.
            Listen(loop, 6742, 3988),
            Listen(loop, 9100, 4420),
            Listen(loop, 27060, 6120),

            // Connected.
            Tcp(52114, "142.250.185.78", 443, TcpState.Established, 9412),
            Tcp(52120, "142.250.186.67", 443, TcpState.Established, 9412),
            Tcp(52131, "140.82.112.25", 443, TcpState.Established, 9876),
            Tcp(52140, "23.48.172.97", 443, TcpState.Established, 10224),
            Tcp(52155, "162.159.135.234", 443, TcpState.Established, 7344),
            Tcp(52158, "162.159.136.232", 443, TcpState.Established, 7344),
            Tcp(52160, "35.186.224.25", 443, TcpState.Established, 8012),
            Tcp(52170, "52.113.194.132", 443, TcpState.Established, 5560),
            Tcp(52171, "13.107.42.14", 443, TcpState.Established, 5560),
            Tcp(52180, "162.254.196.67", 27019, TcpState.Established, 6120),
            Tcp(52181, "155.133.248.39", 27024, TcpState.Established, 6120),
            Tcp(25565, "192.168.1.42", 51022, TcpState.Established, 11840),
            Tcp(52190, "127.0.0.1", 6742, TcpState.Established, 4420, loop),
            Tcp(6742, "127.0.0.1", 52190, TcpState.Established, 3988, loop),

            // UDP.
            Udp(any4, 5353, 9412), Udp(any4, 5353, 1388), Udp(any4, 5355, 1388), Udp(any6, 5353, 9412),
            Udp(any4, 27036, 6120), Udp(any4, 57621, 8012), Udp(any4, 50001, 7344), Udp(lan, 1900, 1388),
        };

        // A few short-lived browser connections that come and go, so the live list visibly moves.
        for (int i = 0; i < 3; i++)
        {
            if (random.Next(3) > 0)
            {
                int port = 52200 + ((tick + i) % 40);
                list.Add(Tcp(port, "142.250.185.78", 443, i == 0 ? TcpState.TimeWait : TcpState.Established, 9412));
            }
        }

        return list;
    }
}
