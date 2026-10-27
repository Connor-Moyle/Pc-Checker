using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Demo updater: reports 0.1.0 as the running version and a 0.1.1 release with notes, downloadable in a few seconds.</summary>
public sealed class DemoAppUpdateService : IAppUpdateService
{
    /// <summary>Demo mode never restarts; the event exists to satisfy the contract.</summary>
    public event EventHandler? RestartRequested { add { } remove { } }

    public const string LatestVersion = "0.1.1";

    private const string ReleaseNotes =
        "## PC Checker 0.1.1\n\n" +
        "- Fans: new \"Quiet night\" built-in profile and smoother curve ramping.\n" +
        "- Lighting: reconnects to OpenRGB automatically after it restarts.\n" +
        "- Storage: surface scan now shows a live block map and remembers the last result.\n" +
        "- Memory: XMP/EXPO guidance names the exact BIOS menu for ASUS, MSI, Gigabyte and ASRock boards.\n" +
        "- Fixed: the overlay hotkey was ignored while a game ran in exclusive full screen.\n" +
        "- Fixed: the Health page could show a stale score after a drive was removed.";

    public string CurrentVersion => "0.1.0";
    public bool IsInstalledViaUpdater => true;
    public bool IsUpdateDownloaded { get; private set; }

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 800).ConfigureAwait(false);
        return new AppUpdateInfo(CurrentVersion, LatestVersion, ReleaseNotes, DateTimeOffset.Now.AddDays(-1), false,
            UpdateDecision.IsNewer(CurrentVersion, LatestVersion));
    }

    public async Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        for (int i = 0; i <= 10; i++)
        {
            progress?.Report(new ProgressUpdate("Downloading update", i * 10, $"{i * 2.4:0.0} of 24.0 MB"));
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        IsUpdateDownloaded = true;
        return OperationResult.Ok($"PC Checker {LatestVersion} is downloaded. Restart to install it (demo: nothing is installed).");
    }

    public OperationResult ApplyAndRestart() =>
        // Demo mode never restarts the app.
        OperationResult.Ok("Demo mode: nothing is installed.");
}
