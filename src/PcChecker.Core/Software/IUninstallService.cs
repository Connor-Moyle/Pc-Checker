using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Uninstalls programs and Store apps.</summary>
public interface IUninstallService
{
    (bool CanUninstall, string Reason) CanUninstall(InstalledProgram program);
    Task<OperationResult> UninstallAsync(InstalledProgram program, bool quiet, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Uninstalls with explicit options (quiet switches; for Store apps, whether every account loses the app).</summary>
    Task<OperationResult> UninstallAsync(InstalledProgram program, UninstallOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
