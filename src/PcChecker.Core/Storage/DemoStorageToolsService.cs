using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.Storage;

/// <summary>
/// Demo storage tools for the sample gaming desktop: a healthy Samsung 990 PRO NVMe (C:) and a WD 4 TB hard disk (D:)
/// with two unreadable blocks, a simulated surface scan with a live block map, a benchmark with plausible figures,
/// chkdsk / Repair-Volume / Optimize runs with streamed output lines, and a few disk events. Pure .NET.
/// </summary>
public sealed class DemoStorageToolsService : IStorageToolsService, ISurfaceScanner
{
    private static readonly StorageDevice[] Devices =
    [
        new(0, DemoData.NvmeModel, "S6Z2NJ0DEMO0001", "4B2QJXD7", DemoData.NvmeBytes, "SSD", "NVMe", HealthStatus.Good, ["C:"], true),
        new(1, DemoData.HddModel, "WD-WX12DEMO0002", "80.00A80", DemoData.HddBytes, "HDD", "SATA", HealthStatus.Warning, ["D:"], false),
    ];

    public async Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return Devices;
    }

    public Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, int blockSizeMb, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken) =>
        SurfaceScanAsync(diskNumber, SurfaceScanOptions.Full(blockSizeMb), progress, cancellationToken);

    public async Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, SurfaceScanOptions options, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var device = Devices.FirstOrDefault(d => d.Number == diskNumber) ?? Devices[0];
        bool isHdd = device.MediaType == "HDD";

        // The demo "drive" has 400 blocks so a scan takes a few seconds; quick mode samples it like a real quick scan would.
        const int totalBlocks = 400;
        long blockBytes = device.SizeBytes / totalBlocks;
        var plan = ScanPlan.Build(totalBlocks, options with { RandomSamples = Math.Min(options.RandomSamples, 60) }, seed: diskNumber + 7);
        var map = new BlockState[totalBlocks];
        var cells = new BlockMapAccumulator(totalBlocks, 200);
        var random = DemoData.CreateRandom(diskNumber + 11);
        var started = DateTimeOffset.Now;
        long bad = 0, slow = 0, done = 0;
        var badOffsets = new List<long>();
        double baseSpeed = isHdd ? 190 : 3400;
        double min = double.MaxValue, sum = 0;

        // Bad blocks sit near the end of the HDD, inside the area a quick scan always covers.
        int[] badBlocks = isHdd ? [395, 396] : [];

        foreach (long index in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(isHdd ? 12 : 5, cancellationToken).ConfigureAwait(false);
            double speed = DemoData.Jitter(random, baseSpeed, baseSpeed * 0.1);
            var state = BlockState.Ok;
            if (badBlocks.Contains((int)index))
            {
                state = BlockState.Bad;
                bad++;
                badOffsets.Add(index * blockBytes);
                speed = 0;
            }
            else if (isHdd && random.NextDouble() < 0.015)
            {
                state = BlockState.Slow;
                slow++;
                speed /= 4;
            }

            map[index] = state;
            cells.Set(index, state);
            done++;
            if (state != BlockState.Bad)
            {
                sum += speed;
                min = Math.Min(min, speed);
            }

            progress?.Report(new SurfaceScanProgress(plan.Count, done, bad, slow, Math.Round(speed), cells.Snapshot()));
        }

        var elapsed = DateTimeOffset.Now - started;
        long okBlocks = Math.Max(1, done - bad);
        string summary = SurfaceScanMath.BuildSummary(device.Model, options.Mode, bad, slow, done, true) + " (demo data)";
        return new SurfaceScanResult(diskNumber, done * blockBytes, bad, slow, Math.Round(sum / okBlocks), Math.Round(min == double.MaxValue ? 0 : min),
            elapsed, true, badOffsets, map, summary);
    }

    public async Task<DiskBenchmarkResult> BenchmarkAsync(string driveLetter, int testFileMb, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages = ["Sequential write", "Sequential read", "Random 4K read", "Random 4K write"];
        bool hdd = driveLetter.StartsWith("D", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < stages.Length; i++)
        {
            for (int step = 0; step < 5; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ProgressUpdate(stages[i], 25 * i + 5 * step, $"{(hdd ? 190 : 6800) + step * 7} MB/s so far"));
                await Task.Delay(70, cancellationToken).ConfigureAwait(false);
            }
        }

        var letter = StorageCodes.NormalizeDriveLetter(driveLetter) ?? driveLetter;
        var result = hdd
            ? new DiskBenchmarkResult(letter, 198, 185, 0.9, 1.6, 230, 410, testFileMb * 1024L * 1024L, string.Empty)
            : new DiskBenchmarkResult(letter, 7120, 6540, 82, 215, 21000, 55000, testFileMb * 1024L * 1024L, string.Empty);
        var expected = hdd ? BenchmarkReference.Hdd : BenchmarkReference.NvmeGen4;
        result = result with { Summary = BenchmarkReference.Summarize(result, expected) + " (demo data)" };
        progress?.Report(new ProgressUpdate("Done", 100));
        return result;
    }

    public async Task<OperationResult> CheckDiskAsync(string driveLetter, bool scanOnly, bool scheduleOfflineFix, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] lines =
        [
            "The type of the file system is NTFS.",
            "Stage 1: Examining basic file system structure ...",
            "  1,204,480 file records processed.",
            "Stage 2: Examining file name linkage ...",
            "  1,520,118 index entries processed.",
            "Stage 3: Examining security descriptors ...",
            "Security descriptor verification completed.",
        ];
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate("chkdsk", null, line));
            await Task.Delay(110, cancellationToken).ConfigureAwait(false);
        }

        if (scheduleOfflineFix)
        {
            // The demo's Windows drive is C:; every other volume is "in use", so chkdsk declines the dismount and schedules instead.
            progress?.Report(new ProgressUpdate("chkdsk", null, "Chkdsk cannot run because the volume is in use by another process."));
            progress?.Report(new ProgressUpdate("Done", 100, "This volume will be checked the next time the system restarts."));
            return OperationResult.Ok($"A full check and repair of {driveLetter} will run the next time Windows starts. Restart when convenient and allow it to finish. (demo)", rebootRequired: true);
        }

        progress?.Report(new ProgressUpdate("Done", 100, "Windows has scanned the file system and found no problems."));
        return OperationResult.Ok($"Windows found no file system problems on {driveLetter}. (demo)");
    }

    public async Task<OperationResult> RepairVolumeAsync(string driveLetter, RepairMode mode, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate($"Repair-Volume {mode}", null, $"VERBOSE: Performing operation \"Repair-Volume\" on target \"{driveLetter}\"."));
        await DemoData.DelayAsync(cancellationToken, 400, 900).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Done", 100, "NoErrorsFound"));
        string summary = StorageCommandParsers.DescribeRepairVolumeStatus("NoErrorsFound", mode, driveLetter) + " (demo)";
        // Only the Windows volume (C: in the demo) is deferred to the next restart; a data volume is checked offline right away.
        bool reboot = mode == RepairMode.OfflineScanAndFix && StorageCodes.IsSystemVolume(driveLetter, @"C:\");
        if (reboot)
        {
            summary = StorageCommandParsers.DescribeRepairVolumeStatus("NoErrorsFound", mode, driveLetter) + " The offline check runs the next time Windows starts. (demo)";
        }

        return OperationResult.Ok(summary, reboot);
    }

    public async Task<OperationResult> OptimizeAsync(string driveLetter, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        bool hdd = driveLetter.StartsWith("D", StringComparison.OrdinalIgnoreCase);
        string[] lines = hdd
            ?
            [
                $"VERBOSE: Invoking defragmentation on {driveLetter}...",
                "VERBOSE: Defragmentation:  12% complete.",
                "VERBOSE: Defragmentation:  58% complete.",
                "VERBOSE: Defragmentation: 100% complete.",
                "VERBOSE: Post Defragmentation Report: Total fragmented space = 3%",
            ]
            :
            [
                $"VERBOSE: Invoking retrim on {driveLetter}...",
                "VERBOSE: Retrim:  34% complete.",
                "VERBOSE: Retrim: 100% complete.",
                "VERBOSE: Post Defragmentation Report: Total space trimmed = 1.42 TB",
            ];
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate("Optimize-Volume", null, line));
            await Task.Delay(140, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok(hdd
            ? $"{driveLetter} was defragmented. (demo)"
            : $"{driveLetter} optimized: TRIM told the SSD which blocks are free, which keeps it fast. (demo)");
    }

    public async Task<IReadOnlyList<DiskErrorEvent>> GetDiskErrorEventsAsync(int days, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var all = new List<DiskErrorEvent>
        {
            new(now.AddDays(-2).AddHours(-3), "disk", 153, "The IO operation at logical block address 0x1a2b3c for Disk 1 (PDO name: \\Device\\0000003a) was retried.", @"\Device\Harddisk1\DR1"),
            new(now.AddDays(-2).AddHours(-3).AddMinutes(-4), "disk", 153, "The IO operation at logical block address 0x1a2b10 for Disk 1 (PDO name: \\Device\\0000003a) was retried.", @"\Device\Harddisk1\DR1"),
            new(now.AddDays(-9), "Ntfs", 98, "Volume D: (\\Device\\HarddiskVolume5) is healthy. No action is needed.", "D:"),
            new(now.AddDays(-21), "storahci", 129, "Reset to device, \\Device\\RaidPort1, was issued.", @"\Device\RaidPort1"),
            new(now.AddDays(-40), "disk", 7, "The device, \\Device\\Harddisk1\\DR1, has a bad block.", @"\Device\Harddisk1\DR1"),
        };
        var since = now.AddDays(-Math.Clamp(days, 1, 365));
        return all.Where(e => e.Time >= since).OrderByDescending(e => e.Time).ToList();
    }

    public IReadOnlyList<VendorLink> GetVendorTools(IEnumerable<StorageDevice> devices) => StorageVendorRules.ToolsFor(devices);
}
