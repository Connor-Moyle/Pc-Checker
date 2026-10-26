using PcChecker.Core.Common;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>Optional features, capabilities and removable inbox apps.</summary>
public interface IWindowsFeatureService
{
    Task<IReadOnlyList<WindowsFeature>> ListAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> EnableAsync(WindowsFeature feature, CancellationToken cancellationToken);
    Task<OperationResult> DisableAsync(WindowsFeature feature, CancellationToken cancellationToken);
    Task<OperationResult> RemoveInboxAppAsync(WindowsFeature feature, bool allUsers, CancellationToken cancellationToken);
}
