using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.Storage;

/// <summary>Drive testing (read-only surface scan, benchmark), repair (chkdsk, Repair-Volume), optimize and error history.</summary>
public interface IStorageToolsService
{
    Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, int blockSizeMb, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken);
    Task<DiskBenchmarkResult> BenchmarkAsync(string driveLetter, int testFileMb, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> CheckDiskAsync(string driveLetter, bool scanOnly, bool scheduleOfflineFix, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> RepairVolumeAsync(string driveLetter, RepairMode mode, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> OptimizeAsync(string driveLetter, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiskErrorEvent>> GetDiskErrorEventsAsync(int days, CancellationToken cancellationToken);
    IReadOnlyList<VendorLink> GetVendorTools(IEnumerable<StorageDevice> devices);
}
