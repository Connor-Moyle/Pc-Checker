using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Self-update from GitHub Releases: the app is a single executable that replaces itself.</summary>
public interface IAppUpdateService
{
    string CurrentVersion { get; }

    /// <summary>True when this copy can download and swap in a new executable by itself (a published PcChecker.exe).</summary>
    bool IsInstalledViaUpdater { get; }

    bool IsUpdateDownloaded { get; }

    /// <summary>Raised after the new executable has been put in place and started; the app should close normally.</summary>
    event EventHandler? RestartRequested;

    Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Re-verifies the downloaded executable, swaps it in and starts it. Returns a failure (and keeps the current
    /// version running) when there is no verified download or the swap fails.
    /// </summary>
    OperationResult ApplyAndRestart();
}
