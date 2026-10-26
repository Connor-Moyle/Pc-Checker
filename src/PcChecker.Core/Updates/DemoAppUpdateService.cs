using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Demo updater: reports the running version as current and a newer release available.</summary>
public sealed class DemoAppUpdateService : IAppUpdateService
{
    public string CurrentVersion => "0.1.0";
    public bool IsInstalledViaUpdater => true;
    public bool IsUpdateDownloaded { get; private set; }

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 800).ConfigureAwait(false);
        return new AppUpdateInfo(CurrentVersion, "0.2.0", "Fan curves, RGB lighting, storage tests and more.", DateTimeOffset.Now.AddDays(-1), false, true);
    }

    public async Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        for (int i = 0; i <= 10; i++)
        {
            progress?.Report(new ProgressUpdate("Downloading update", i * 10));
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        IsUpdateDownloaded = true;
        return OperationResult.Ok("Update downloaded (demo).");
    }

    public void ApplyAndRestart()
    {
    }
}
