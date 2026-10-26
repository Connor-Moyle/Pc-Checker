using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>Installed driver inventory, Windows Update driver search, install, backup and rollback.</summary>
public interface IDriverService
{
    Task<DriverScanResult> ScanInstalledAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<DriverUpdateCheckResult> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> DownloadAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> BackupDriversAsync(string folder, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> InstallInfAsync(string infPath, CancellationToken cancellationToken);
    Task<OperationResult> RollbackAsync(string deviceId, CancellationToken cancellationToken);
}
