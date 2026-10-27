using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>
/// Demo old-driver scan for the sample gaming desktop: three NVIDIA display packages left behind by graphics driver
/// updates, an older Realtek audio package, a capture card that is no longer connected, and an Intel chipset package
/// that is still in use next to a newer staged version. Runs the real <see cref="DriverCleanupRules"/>; removals are
/// simulated and remembered, so a rescan reflects them. Pure .NET.
/// </summary>
public sealed class DemoDriverCleanupService : IDriverCleanupService
{
    private const string MediaGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";
    private const string DisplayGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string SystemGuid = "{4d36e97d-e325-11ce-bfc1-08002be10318}";
    private const string Whql = "Microsoft Windows Hardware Compatibility Publisher";

    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    public string BackupRoot => DriverCleanupRules.DefaultBackupRoot;

    public async Task<DriverCleanupScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading installed drivers", 10));
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading the driver store", 40));
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Looking for devices that are no longer connected", 70));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);

        HashSet<string> removed;
        lock (_removed)
        {
            removed = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        }

        var packages = SamplePackages().Where(p => !removed.Contains(p.PublishedName)).ToList();
        var ghosts = SampleDisconnected().Where(d => !removed.Contains(d.InstanceId)).ToList();
        var items = DriverCleanupRules.Analyze(packages, SampleDrivers(), ghosts, SampleSizes());
        progress?.Report(new ProgressUpdate("Done", 100));
        return new DriverCleanupScanResult(items, DriverCleanupRules.Summarize(items), [], DateTimeOffset.Now);
    }

    public async Task<OperationResult<DriverCleanupRemovalResult>> RemoveAsync(IReadOnlyList<DriverCleanupStep> plan, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0)
        {
            return OperationResult<DriverCleanupRemovalResult>.Fail("Nothing was selected.");
        }

        var backup = DriverCleanupRules.BackupFolderFor(BackupRoot, DateTime.Now);
        var inUse = DriverCleanupRules.PackagesInUse(SampleDrivers());
        var results = new List<DriverCleanupStepResult>();
        for (int i = 0; i < plan.Count; i++)
        {
            var step = plan[i];
            progress?.Report(new ProgressUpdate(step.Kind == DriverCleanupStepKind.RemoveDevice ? "Forgetting device" : "Backing up and removing",
                100.0 * i / plan.Count, step.Title));
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (step.Kind == DriverCleanupStepKind.RemoveDevice)
            {
                Remember(step.Target);
                results.Add(new(step, OperationResult.Ok($"Windows forgot {step.Title} (demo)."), null));
            }
            else if (inUse.ContainsKey(step.Target))
            {
                // Same answer pnputil gives without /force.
                results.Add(new(step, OperationResult.Fail(PnpUtilParser.InterpretExitCode(PnpUtilParser.DriverStoreDeleteFailedExitCode).Text), null));
            }
            else
            {
                var folder = Path.Combine(backup, Path.GetFileNameWithoutExtension(step.Target));
                Remember(step.Target);
                results.Add(new(step, OperationResult.Ok($"Removed {step.Target}. A copy is in {folder} (demo)."), folder));
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        bool anyPackage = plan.Any(s => s.Kind == DriverCleanupStepKind.DeletePackage);
        var outcome = new DriverCleanupRemovalResult(results, anyPackage ? backup : null, false);
        return new OperationResult<DriverCleanupRemovalResult>(outcome.Succeeded > 0, outcome.Summary, outcome);
    }

    /// <summary>The demo driver store: the regular sample packages plus the leftovers this page finds.</summary>
    public static IReadOnlyList<DriverPackage> SamplePackages() =>
    [
        .. DemoDriverService.SamplePackages(),
        new("oem19.inf", "nv_dispi.inf", "NVIDIA Corporation", "Display adapters", DisplayGuid, "30.0.15.1179", new DateTime(2022, 2, 2), Whql),
        new("oem27.inf", "nv_dispi.inf", "NVIDIA Corporation", "Display adapters", DisplayGuid, "31.0.15.3623", new DateTime(2023, 6, 21), Whql),
        new("oem31.inf", "nv_dispi.inf", "NVIDIA Corporation", "Display adapters", DisplayGuid, "31.0.15.5222", new DateTime(2024, 3, 26), Whql),
        new("oem14.inf", "hdxrt.inf", "Realtek Semiconductor Corp.", "Sound, video and game controllers", MediaGuid, "6.0.9225.1", new DateTime(2021, 9, 14), Whql),
        new("oem33.inf", "EGAVDevice.inf", "Elgato Systems GmbH", "Sound, video and game controllers", MediaGuid, "1.2.0.164", new DateTime(2021, 5, 4), Whql),
        new("oem2.inf", "AlderLakePCH-SSystem.inf", "INTEL", "System devices", SystemGuid, "10.1.47.8", new DateTime(2023, 3, 1), Whql),
        new("oem40.inf", "AlderLakePCH-SSystem.inf", "INTEL", "System devices", SystemGuid, "10.1.49.10", new DateTime(2024, 8, 12), Whql),
    ];

    /// <summary>The demo installed drivers: the regular sample plus the chipset device that uses the older Intel package.</summary>
    public static IReadOnlyList<DriverInfo> SampleDrivers() =>
    [
        .. DemoDriverService.SampleDrivers(),
        new("Intel(R) SMBus - 7AA3", @"PCI\VEN_8086&DEV_7AA3", "System", "INTEL", "10.1.47.8", new DateTime(2023, 3, 1), "oem2.inf", true,
            "Microsoft Windows Hardware Compatibility Publisher", false, [@"PCI\VEN_8086&DEV_7AA3"], "OK", 0, "PCI bus 0, device 31, function 4"),
    ];

    /// <summary>The capture card that was unplugged a while ago.</summary>
    public static IReadOnlyList<DisconnectedDevice> SampleDisconnected() =>
    [
        new(@"USB\VID_0FD9&PID_004F\000000000001", "Elgato Game Capture HD60 S", "MEDIA", MediaGuid, "Elgato Systems GmbH", "Disconnected", "oem33.inf"),
    ];

    /// <summary>Sizes of the demo packages' FileRepository folders.</summary>
    public static IReadOnlyDictionary<string, long> SampleSizes() => new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["oem12.inf"] = 1_492_000_000,
        ["oem19.inf"] = 1_046_000_000,
        ["oem27.inf"] = 1_211_000_000,
        ["oem31.inf"] = 1_318_000_000,
        ["oem14.inf"] = 61_400_000,
        ["oem15.inf"] = 64_900_000,
        ["oem33.inf"] = 18_700_000,
        ["oem2.inf"] = 410_000,
        ["oem40.inf"] = 430_000,
    };

    private void Remember(string id)
    {
        lock (_removed)
        {
            _removed.Add(id);
        }
    }
}
