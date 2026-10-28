using System.Net;
using PcChecker.Core.Common;
using PcChecker.Core.Network.Tuning;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>
/// Demo DNS: the demo PC's Wi-Fi adapter gets its DNS from the router (192.168.1.1). The benchmark runs the real runner
/// and parser over <see cref="DemoDnsTransport"/>, so the numbers behave like a real run; switching changes only memory.
/// </summary>
public sealed class DemoDnsService : IDnsService
{
    private const string WifiId = "{6F1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B}";
    private const string Router = "192.168.1.1";

    private readonly object _gate = new();
    private string _manual4 = string.Empty;
    private string _manual6 = string.Empty;
    private bool _encrypted;
    private DnsPreviousSettings? _last;

    public DnsPreviousSettings? LastChange
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public async Task<DnsStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 300).ConfigureAwait(false);
        lock (_gate)
        {
            return new DnsStatus([Wifi()], EncryptionSupported: true, DateTimeOffset.Now);
        }
    }

    public Task<DnsBenchmarkReport> BenchmarkAsync(IReadOnlyList<DnsBenchmarkTarget> targets, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        new DnsBenchmarkRunner(new DemoDnsTransport(), new DnsBenchmarkOptions { Seed = 4070 }).RunAsync(targets, progress, cancellationToken);

    public async Task<OperationResult> SwitchAsync(DnsChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DnsSwitchPlan.Validate(request) != DnsSwitchProblem.None)
        {
            return OperationResult.Fail("The DNS change is not valid.");
        }

        await DemoData.DelayAsync(cancellationToken, 400, 700).ConfigureAwait(false);
        var servers = DnsSwitchPlan.Servers(request);
        lock (_gate)
        {
            _last = Capture(request.Label);
            _manual4 = string.Join(",", servers.Where(s => !s.Contains(':')));
            if (request.Servers6.Count > 0)
            {
                _manual6 = string.Join(",", servers.Where(s => s.Contains(':')));
            }

            _encrypted = DnsSwitchPlan.DohServers(request).Count > 0;
        }

        return OperationResult.Ok($"Demo mode: Wi-Fi would now use {request.Label} ({string.Join(", ", servers)}).");
    }

    public async Task<OperationResult> ResetToAutomaticAsync(IReadOnlyList<string> adapterIds, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 500).ConfigureAwait(false);
        lock (_gate)
        {
            _last = Capture("Automatic");
            _manual4 = _manual6 = string.Empty;
            _encrypted = false;
        }

        return OperationResult.Ok("Demo mode: Wi-Fi would now get its DNS servers from the router again.");
    }

    public async Task<OperationResult> UndoLastChangeAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 500).ConfigureAwait(false);
        lock (_gate)
        {
            if (_last?.Adapters.FirstOrDefault() is not { } previous)
            {
                return OperationResult.Fail("There is no DNS change to undo.");
            }

            _manual4 = previous.NameServer;
            _manual6 = previous.NameServer6 ?? _manual6;
            _encrypted = previous.DohFlags.Values.Any(v => v is > 0);
            _last = null;
        }

        return OperationResult.Ok("Demo mode: the previous DNS servers would now be back.");
    }

    private DnsAdapterInfo Wifi()
    {
        var v4 = DnsCommands.Split(_manual4);
        var v6 = DnsCommands.Split(_manual6);
        return new DnsAdapterInfo(WifiId, 12, DemoNetworkService.WifiAdapterName, "Intel(R) Wi-Fi 6E AX211 160MHz", true,
            v4.Count > 0 ? v4 : [Router], v6, _manual4, _manual6, [Router], Router,
            _encrypted ? DnsEncryptionState.On : DnsEncryptionState.Off);
    }

    private DnsPreviousSettings Capture(string label)
    {
        var adapter = DnsSettingsCapture.FromAdapter(Wifi());
        var flags = Wifi().AllServers.ToDictionary(s => s, s => _encrypted ? (long?)DohInterfaceSettings.AutomaticTemplate : null, StringComparer.OrdinalIgnoreCase);
        return new DnsPreviousSettings(DateTimeOffset.Now, label, [adapter with { DohFlags = flags }], []);
    }
}

/// <summary>
/// Simulated DNS servers for demo mode: each known address answers with a typical home-broadband time (the router
/// forwards to a slower provider DNS), made-up names get "does not exist", and Quad9's second server misses the odd lookup.
/// </summary>
public sealed class DemoDnsTransport : IDnsTransport
{
    private static readonly Dictionary<string, (double Cached, double Uncached, double Loss, bool Dnssec)> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["192.168.1.1"] = (23.8, 71.0, 0, false),
        ["1.1.1.1"] = (10.6, 31.5, 0, true),
        ["1.0.0.1"] = (11.2, 33.0, 0, true),
        ["8.8.8.8"] = (13.9, 38.2, 0, true),
        ["8.8.4.4"] = (14.6, 40.1, 0, true),
        ["9.9.9.9"] = (15.8, 44.7, 0, true),
        ["149.112.112.112"] = (17.1, 47.9, 0.03, true),
        ["208.67.222.222"] = (18.4, 52.3, 0, false),
        ["94.140.14.14"] = (26.2, 63.8, 0, true),
        ["1.1.1.3"] = (11.9, 34.6, 0, true),
        ["76.76.2.0"] = (19.3, 46.9, 0, true),
    };

    private readonly Random _random = DemoData.CreateRandom(53);
    private readonly object _gate = new();

    public async Task<DnsExchange> ExchangeAsync(string server, byte[] query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        var profile = Profiles.TryGetValue(server, out var p) ? p : (Cached: 34.0, Uncached: 88.0, Loss: 0.0, Dnssec: false);
        double roll;
        double jitter;
        lock (_gate)
        {
            roll = _random.NextDouble();
            jitter = Math.Exp((_random.NextDouble() - 0.5) * 0.35); // ±~18 %, skewed like real timings
        }

        if (roll < profile.Loss)
        {
            return new DnsExchange(DnsExchangeStatus.Timeout, null, timeout.TotalMilliseconds);
        }

        int offset = 12;
        DnsResponseParser.TryReadName(query, ref offset, out var name);
        bool uncached = name.StartsWith(DnsQueryNames.RandomPrefix, StringComparison.OrdinalIgnoreCase);
        ushort type = offset + 2 <= query.Length ? (ushort)((query[offset] << 8) | query[offset + 1]) : DnsRecordType.A;
        IReadOnlyList<IPAddress> answers = uncached ? [] : [type == DnsRecordType.Aaaa ? IPAddress.Parse("2606:4700::6810:84e5") : IPAddress.Parse("104.16.132.229")];
        var response = DnsResponseWriter.Answer(query, uncached ? DnsResponseCode.NameError : DnsResponseCode.NoError, answers,
            authenticatedData: profile.Dnssec && (query[3] & 0x20) != 0);
        return new DnsExchange(DnsExchangeStatus.Answered, response, (uncached ? profile.Uncached : profile.Cached) * jitter);
    }
}
