using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Windows implementation of <see cref="IDriverCleanupService"/>. Reads the installed drivers through
/// <see cref="IDriverService"/> (WMI), the driver store through <see cref="IDriverStoreProvider"/> (<c>pnputil
/// /enum-drivers</c>) and disconnected devices with <c>pnputil /enum-devices /disconnected</c> (Windows 10 2004 and
/// later; WMI fills in on older builds). Removal runs under <see cref="IChangeGuard"/>: devices are forgotten with
/// <c>pnputil /remove-device</c>, and each package is exported with <c>/export-driver</c> before
/// <c>/delete-driver</c> (never <c>/force</c> or <c>/uninstall</c>).
/// </summary>
public sealed class WindowsDriverCleanupService : IDriverCleanupService
{
    private const string ModuleName = "Drivers";

    /// <summary>First Windows build whose pnputil has <c>/enum-devices</c> and <c>/remove-device</c> (Windows 10 2004).</summary>
    private const int EnumDevicesMinBuild = 19041;

    private readonly ILogger<WindowsDriverCleanupService> _logger;
    private readonly IChangeGuard _changeGuard;
    private readonly IDriverService _drivers;

    public WindowsDriverCleanupService(ILogger<WindowsDriverCleanupService> logger, IChangeGuard changeGuard, IDriverService drivers)
    {
        _logger = logger;
        _changeGuard = changeGuard;
        _drivers = drivers;
    }

    public string BackupRoot => DriverCleanupRules.DefaultBackupRoot;

    private static bool HasEnumDevices => Environment.OSVersion.Version.Build >= EnumDevicesMinBuild;

