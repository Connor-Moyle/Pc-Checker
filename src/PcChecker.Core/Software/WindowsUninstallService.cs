// TODO(software): software module — runs UninstallCommandBuilder output through IChangeGuard.
using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Windows implementation of <see cref="IUninstallService"/>.</summary>
public sealed class WindowsUninstallService : IUninstallService
{
    public (bool CanUninstall, string Reason) CanUninstall(InstalledProgram program) =>
        (false, "Uninstalling is not implemented yet.");

    public Task<OperationResult> UninstallAsync(InstalledProgram program, bool quiet, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail("Uninstalling is not implemented yet."));
}
