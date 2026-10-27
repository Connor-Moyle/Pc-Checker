using PcChecker.Core.Common;

namespace PcChecker.Core.Services;

/// <summary>
/// Every Windows service, with the actions the Services page offers. Changes (start, stop, restart, pause, continue,
/// set start type) go through the change guard and are journaled; the read calls only read.
/// </summary>
public interface IServiceManagementService
{
    /// <summary>Reads every service with its state, start type, description, account, PID and group.</summary>
    Task<IReadOnlyList<ServiceDetails>> GetServicesAsync(CancellationToken cancellationToken);

    /// <summary>Which services the given service depends on, and which depend on it.</summary>
    Task<ServiceDependencies> GetDependenciesAsync(string serviceName, CancellationToken cancellationToken);

    Task<OperationResult> StartAsync(ServiceDetails service, CancellationToken cancellationToken);

    Task<OperationResult> StopAsync(ServiceDetails service, CancellationToken cancellationToken);

    /// <summary>Stops the service and its dependents, then starts them again in the right order.</summary>
    Task<OperationResult> RestartAsync(ServiceDetails service, CancellationToken cancellationToken);

    Task<OperationResult> PauseAsync(ServiceDetails service, CancellationToken cancellationToken);

    Task<OperationResult> ContinueAsync(ServiceDetails service, CancellationToken cancellationToken);

    /// <summary>Changes the start type. The previous type is journaled so the change can be undone.</summary>
    Task<OperationResult> SetStartupTypeAsync(ServiceDetails service, ServiceStartupType startupType, CancellationToken cancellationToken);

    /// <summary>Opens the Windows Services console (services.msc).</summary>
    OperationResult OpenServicesConsole();

    /// <summary>Opens File Explorer with the service's program file selected.</summary>
    OperationResult OpenFileLocation(ServiceDetails service);

    /// <summary>Opens a web search for the service name in the default browser.</summary>
    OperationResult SearchOnline(ServiceDetails service);
}
