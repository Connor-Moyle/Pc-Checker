using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// The part of <see cref="IPcPlatformService"/> the Windows and demo services share: one check at a time per mode
/// (the chipset and catalog providers ask together during a driver search and get the same answer), a short reuse
/// window so a search right after the card's check does not repeat the work, and the last report.
/// </summary>
public abstract class PcPlatformServiceBase : IPcPlatformService
{
    /// <summary>A report this fresh is reused by a non-forced check in the same mode.</summary>
    public static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<bool, (Task<PcPlatformReport> Task, DateTimeOffset Started, string SettingsKey)> _runs = [];

    public PcPlatformReport? Last { get; private set; }

    public async Task<PcPlatformReport> CheckAsync(bool online, bool forceRefresh, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var settings = (CurrentSettings() ?? new DriverSourceSettings()).Clone();
        var key = $"{settings.Chipset}|{settings.MakerCatalogs}";
        Task<PcPlatformReport> task;
        lock (_gate)
        {
            if (!forceRefresh && Reusable(online, key) is { } reuse)
            {
                task = reuse;
            }
            else if (!forceRefresh && !online && Reusable(true, key) is { IsCompletedSuccessfully: true } onlineRun)
            {
                // A recent online report already holds everything an offline one would.
                task = onlineRun;
            }
            else
            {
                // The shared run is not tied to one caller's token: a caller that gives up only stops waiting.
                task = Task.Run(() => BuildAsync(settings, online, forceRefresh, progress, CancellationToken.None), CancellationToken.None);
                _runs[online] = (task, DateTimeOffset.Now, key);
            }
        }

        var report = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Last = report;
        return report;
    }

    /// <summary>A running check, or a finished one younger than <see cref="ReuseFor"/>, for the same mode and switches.</summary>
    private Task<PcPlatformReport>? Reusable(bool online, string key) =>
        _runs.TryGetValue(online, out var run) && run.SettingsKey == key
        && (!run.Task.IsCompleted || (run.Task.IsCompletedSuccessfully && DateTimeOffset.Now - run.Started < ReuseFor))
            ? run.Task
            : null;

    /// <summary>The driver source options in force (the chipset and catalog switches).</summary>
    protected abstract DriverSourceSettings? CurrentSettings();

    /// <summary>Reads the PC and builds a report.</summary>
    protected abstract Task<PcPlatformReport> BuildAsync(DriverSourceSettings settings, bool online, bool forceRefresh, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
