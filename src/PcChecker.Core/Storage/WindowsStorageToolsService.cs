// TODO(storage): storage module — MSFT_PhysicalDisk, raw read surface scan, unbuffered benchmark, chkdsk/Repair-Volume/Optimize-Volume.
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.Storage;

/// <summary>Windows implementation of <see cref="IStorageToolsService"/>.</summary>
public sealed class WindowsStorageToolsService : IStorageToolsService
{
    private const string NotImplemented = "Storage tools are not implemented yet.";

    public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StorageDevice>>([]);

    public Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, int blockSizeMb, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new SurfaceScanResult(diskNumber, 0, 0, 0, 0, 0, TimeSpan.Zero, false, [], [], NotImplemented));

    public Task<DiskBenchmarkResult> BenchmarkAsync(string driveLetter, int testFileMb, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new DiskBenchmarkResult(driveLetter, 0, 0, 0, 0, 0, 0, 0, NotImplemented));

    public Task<OperationResult> CheckDiskAsync(string driveLetter, bool scanOnly, bool scheduleOfflineFix, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> RepairVolumeAsync(string driveLetter, RepairMode mode, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> OptimizeAsync(string driveLetter, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<IReadOnlyList<DiskErrorEvent>> GetDiskErrorEventsAsync(int days, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiskErrorEvent>>([]);

    public IReadOnlyList<VendorLink> GetVendorTools(IEnumerable<StorageDevice> devices) => StorageVendorRules.ToolsFor(devices);
}
