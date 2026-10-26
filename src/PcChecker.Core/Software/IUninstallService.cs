using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Uninstalls programs and Store apps.</summary>
public interface IUninstallService
{
    (bool CanUninstall, string Reason) CanUninstall(InstalledProgram program);
    Task<OperationResult> UninstallAsync(InstalledProgram program, bool quiet, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
