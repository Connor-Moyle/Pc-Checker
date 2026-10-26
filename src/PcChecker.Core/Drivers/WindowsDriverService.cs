// TODO(drivers): drivers module — Win32_PnPSignedDriver, Windows Update Agent COM, pnputil.
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>Windows implementation of <see cref="IDriverService"/>.</summary>
public sealed class WindowsDriverService : IDriverService
{
    private const string NotImplemented = "Driver management is not implemented yet.";

    public Task<DriverScanResult> ScanInstalledAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(DriverScanResult.Empty(NotImplemented));

    public Task<DriverUpdateCheckResult> CheckForUpdatesAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(DriverUpdateCheckResult.Empty(NotImplemented));

    public Task<OperationResult> DownloadAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<DriverInstallResult> InstallAsync(IReadOnlyList<DriverUpdateInfo> updates, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new DriverInstallResult(false, false, updates.Select(u => (u, OperationResult.Fail(NotImplemented))).ToList()));

    public Task<OperationResult> BackupDriversAsync(string folder, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> InstallInfAsync(string infPath, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> RollbackAsync(string deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));
}
