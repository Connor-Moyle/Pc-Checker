using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Windows implementation of <see cref="IDriverService"/>: WMI (<c>Win32_PnPSignedDriver</c> + <c>Win32_PnPEntity</c>)
/// for the installed list, the Windows Update Agent for the driver catalog, and <c>pnputil</c> for backup, install
/// and rollback. Every system change goes through <see cref="IChangeGuard"/>.
/// </summary>
public sealed class WindowsDriverService : IDriverService, IDriverStoreProvider, IDriverUpdateStateProvider, IDriverUpdateHiding
{
    private const string ModuleName = "Drivers";

    private readonly ILogger<WindowsDriverService> _logger;
    private readonly IChangeGuard _changeGuard;
    private readonly IHardwareInventoryService _hardware;
    private readonly WindowsUpdateAgent _agent;
    private readonly object _gate = new();
    private Dictionary<string, DriverInfo> _byDeviceId = new(StringComparer.OrdinalIgnoreCase);

    public WindowsDriverService(ILogger<WindowsDriverService> logger, IChangeGuard changeGuard, IHardwareInventoryService hardware)
    {
        _logger = logger;
        _changeGuard = changeGuard;
        _hardware = hardware;
        _agent = new WindowsUpdateAgent(logger);
    }

    /// <summary>The most recent installed-driver scan, or null before the first scan.</summary>
    public DriverScanResult? LastScan { get; private set; }