    public async Task<DriverCleanupScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return DriverCleanupScanResult.Empty("Old drivers can only be checked on Windows.");
        }

        var warnings = new List<string>();
        progress?.Report(new ProgressUpdate("Reading installed drivers", 5));
        var installed = await _drivers.ScanInstalledAsync(null, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(installed.Warnings);
        bool usageKnown = installed.Drivers.Any(d => d.IsPresent);
        if (!usageKnown)
        {
            warnings.Add("PC Checker could not read which devices use which driver, so every package is marked Keep.");
        }

        progress?.Report(new ProgressUpdate("Reading the driver store", 35));
        IReadOnlyList<DriverPackage> packages = [];
        if (_drivers is IDriverStoreProvider store)
        {
            var listing = await store.GetDriverStoreAsync(cancellationToken).ConfigureAwait(false);
            packages = listing.Packages;
            warnings.AddRange(listing.Warnings);
        }
        else
        {
            warnings.Add("The driver store could not be listed.");
        }

        progress?.Report(new ProgressUpdate("Looking for devices that are no longer connected", 55));
        var ghosts = await ReadDisconnectedAsync(warnings, cancellationToken).ConfigureAwait(false);

        var items = DriverCleanupRules.Analyze(packages, installed.Drivers, ghosts, null, usageKnown);
        progress?.Report(new ProgressUpdate("Measuring driver packages", 75));
        var sizes = await Task.Run(() => MeasurePackages(items, cancellationToken), cancellationToken).ConfigureAwait(false);
        items = DriverCleanupRules.Sort(DriverCleanupRules.ApplySizes(items, sizes));
        var summary = DriverCleanupRules.Summarize(items);
        _logger.LogInformation("Old driver scan: {Packages} package(s) found, {Safe} safe, {Probably} probably safe, {Keep} keep, {Devices} disconnected device(s), {Bytes} bytes freeable.",
            summary.PackagesFound, summary.SafeToRemove, summary.ProbablySafe, summary.Keep, summary.DisconnectedDevices, summary.FreeableBytes);
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DriverCleanupScanResult(items, summary, warnings.Distinct().ToList(), DateTimeOffset.Now);
    }

    public async Task<OperationResult<DriverCleanupRemovalResult>> RemoveAsync(IReadOnlyList<DriverCleanupStep> plan, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0)
        {
            return OperationResult<DriverCleanupRemovalResult>.Fail("Nothing was selected.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<DriverCleanupRemovalResult>.Fail("Drivers can only be removed on Windows.");
        }

        var backup = DriverCleanupRules.BackupFolderFor(BackupRoot, DateTime.Now);
        int packages = plan.Count(s => s.Kind == DriverCleanupStepKind.DeletePackage);
        int devices = plan.Count - packages;
        var description = (packages, devices) switch
        {
            (> 0, > 0) => $"Remove {packages} old driver package(s) and {devices} disconnected device(s)",
            (> 0, _) => $"Remove {packages} old driver package(s)",
            _ => $"Remove {devices} disconnected device(s)",
        };

        return await _changeGuard.RunGuardedAsync(
            description,
            ChangeOptions.For(ModuleName, string.Join(", ", plan.Select(s => s.Target)), reversible: packages > 0,
                undoHint: $"Each package was copied to {backup} first. Reinstall one with Drivers → More → Install .inf (Advanced mode), or restore the restore point.")
                with { OnRestorePointFailed = RestorePointFailedBehavior.Abort },
            async ct =>
            {
                var results = new List<DriverCleanupStepResult>();
                for (int i = 0; i < plan.Count; i++)
                {
                    var step = plan[i];
                    progress?.Report(new ProgressUpdate(step.Kind == DriverCleanupStepKind.RemoveDevice ? "Forgetting device" : "Backing up and removing",
                        100.0 * i / plan.Count, step.Title));
                    results.Add(step.Kind == DriverCleanupStepKind.RemoveDevice
                        ? new DriverCleanupStepResult(step, await RemoveDeviceAsync(step, ct).ConfigureAwait(false), null)
                        : await DeletePackageAsync(step, backup, ct).ConfigureAwait(false));
                }

                progress?.Report(new ProgressUpdate("Done", 100));
                var outcome = new DriverCleanupRemovalResult(results, packages > 0 ? backup : null, results.Any(r => r.Result.RebootRequired));
                _logger.LogInformation("Old driver removal: {Ok} of {Total} step(s) succeeded. Backups in {Folder}.", outcome.Succeeded, results.Count, backup);
                return new OperationResult<DriverCleanupRemovalResult>(outcome.Succeeded > 0, outcome.Summary, outcome, null, outcome.RebootRequired);
            },
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DisconnectedDevice>> ReadDisconnectedAsync(List<string> warnings, CancellationToken ct)
    {
        if (!HasEnumDevices)
        {
            // WMI's non-present devices (already in the installed-driver scan) stand in on older builds.
            return [];
        }

        var r = await PnpUtilRunner.RunAsync("/enum-devices /disconnected", null, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!r.Started || r.TimedOut)
        {
            warnings.Add("Disconnected devices could not be listed with pnputil; devices Windows reports through WMI are shown instead.");
            return [];
        }

        var devices = PnpUtilParser.ParseEnumDevices(r.Output);
        if (devices.Count == 0 && r.ExitCode != 0 && r.ExitCode != PnpUtilParser.NoMoreItemsExitCode)
        {
            // An older pnputil without /disconnected prints its usage text and fails; fall back quietly.
            _logger.LogInformation("pnputil /enum-devices /disconnected is not available (exit {Code}); using WMI.", r.ExitCode);
        }

        return devices;
    }

    private async Task<OperationResult> RemoveDeviceAsync(DriverCleanupStep step, CancellationToken ct)
    {
        if (!HasEnumDevices)
        {
            return OperationResult.Fail("Forgetting devices needs Windows 10 version 2004 or later. Use Device Manager → View → Show hidden devices, then uninstall the greyed-out device.");
        }

        var r = await PnpUtilRunner.RunAsync("/remove-device " + Quote(step.Target), null, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var result = PnpUtilParser.ToOperationResult(r, $"Windows forgot {step.Title}.");
        _logger.LogInformation("pnputil /remove-device {Id}: exit {Code}.", step.Target, r.ExitCode);
        return result;
    }

    private async Task<DriverCleanupStepResult> DeletePackageAsync(DriverCleanupStep step, string backupRoot, CancellationToken ct)
    {
        if (!DriverCleanupRules.IsRemovablePackageName(step.Target))
        {
            return new(step, OperationResult.Fail($"{step.Target} ships with Windows and is never removed."), null);
        }

        var folder = Path.Combine(backupRoot, Path.GetFileNameWithoutExtension(step.Target));
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return new(step, OperationResult.Fail("Not removed: the backup folder could not be created: " + ex.Message, ex), null);
        }

        var export = await PnpUtilRunner.RunAsync($"/export-driver {step.Target} {PnpUtilRunner.Quote(folder)}", null, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        var exported = PnpUtilParser.ToOperationResult(export, "Exported.");
        if (!exported.Success || !ContainsFiles(folder))
        {
            _logger.LogWarning("Export of {Inf} failed (exit {Code}); the package was not deleted.", step.Target, export.ExitCode);
            return new(step, OperationResult.Fail("Not removed, because a backup copy could not be made first. " + exported.Message), null);
        }

        // No /uninstall and no /force: Windows refuses a package that a device still uses, which is exactly what we want.
        var delete = await PnpUtilRunner.RunAsync("/delete-driver " + step.Target, null, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        var result = PnpUtilParser.ToOperationResult(delete, $"Removed {step.Target}. A copy is in {folder}.");
        _logger.LogInformation("pnputil /delete-driver {Inf}: exit {Code}.", step.Target, delete.ExitCode);
        return new(step, result, folder);
    }

    /// <summary>Folder sizes in the driver store for the package findings (published name → bytes).</summary>
    private Dictionary<string, long> MeasurePackages(IEnumerable<DriverCleanupItem> items, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var repository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
        foreach (var item in items.Where(i => i.Kind == DriverCleanupKind.Package && i.Package is not null))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var folder = FindRepositoryFolder(repository, item.Package!);
                if (folder is not null)
                {
                    sizes[item.Id] = FolderSize(folder);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not measure {Inf}.", item.Id);
            }
        }

        return sizes;
    }

    /// <summary>
    /// The FileRepository folder of a package. Windows records it under
    /// <c>HKLM\SYSTEM\DriverDatabase\DriverInfFiles\oemN.inf</c>; when that key is missing, the folder whose INF is
    /// byte-identical to <c>%windir%\INF\oemN.inf</c> is used.
    /// </summary>
    private static string? FindRepositoryFolder(string repository, DriverPackage package)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverInfFiles\" + package.PublishedName))
        {
            var candidates = new List<string>();
            if (key?.GetValue("Active") is string active && active.Length > 0)
            {
                candidates.Add(active);
            }

            if (key?.GetValue(null) is string[] all)
            {
                candidates.AddRange(all.Reverse());
            }

            foreach (var name in candidates)
            {
                var path = Path.Combine(repository, name);
                if (Directory.Exists(path))
                {
                    return path;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(package.OriginalName) || !Directory.Exists(repository))
        {
            return null;
        }

        var published = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", package.PublishedName);
        if (!File.Exists(published))
        {
            return null;
        }

        var expected = File.ReadAllBytes(published);
        foreach (var dir in Directory.EnumerateDirectories(repository, package.OriginalName + "_*"))
        {
            var inf = Path.Combine(dir, package.OriginalName);
            if (File.Exists(inf) && new FileInfo(inf).Length == expected.Length && File.ReadAllBytes(inf).AsSpan().SequenceEqual(expected))
            {
                return dir;
            }
        }

        return null;
    }

    private static long FolderSize(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    private static bool ContainsFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value) => "\"" + value.Trim() + "\"";
}
