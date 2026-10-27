using PcChecker.Core.Common;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Everything the debloat engine reads from and changes on the machine. <see cref="WindowsDebloatSystem"/> talks to
/// Windows; <see cref="InMemoryDebloatSystem"/> simulates a PC for demo mode and tests.
/// </summary>
public interface IDebloatSystem
{
    IRegistryAccessor Registry { get; }

    /// <summary>The Windows build number (e.g. 26100), or null when it cannot be read.</summary>
    int? OsBuild { get; }

    /// <summary>Scheduled task path → enabled, for the given task paths; missing tasks are left out. Null when the query failed.</summary>
    Task<IReadOnlyDictionary<string, bool>?> GetTaskStatesAsync(IReadOnlyCollection<string> taskPaths, CancellationToken cancellationToken);

    /// <summary>Package names of the Store apps installed for the current user; null when the query failed.</summary>
    Task<IReadOnlySet<string>?> GetInstalledAppsAsync(CancellationToken cancellationToken);

    /// <summary>State of an optional feature; <see cref="FeatureState.NotPresent"/> when this Windows has no such feature.</summary>
    Task<FeatureState> GetOptionalFeatureStateAsync(string featureName, CancellationToken cancellationToken);

    /// <summary>Runs a detection script and returns true/false from its output, or null when it failed.</summary>
    Task<bool?> DetectAsync(PowerShellOp operation, CancellationToken cancellationToken);

    /// <summary>Names of the installed printers (including virtual ones such as Microsoft Print to PDF).</summary>
    IReadOnlyList<string> GetPrinterNames();

    Task<OperationResult> SetServiceStartModeAsync(string serviceName, DebloatStartMode mode, CancellationToken cancellationToken);

    Task<OperationResult> SetTaskEnabledAsync(string taskPath, bool enabled, CancellationToken cancellationToken);

    Task<OperationResult> RemoveAppAsync(AppxRemovalOp operation, bool allUsers, CancellationToken cancellationToken);

    Task<OperationResult> SetOptionalFeatureAsync(OptionalFeatureOp operation, bool enabled, CancellationToken cancellationToken);

    Task<OperationResult> RunScriptAsync(string script, CancellationToken cancellationToken);
}
