using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.Storage;

/// <summary>Demo storage tools: the sample NVMe and HDD, a quick simulated surface scan and benchmark.</summary>
public sealed class DemoStorageToolsService : IStorageToolsService
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

    public async Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, int blockSizeMb, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken)
    {
        var device = Devices.FirstOrDefault(d => d.Number == diskNumber) ?? Devices[0];
        const int blocks = 400;
        var map = new BlockState[blocks];
        var random = DemoData.CreateRandom(diskNumber + 11);
        var started = DateTimeOffset.Now;
        long bad = 0, slow = 0;
        var badOffsets = new List<long>();
        bool isHdd = device.MediaType == "HDD";
        double baseSpeed = isHdd ? 190 : 3400;
        double min = double.MaxValue, sum = 0;

        for (int i = 0; i < blocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(isHdd ? 12 : 5, cancellationToken).ConfigureAwait(false);
            double speed = DemoData.Jitter(random, baseSpeed, baseSpeed * 0.1);
            var state = BlockState.Ok;
            if (isHdd && (i == 311 || i == 312))
            {
                state = BlockState.Bad;
                bad++;
                badOffsets.Add((long)i * blockSizeMb * 1024L * 1024L);
                speed = 0;
            }
            else if (isHdd && random.NextDouble() < 0.015)
            {
                state = BlockState.Slow;
                slow++;
                speed /= 4;
            }

            map[i] = state;
            sum += speed;
            min = Math.Min(min, speed);
            progress?.Report(new SurfaceScanProgress(blocks, i + 1, bad, slow, Math.Round(speed), BlockMapBuilder.Downsample(map, 200)));
        }

        var elapsed = DateTimeOffset.Now - started;
        var summary = bad > 0
            ? $"{bad} bad blocks found on {device.Model}. Back up this drive now and plan to replace it."
            : $"No bad blocks found on {device.Model}. {slow} slow blocks (normal wear or background activity).";
        return new SurfaceScanResult(diskNumber, device.SizeBytes, bad, slow, Math.Round(sum / blocks), Math.Round(min == double.MaxValue ? 0 : min),
            elapsed, true, badOffsets, map, summary + " (demo scan of a sample of the drive)");
    }

    public async Task<DiskBenchmarkResult> BenchmarkAsync(string driveLetter, int testFileMb, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages = ["Sequential write", "Sequential read", "Random 4K read", "Random 4K write"];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 25 * i, $"{testFileMb} MB test file"));
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        bool hdd = driveLetter.StartsWith("D", StringComparison.OrdinalIgnoreCase);
        var result = hdd
            ? new DiskBenchmarkResult(driveLetter, 198, 185, 0.9, 1.6, 230, 410, testFileMb * 1024L * 1024L, "Hard disk class: fine for media and backups, slow for games and Windows.")
            : new DiskBenchmarkResult(driveLetter, 7120, 6540, 82, 215, 21000, 55000, testFileMb * 1024L * 1024L, "NVMe (PCIe 4.0 class): excellent. No action needed.");
        progress?.Report(new ProgressUpdate("Done", 100));
        return result;
    }

    public async Task<OperationResult> CheckDiskAsync(string driveLetter, bool scanOnly, bool scheduleOfflineFix, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Checking file system", 10, driveLetter));
        await DemoData.DelayAsync(cancellationToken, 400, 900).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Done", 100));
        return scheduleOfflineFix
            ? OperationResult.Ok($"A full repair of {driveLetter} will run the next time Windows starts (demo).", rebootRequired: true)
            : OperationResult.Ok($"Windows found no file system problems on {driveLetter} (demo).");
    }

    public async Task<OperationResult> RepairVolumeAsync(string driveLetter, RepairMode mode, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 900).ConfigureAwait(false);
        return OperationResult.Ok($"Repair-Volume {mode} on {driveLetter} completed: no corruption found (demo).", mode == RepairMode.OfflineScanAndFix);
    }

    public async Task<OperationResult> OptimizeAsync(string driveLetter, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Optimizing", 30, driveLetter));
        await DemoData.DelayAsync(cancellationToken, 400, 900).ConfigureAwait(false);
        return OperationResult.Ok($"{driveLetter} optimized (TRIM sent to the SSD) (demo).");
    }

    public async Task<IReadOnlyList<DiskErrorEvent>> GetDiskErrorEventsAsync(int days, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        return
        [
            new(now.AddDays(-2).AddHours(-3), "disk", 153, "The IO operation at logical block address 0x1a2b3c for Disk 1 was retried.", @"\Device\Harddisk1\DR1"),
            new(now.AddDays(-9), "Ntfs", 98, "Volume D: (\\Device\\HarddiskVolume5) is healthy. No action is needed.", "D:"),
        ];
    }

    public IReadOnlyList<VendorLink> GetVendorTools(IEnumerable<StorageDevice> devices) => StorageVendorRules.ToolsFor(devices);
}
