using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>Startup apps, automatic services and boot configuration.</summary>
public interface IStartupService
{
    Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken);
    Task<OperationResult> RemoveStartupItemAsync(StartupItem item, CancellationToken cancellationToken);
    Task<OperationResult> AddStartupItemAsync(string name, string command, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceInfo>> GetAutoStartServicesAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken);
    Task<BootConfig> GetBootConfigAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetBootTimeoutAsync(int seconds, CancellationToken cancellationToken);
    Task<OperationResult> SetSafeModeNextBootAsync(bool enabled, CancellationToken cancellationToken);
    Task<OperationResult> SetFastStartupAsync(bool enabled, CancellationToken cancellationToken);
    Task<OperationResult> RestartToUefiAsync(CancellationToken cancellationToken);
    Task<OperationResult> RestartToAdvancedStartupAsync(CancellationToken cancellationToken);
    OperationResult OpenStartupAppsSettings();
    OperationResult OpenTaskScheduler();
    OperationResult OpenSystemConfiguration();
}
