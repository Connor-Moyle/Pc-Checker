using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Demo uninstaller: pretends to run the uninstaller and succeeds.</summary>
public sealed class DemoUninstallService : IUninstallService
{
    public (bool CanUninstall, string Reason) CanUninstall(InstalledProgram program) =>
        program.IsProtected ? (false, program.ProtectionReason ?? "Protected") : (true, "Ready to uninstall (demo).");

    public async Task<OperationResult> UninstallAsync(InstalledProgram program, bool quiet, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Uninstalling", 40, program.Name));
        await DemoData.DelayAsync(cancellationToken, 600, 1200).ConfigureAwait(false);
        return OperationResult.Ok($"{program.Name} was uninstalled{(quiet ? " quietly" : string.Empty)} (demo).");
    }
}
