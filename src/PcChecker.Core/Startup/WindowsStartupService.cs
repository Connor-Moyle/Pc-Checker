// TODO(startup): startup module — StartupApproved bytes, scheduled tasks, services, bcdedit, fast startup, shutdown /r /fw.
using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>Windows implementation of <see cref="IStartupService"/>.</summary>
public sealed class WindowsStartupService : IStartupService
{
    private const string NotImplemented = "Startup management is not implemented yet.";

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken) => StartupEnumerator.EnumerateAsync(cancellationToken);
    public Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> RemoveStartupItemAsync(StartupItem item, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> AddStartupItemAsync(string name, string command, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<IReadOnlyList<ServiceInfo>> GetAutoStartServicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ServiceInfo>>([]);
    public Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<BootConfig> GetBootConfigAsync(CancellationToken cancellationToken) => Task.FromResult(BootConfig.Unknown);
    public Task<OperationResult> SetBootTimeoutAsync(int seconds, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetSafeModeNextBootAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetFastStartupAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> RestartToUefiAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> RestartToAdvancedStartupAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public OperationResult OpenStartupAppsSettings() => ShellLauncher.Open("ms-settings:startupapps");
    public OperationResult OpenTaskScheduler() => ShellLauncher.Open("taskschd.msc");
    public OperationResult OpenSystemConfiguration() => ShellLauncher.Open("msconfig.exe");
}
