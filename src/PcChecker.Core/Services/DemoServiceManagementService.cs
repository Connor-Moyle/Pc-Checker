using PcChecker.Core.Common;

namespace PcChecker.Core.Services;

/// <summary>
/// A believable set of Windows services for demo mode and screenshots: core Windows services, a few optional ones,
/// and two third-party services (one of them a known start-up hog) so the filters and the "safe to set to Manual"
/// note have something to show. Actions change only the in-memory list.
/// </summary>
public sealed class DemoServiceManagementService : IServiceManagementService
{
    private const string LocalSystem = "LocalSystem";
    private const string LocalService = "NT AUTHORITY\\LocalService";
    private const string NetworkService = "NT AUTHORITY\\NetworkService";

    private readonly object _gate = new();
    private readonly List<ServiceDetails> _services;
    private readonly Dictionary<string, List<string>> _dependsOn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Winmgmt"] = ["RPCSS"],
        ["RPCSS"] = ["RpcEptMapper", "DcomLaunch"],
        ["Dnscache"] = ["nsi", "NlaSvc"],
        ["Spooler"] = ["RPCSS", "http"],
        ["Audiosrv"] = ["AudioEndpointBuilder", "RpcSs"],
        ["LanmanWorkstation"] = ["bowser", "mrxsmb"],
    };

    public DemoServiceManagementService()
    {
        _services = Build();
    }

    public async Task<IReadOnlyList<ServiceDetails>> GetServicesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 120, 320).ConfigureAwait(false);
        lock (_gate)
        {
            return _services.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public async Task<ServiceDependencies> GetDependenciesAsync(string serviceName, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 60, 160).ConfigureAwait(false);
        var dependsOn = _dependsOn.TryGetValue(serviceName, out var list) ? list.ToList() : new List<string>();
        var requiredBy = _dependsOn.Where(kv => kv.Value.Contains(serviceName, StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        return new ServiceDependencies(serviceName, dependsOn, requiredBy);
    }

    public Task<OperationResult> StartAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        SetState(service, ServiceRunState.Running, $"{service.DisplayName} is now running.");

    public Task<OperationResult> StopAsync(ServiceDetails service, CancellationToken cancellationToken)
    {
        var (allowed, reason) = ServiceSafetyRules.CanStop(service);
        return allowed
            ? SetState(service, ServiceRunState.Stopped, $"{service.DisplayName} is now stopped.")
            : Task.FromResult(OperationResult.Fail(reason ?? "This service cannot be stopped."));
    }

    public Task<OperationResult> RestartAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        SetState(service, ServiceRunState.Running, $"{service.DisplayName} was restarted.");

    public Task<OperationResult> PauseAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        SetState(service, ServiceRunState.Paused, $"{service.DisplayName} is paused.");

    public Task<OperationResult> ContinueAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        SetState(service, ServiceRunState.Running, $"{service.DisplayName} has resumed.");

    public Task<OperationResult> SetStartupTypeAsync(ServiceDetails service, ServiceStartupType startupType, CancellationToken cancellationToken)
    {
        var (allowed, reason) = ServiceSafetyRules.CanSetStartup(service.Name, startupType);
        if (!allowed)
        {
            return Task.FromResult(OperationResult.Fail(reason ?? "This start type cannot be set."));
        }

        lock (_gate)
        {
            Replace(service.Name, s => s with { StartupType = startupType });
        }

        return Task.FromResult(OperationResult.Ok($"{service.DisplayName} is now set to {ServiceStartupMapping.Label(startupType)}."));
    }

    public OperationResult OpenServicesConsole() => OperationResult.Ok("Opened the Services console (demo).");

    public OperationResult OpenFileLocation(ServiceDetails service) => OperationResult.Ok("Opened the file location (demo).");

    public OperationResult SearchOnline(ServiceDetails service) => OperationResult.Ok("Opened a web search (demo).");

    private async Task<OperationResult> SetState(ServiceDetails service, ServiceRunState state, string message)
    {
        await DemoData.DelayAsync(CancellationToken.None, 150, 400).ConfigureAwait(false);
        lock (_gate)
        {
            Replace(service.Name, s => s with
            {
                State = state,
                Pid = state == ServiceRunState.Stopped ? null : s.Pid ?? 5000 + s.Name.Length,
            });
        }

        return OperationResult.Ok(message);
    }

    private void Replace(string name, Func<ServiceDetails, ServiceDetails> update)
    {
        int i = _services.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            _services[i] = update(_services[i]);
        }
    }

    private static List<ServiceDetails> Build() =>
    [
        Win("Winmgmt", "Windows Management Instrumentation", "Provides a common interface to manage and read the PC's hardware and software.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalSystem, 1480, "COM Infrastructure", canStop: false),
        Win("RpcSs", "Remote Procedure Call (RPC)", "The backbone that lets Windows components talk to each other.",
            ServiceRunState.Running, ServiceStartupType.Automatic, NetworkService, 980, "COM Infrastructure", canStop: false),
        Win("Dnscache", "DNS Client", "Looks up and remembers website names so pages load faster.",
            ServiceRunState.Running, ServiceStartupType.Automatic, NetworkService, 1360, "NetworkService"),
        Win("Spooler", "Print Spooler", "Manages printing and your printers.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalSystem, 2210, null, canPauseContinue: false),
        Win("Audiosrv", "Windows Audio", "Plays sound. Stopping it leaves the PC silent until it runs again.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalService, 2088, "AudioGroup"),
        Win("wuauserv", "Windows Update", "Downloads and installs Windows and security updates.",
            ServiceRunState.Running, ServiceStartupType.Manual, LocalSystem, 3120, "netsvcs"),
        Win("WinDefend", "Microsoft Defender Antivirus Service", "Your built-in real-time virus protection.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalSystem, 3540, null, canStop: false),
        Win("SysMain", "SysMain", "Pre-loads apps you use often. Usually makes little difference on an SSD.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalSystem, 1760, null),
        Win("LanmanWorkstation", "Workstation", "Connects to shared folders and printers on your network.",
            ServiceRunState.Running, ServiceStartupType.Automatic, NetworkService, 1520, "NetworkProvider"),
        Win("WSearch", "Windows Search", "Builds the search index for the Start menu and File Explorer.",
            ServiceRunState.Running, ServiceStartupType.AutomaticDelayed, LocalSystem, 4280, null, canPauseContinue: true),
        Win("BITS", "Background Intelligent Transfer Service", "Downloads updates quietly in the background.",
            ServiceRunState.Stopped, ServiceStartupType.Manual, LocalSystem, null, "netsvcs"),
        Win("Fax", "Fax", "Lets the PC send and receive faxes. Off on most PCs.",
            ServiceRunState.Stopped, ServiceStartupType.Disabled, NetworkService, null, null),

        // Third-party services.
        ThirdParty("GoogleUpdaterService", "Google Updater Service", "Keeps Google software up to date.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalSystem, 6120,
            @"C:\Program Files (x86)\Google\GoogleUpdater\current\updater.exe"),
        ThirdParty("NvContainerLocalSystem", "NVIDIA Display Container LS", "Runs NVIDIA's control panel and driver features.",
            ServiceRunState.Running, ServiceStartupType.Automatic, LocalSystem, 3980,
            @"C:\Program Files\NVIDIA Corporation\Display.NvContainer\NVDisplay.Container.exe"),
        ThirdParty("SteamClientService", "Steam Client Service", "A helper Steam starts when it needs it.",
            ServiceRunState.Stopped, ServiceStartupType.Manual, LocalSystem, null,
            @"C:\Program Files (x86)\Common Files\Steam\steamservice.exe"),
    ];

    private static ServiceDetails Win(string name, string display, string description, ServiceRunState state,
        ServiceStartupType startup, string? logOn, int? pid, string? group, bool canStop = true, bool canPauseContinue = false) =>
        new(name, display, description, state, startup, logOn, pid, group,
            $@"C:\Windows\System32\svchost.exe -k {group ?? "LocalService"}", IsMicrosoft: true, canStop, canPauseContinue);

    private static ServiceDetails ThirdParty(string name, string display, string description, ServiceRunState state,
        ServiceStartupType startup, string? logOn, int? pid, string binaryPath) =>
        new(name, display, description, state, startup, logOn, pid, null, binaryPath, IsMicrosoft: false, CanStop: true, CanPauseContinue: false);
}
