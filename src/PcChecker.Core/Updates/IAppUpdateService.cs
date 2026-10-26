using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Self-update from GitHub Releases.</summary>
public interface IAppUpdateService
{
    string CurrentVersion { get; }
    bool IsInstalledViaUpdater { get; }
    bool IsUpdateDownloaded { get; }
    Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    void ApplyAndRestart();
}
