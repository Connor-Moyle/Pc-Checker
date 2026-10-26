// TODO(features): Windows features module — DISM/PowerShell cmdlets via PowerShellRunner and IChangeGuard.
using PcChecker.Core.Common;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>Windows implementation of <see cref="IWindowsFeatureService"/>.</summary>
public sealed class WindowsFeatureService : IWindowsFeatureService
{
    private const string NotImplemented = "Windows feature management is not implemented yet.";

    public Task<IReadOnlyList<WindowsFeature>> ListAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WindowsFeature>>([]);

    public Task<OperationResult> EnableAsync(WindowsFeature feature, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> DisableAsync(WindowsFeature feature, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> RemoveInboxAppAsync(WindowsFeature feature, bool allUsers, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));
}
