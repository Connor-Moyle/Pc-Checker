using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Software;

/// <summary>
/// Windows implementation of <see cref="IUninstallService"/>: builds the command with
/// <see cref="UninstallCommandBuilder"/>, runs it through <see cref="IChangeGuard"/> (restore point + journal), maps the
/// exit code and confirms the registry entry is gone (allowing a short grace period for uninstallers that finish in the background).
/// </summary>
public sealed class WindowsUninstallService : IUninstallService
{
    private readonly IChangeGuard _guard;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsUninstallService> _logger;

    public WindowsUninstallService(IChangeGuard guard, ISettingsService settings, ILogger<WindowsUninstallService> logger)
    {
        _guard = guard;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// When set, overrides the advanced-mode setting for protected (non-critical) programs. Null follows settings.
    /// </summary>
    public bool? AllowProtectedOverride { get; set; }

    private bool AdvancedMode => AllowProtectedOverride ?? _settings.Current.General.AdvancedMode;

    public (bool CanUninstall, string Reason) CanUninstall(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!PlatformGuard.IsWindows)
        {
            return (false, "Uninstalling only works on Windows.");
        }

        var (allowed, reason) = ProtectedSoftwareRules.CanUninstall(program, AdvancedMode);
        if (!allowed)
        {
            return (false, reason);
        }

        if (program.Source == SoftwareSource.StoreApp || program.Installer == InstallerKind.StoreApp)
        {
            return string.IsNullOrWhiteSpace(program.PackageFullName)
                ? (false, "This Store app has no package name recorded, so it cannot be removed from here.")
                : (true, "Store app; removed with Remove-AppxPackage.");
        }

        if (UninstallCommandBuilder.Build(program, quiet: false) is null)
        {
            return (false, "No uninstall command is recorded for this program. Use its own uninstaller or Windows Settings → Apps.");
        }

        return (true, reason);
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

        bool storeApp = program.Source == SoftwareSource.StoreApp || program.Installer == InstallerKind.StoreApp;
        string scope = storeApp && options.StoreAppAllUsers ? " for all users" : string.Empty;
        var change = new ChangeOptions(null, null, "Software", program.Name, Reversible: false, UndoHint: "Reinstall from the vendor");
        return await _guard.RunGuardedAsync($"Uninstall {program.Name}{scope}", change,
            ct => RunUninstallAsync(program, options, progress, ct), progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> RunUninstallAsync(InstalledProgram program, UninstallOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        bool quiet = options.Quiet;
        if (program.Source == SoftwareSource.StoreApp || program.Installer == InstallerKind.StoreApp)
        {
            return await RemoveStoreAppAsync(program, options.StoreAppAllUsers, progress, cancellationToken).ConfigureAwait(false);
        }

        var command = UninstallCommandBuilder.Build(program, quiet);
        if (command is null)
        {
            return OperationResult.Fail("No uninstall command could be built for this program.");
        }

        var (file, args) = command.Value;
        _logger.LogInformation("Running uninstaller for {Program}: {File} {Args}", program.Name, file, args);
        progress?.Report(new ProgressUpdate(quiet ? "Uninstalling quietly" : "Running the uninstaller", 20, program.Name));

        var run = await UninstallProcessRunner.RunAsync(file, args, quiet, progress, cancellationToken).ConfigureAwait(false);
        if (!run.Success)
        {
            return run.AsUntyped();
        }

        var mapped = UninstallExitCodes.Map(run.Value, program.Name);
        if (!mapped.Success)
        {
            _logger.LogWarning("Uninstaller for {Program} exited with {Code}: {Message}", program.Name, run.Value, mapped.Message);
            return mapped;
        }

        progress?.Report(new ProgressUpdate("Checking the program is gone", 90, program.Name));

        // NSIS-style uninstallers exit before their temp copy has finished removing the program, so give the registry
        // entry a moment to disappear instead of judging it the instant the process returns.
        var gone = await UninstallCompletionWaiter.WaitUntilGoneAsync(
            () => RegistryUninstallReader.KeyExists(program.RegistryKey),
            UninstallCompletionWaiter.GraceFor(program.Installer, quiet),
            UninstallCompletionWaiter.DefaultInterval,
            cancellationToken).ConfigureAwait(false);
        if (gone)
        {
            return mapped;
        }

        _logger.LogInformation("Uninstaller for {Program} reported success but its registry entry is still present after the grace period.", program.Name);
        if (quiet)
        {
            return mapped with
            {
                Message = mapped.Message + $" {program.Name} is still registered, so its uninstaller may still be finishing in the background."
                    + " Scan again in a minute; if it still appears, run the uninstall without quiet mode so you can follow its own prompts.",
            };
        }

        return mapped with { Message = mapped.Message + " Its registry entry is still present; it may disappear after a restart or the uninstaller may have been cancelled." };
    }

    /// <param name="allUsers">Remove the package from every account (an explicit per-uninstall choice, never inferred from a global toggle).</param>
    private async Task<OperationResult> RemoveStoreAppAsync(InstalledProgram program, bool allUsers, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var package = program.PackageFullName!;
        var scope = allUsers ? " -AllUsers" : string.Empty;
        var command = $"Remove-AppxPackage -Package {PowerShellRunner.Quote(package)}{scope} -ErrorAction Stop";
        _logger.LogInformation("Removing Store app {Package} (all users: {AllUsers})", package, allUsers);
        progress?.Report(new ProgressUpdate("Removing Store app", 30, program.Name));

        var result = await PowerShellRunner.RunAsync(command, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return OperationResult.Fail($"Removing {program.Name} took too long and was stopped.");
        }

        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? $"PowerShell exit code {result.ExitCode}" : result.StdErr.Split('\n')[0].Trim();
            if (result.StdErr.Contains("0x80073CFA", StringComparison.OrdinalIgnoreCase) || result.StdErr.Contains("cannot be removed", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail($"{program.Name} is a system app that Windows does not allow to be removed.");
            }

            return OperationResult.Fail($"Removing {program.Name} failed: {detail}");
        }

        return allUsers
            ? OperationResult.Ok($"{program.Name} was removed for all users on this PC.")
            : ElevatedUserScope.Annotate(OperationResult.Ok($"{program.Name} was removed from your account; other user accounts on this PC keep it."));
    }
}