    public async Task<DriverScanResult> ScanInstalledAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return DriverScanResult.Empty("Driver scanning is only available on Windows.");
        }

        var result = await Task.Run(() => ScanCore(progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _byDeviceId = result.Drivers.Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
                .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        LastScan = result;
        return result;
    }

    private DriverScanResult ScanCore(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var entities = new Dictionary<string, DriverInfoBuilder.EntityFacts>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ProgressUpdate("Reading device status", 10));
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, ConfigManagerErrorCode, Status, Present FROM Win32_PnPEntity");
            foreach (ManagementBaseObject entity in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (entity)
                {
                    var bag = ToBag(entity);
                    var id = WmiPropertyMapper.GetString(bag, "DeviceID");
                    if (id is null)
                    {
                        continue;
                    }

                    entities[id] = new DriverInfoBuilder.EntityFacts(
                        WmiPropertyMapper.GetInt32(bag, "ConfigManagerErrorCode"),
                        WmiPropertyMapper.GetString(bag, "Status"),
                        WmiPropertyMapper.GetBool(bag, "Present"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Win32_PnPEntity query failed; problem codes will be missing.");
            warnings.Add("Device status could not be read (" + ex.Message + "). Problem codes are not shown.");
        }

        progress?.Report(new ProgressUpdate("Reading installed drivers", 40));
        var drivers = new List<DriverInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPSignedDriver");
            int n = 0;
            foreach (ManagementBaseObject driver in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (driver)
                {
                    var bag = ToBag(driver);
                    var id = WmiPropertyMapper.GetString(bag, "DeviceID");
                    entities.TryGetValue(id ?? string.Empty, out var facts);
                    drivers.Add(DriverInfoBuilder.FromProperties(bag, facts));
                }

                if (++n % 50 == 0)
                {
                    progress?.Report(new ProgressUpdate("Reading installed drivers", Math.Min(90, 40 + n / 10.0), $"{n} drivers"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Win32_PnPSignedDriver query failed.");
            warnings.Add("Installed drivers could not be read: " + ex.Message);
        }

        progress?.Report(new ProgressUpdate("Sorting", 95));
        var sorted = DriverInfoBuilder.Sort(DriverInfoBuilder.Deduplicate(drivers));
        _logger.LogInformation("Driver scan found {Count} drivers ({Problems} with problems).", sorted.Count, sorted.Count(d => d.HasProblem));
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DriverScanResult(sorted, warnings, DateTimeOffset.Now);
    }

    public async Task<DriverUpdateCheckResult> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var links = DriverRules.VendorLinksFor(_hardware.Last);
        if (!PlatformGuard.IsWindows)
        {
            return new DriverUpdateCheckResult([], links, ["Windows Update is only available on Windows."], DateTimeOffset.Now);
        }

        var (updates, warnings) = await _agent.SearchAsync(progress, cancellationToken).ConfigureAwait(false);
        var allWarnings = new List<string>(warnings);
        if (_hardware.Last is null)
        {
            allWarnings.Add("Run a hardware scan to get download links for your exact graphics card, chipset and motherboard.");
        }

        _logger.LogInformation("Windows Update reported {Count} driver update(s).", updates.Count);
        bool amdSoftware = Overclocking.VendorSoftwareCatalog.AmdAdrenalin.Locate(File.Exists) is not null;
        bool nvidiaSoftware = Overclocking.VendorSoftwareCatalog.NvidiaDriverToolsInstalled(File.Exists, Directory.Exists);
        bool amdChipsetSoftware = Overclocking.VendorSoftwareCatalog.AmdChipsetSoftwareInstalled(Directory.Exists);
        var annotated = DriverUpdateRules.Annotate(updates, amdSoftware, nvidiaSoftware, amdChipsetSoftware);
        if (annotated.Any(u => u.VendorWarning is not null))
        {
            allWarnings.Add("Some offers would replace a driver that AMD Software, AMD Chipset Software or NVIDIA's software manages; they are left unticked. Use \"Hide selected\" to stop Windows Update offering them, or the Debloat tweak \"Don't install drivers through Windows Update\" to stop driver updates for good.");
        }

        return new DriverUpdateCheckResult(annotated, links, allWarnings, DateTimeOffset.Now);
    }

    public async Task<OperationResult> SetHiddenAsync(IReadOnlyList<DriverUpdateInfo> updates, bool hidden, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return OperationResult.Fail("Nothing selected.");
        }

        var target = updates.Count == 1 ? updates[0].Title : $"{updates.Count} driver updates";
        return await _changeGuard.RunGuardedAsync(
            hidden ? "Hide driver updates" : "Unhide driver updates",
            ChangeOptions.For(ModuleName, target, reversible: true, undoHint: hidden ? "Drivers page → More → Unhide driver updates" : "Hide the update again on the Drivers page.")
                with { CreateBefore = false, CreateAfter = false },
            ct => _agent.SetHiddenAsync(updates, hidden, ct),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DriverUpdateInfo>> GetHiddenAsync(CancellationToken cancellationToken) => _agent.SearchHiddenAsync(cancellationToken);

    public Task<OperationResult> DownloadAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return _agent.DownloadAsync(updates, progress, cancellationToken);
    }

    public Task<IReadOnlySet<string>> GetDownloadedUpdateIdsAsync(IEnumerable<string> updateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        return _agent.GetDownloadedIdsAsync(updateIds, cancellationToken);
    }

    public async Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return new DriverInstallResult(false, false, []);
        }

        var target = updates.Count == 1 ? updates[0].Title : $"{updates.Count} driver updates";
        var guarded = await _changeGuard.RunGuardedAsync(
            "Install driver updates",
            ChangeOptions.For(ModuleName, target, reversible: true, undoHint: "Device Manager → device → Properties → Driver → Roll Back Driver, or restore the restore point.") with { OnRestorePointFailed = RestorePointFailedBehavior.Abort },
            async ct =>
            {
                var r = await _agent.InstallAsync(updates, progress, ct).ConfigureAwait(false);
                // The per-update results travel with the outcome either way; only Success decides how the change
                // guard records it (journal entry, optional "after" restore point).
                return new OperationResult<DriverInstallResult>(r.Success, Summarize(r), r, null, r.RebootRequired);
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        if (guarded.Value is { } value)
        {
            return value;
        }

        return new DriverInstallResult(false, false, updates.Select(u => (u, OperationResult.Fail(guarded.Message))).ToList());
    }

    public async Task<OperationResult> BackupDriversAsync(string folder, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Driver backup is only available on Windows.");
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("The backup folder could not be created: " + ex.Message, ex);
        }

        int? total = null;
        try
        {
            var store = await GetDriverStoreAsync(cancellationToken).ConfigureAwait(false);
            if (store.Packages.Count > 0)
            {
                total = store.Packages.Count;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Progress is cosmetic.
        }

        int done = 0;
        progress?.Report(new ProgressUpdate("Exporting driver packages", 0, folder));
        var result = await PnpUtilRunner.RunAsync("/export-driver * " + PnpUtilRunner.Quote(folder), line =>
        {
            if (PnpUtilParser.IsExportLine(line))
            {
                done++;
                double? pct = total is > 0 ? Math.Min(99, 100.0 * done / total.Value) : null;
                progress?.Report(new ProgressUpdate("Exporting driver packages", pct, total is > 0 ? $"{done} of {total}" : $"{done} exported"));
            }
        }, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return OperationResult.Fail("Driver backup could not start: " + result.StartError);
        }

        if (result.TimedOut)
        {
            return OperationResult.Fail("Driver backup took too long and was stopped.");
        }

        var (success, _, text) = PnpUtilParser.InterpretExitCode(result.ExitCode);
        var (_, exported) = PnpUtilParser.ParseExportSummary(result.Output);
        int count = exported ?? done;
        _logger.LogInformation("Driver backup to {Folder}: exit {Code}, {Count} packages.", folder, result.ExitCode, count);
        progress?.Report(new ProgressUpdate("Done", 100));
        return success
            ? OperationResult.Ok($"Exported {count} driver package(s) to {folder}.")
            : OperationResult.Fail($"Driver backup failed: {text}");
    }

    public Task<OperationResult> InstallInfAsync(string infPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Driver installation is only available on Windows."));
        }

        if (!File.Exists(infPath) || !infPath.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(OperationResult.Fail("Choose an existing .inf file."));
        }

        return _changeGuard.RunGuardedAsync(
            "Install driver package " + Path.GetFileName(infPath),
            ChangeOptions.For(ModuleName, infPath, reversible: true, undoHint: "Remove the package with pnputil /delete-driver or roll back from Device Manager.") with { OnRestorePointFailed = RestorePointFailedBehavior.Abort },
            async ct =>
            {
                var r = await PnpUtilRunner.RunAsync("/add-driver " + PnpUtilRunner.Quote(infPath) + " /install", null, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return FromPnpUtil(r, $"Installed {Path.GetFileName(infPath)}.");
            },
            null,
            cancellationToken);
    }

    public Task<OperationResult> RollbackAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Driver rollback is only available on Windows."));
        }

        DriverInfo? driver;
        lock (_gate)
        {
            _byDeviceId.TryGetValue(deviceId, out driver);
        }

        if (driver is null)
        {
            return Task.FromResult(OperationResult.Fail("This device was not in the last scan. Scan drivers again, or use Device Manager → Properties → Driver → Roll Back Driver."));
        }

        if (!DriverRules.CanRollBack(driver))
        {
            return Task.FromResult(OperationResult.Fail($"{driver.DeviceName} uses the driver Windows ships ({driver.InfName ?? "inbox"}), so there is no third-party package to remove. Use Device Manager → Properties → Driver → Roll Back Driver if an earlier version exists."));
        }

        IReadOnlyList<DriverInfo> all;
        lock (_gate)
        {
            all = _byDeviceId.Values.ToList();
        }

        if (DriverRules.RollbackBlockReason(driver, all) is { } blocked)
        {
            _logger.LogInformation("Rollback of {Inf} for {Device} refused: {Reason}", driver.InfName, driver.DeviceName, blocked);
            return Task.FromResult(OperationResult.Fail(blocked));
        }

        var inf = driver.InfName!.Trim();
        var backup = Path.Combine(DriverCleanupRules.BackupFolderFor(DriverCleanupRules.DefaultBackupRoot, DateTime.Now), Path.GetFileNameWithoutExtension(inf));
        return _changeGuard.RunGuardedAsync(
            $"Remove driver package {inf} for {driver.DeviceName}",
            ChangeOptions.For(ModuleName, deviceId, reversible: true,
                undoHint: $"The package was copied to {backup} first. Reinstall it with Drivers → More → Install .inf (Advanced mode), or restore the restore point.")
                with { OnRestorePointFailed = RestorePointFailedBehavior.Abort },
            async ct =>
            {
                try
                {
                    Directory.CreateDirectory(backup);
                }
                catch (Exception ex)
                {
                    return OperationResult.Fail("Not removed: the backup folder could not be created: " + ex.Message, ex);
                }

                var export = await PnpUtilRunner.RunAsync($"/export-driver {inf} {PnpUtilRunner.Quote(backup)}", null, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                var exported = FromPnpUtil(export, "Exported.");
                if (!exported.Success || !ContainsFiles(backup))
                {
                    _logger.LogWarning("Export of {Inf} failed (exit {Code}); the package was not removed.", inf, export.ExitCode);
                    return OperationResult.Fail("Not removed, because a backup copy could not be made first. " + exported.Message);
                }

                // No /force: Windows takes the package off the device, then refuses the delete if anything still uses it.
                var r = await PnpUtilRunner.RunAsync($"/delete-driver {inf} /uninstall", null, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return FromPnpUtil(r, $"Removed {inf}. Windows will fall back to its own driver for {driver.DeviceName} (or look for one on Windows Update). A copy is in {backup}.");
            },
            null,
            cancellationToken);
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

    public async Task<DriverStoreResult> GetDriverStoreAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return DriverStoreResult.Empty("The driver store can only be listed on Windows.");
        }

        var result = await PnpUtilRunner.RunAsync("/enum-drivers", null, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (!result.Started)
        {
            return DriverStoreResult.Empty("pnputil could not start: " + result.StartError);
        }

        var packages = PnpUtilParser.ParseEnumDrivers(result.Output);
        var warnings = new List<string>();
        if (packages.Count == 0 && result.ExitCode != 0)
        {
            warnings.Add("pnputil /enum-drivers failed: " + PnpUtilParser.InterpretExitCode(result.ExitCode).Text);
        }
        else if (packages.Count == 0 && PnpUtilParser.ContainsFieldLines(result.Output))
        {
            _logger.LogWarning("pnputil /enum-drivers printed a listing that could not be parsed (display language {Culture}).", CultureInfo.CurrentUICulture.Name);
            warnings.Add("pnputil listed driver packages in a format PC Checker could not read (it prints in the Windows display language). The driver store list is incomplete.");
        }

        return new DriverStoreResult(packages.OrderBy(p => p.Provider).ThenBy(p => p.OriginalName).ToList(), warnings, DateTimeOffset.Now);
    }

    private static OperationResult FromPnpUtil(PnpUtilResult r, string successMessage)
    {
        if (!r.Started)
        {
            return OperationResult.Fail("pnputil could not start: " + r.StartError);
        }

        if (r.TimedOut)
        {
            return OperationResult.Fail("pnputil took too long and was stopped.");
        }

        var (success, reboot, text) = PnpUtilParser.InterpretExitCode(r.ExitCode);
        var tail = LastMeaningfulLine(r.Output);
        return success
            ? OperationResult.Ok(reboot ? successMessage + " Restart Windows to finish." : successMessage, reboot)
            : OperationResult.Fail(text + (tail is null ? string.Empty : " " + tail));
    }

    private static string? LastMeaningfulLine(string output)
    {
        foreach (var line in output.Split('\n').Reverse())
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("Microsoft PnP", StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return null;
    }

    private static string Summarize(DriverInstallResult r)
    {
        int ok = r.Results.Count(x => x.Result.Success);
        var text = $"{ok} of {r.Results.Count} update(s) installed.";
        return r.RebootRequired ? text + " Restart Windows to finish." : text;
    }

    private static Dictionary<string, object?> ToBag(ManagementBaseObject obj)
    {
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.Properties)
        {
            bag[p.Name] = p.Value;
        }

        return bag;
    }
}
