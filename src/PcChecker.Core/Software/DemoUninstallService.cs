using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Software;

/// <summary>Demo uninstaller: applies the real protection rules, pretends to run the uninstaller and succeeds.</summary>
public sealed class DemoUninstallService : IUninstallService
{
    private readonly ISettingsService? _settings;

    public DemoUninstallService()
    {
    }

    public DemoUninstallService(ISettingsService settings)
    {
        _settings = settings;
    }

    private bool AdvancedMode => _settings?.Current.General.AdvancedMode ?? false;

    public (bool CanUninstall, string Reason) CanUninstall(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var (allowed, reason) = ProtectedSoftwareRules.CanUninstall(program, AdvancedMode);
        if (!allowed)
        {
            return (false, reason);
        }

        if (program.Source != SoftwareSource.StoreApp && UninstallCommandBuilder.Build(program, quiet: false) is null)
        {
            return (false, "No uninstall command is recorded for this program.");
        }

        return (true, program.Source == SoftwareSource.StoreApp ? "Store app; removed with Remove-AppxPackage (demo)." : "Ready to uninstall (demo).");
    }

    public Task<OperationResult> UninstallAsync(InstalledProgram program, bool quiet, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        UninstallAsync(program, new UninstallOptions(quiet), progress, cancellationToken);

    public async Task<OperationResult> UninstallAsync(InstalledProgram program, UninstallOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(options);
        var (can, why) = CanUninstall(program);
        if (!can)
        {
            return OperationResult.Fail(why);
        }

        bool quiet = options.Quiet;
        progress?.Report(new ProgressUpdate(quiet ? "Uninstalling quietly" : "Running the uninstaller", 40, program.Name));
        await DemoData.DelayAsync(cancellationToken, 600, 1200).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking the program is gone", 90, program.Name));
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        bool storeApp = program.Source == SoftwareSource.StoreApp || program.Installer == InstallerKind.StoreApp;
        string scope = storeApp ? (options.StoreAppAllUsers ? " for all users" : " from your account") : string.Empty;
        return OperationResult.Ok($"{program.Name} was uninstalled{(quiet && !storeApp ? " quietly" : string.Empty)}{scope} (demo — nothing was changed).");
    }
}
