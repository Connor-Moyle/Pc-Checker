using PcChecker.Core.Common;

namespace PcChecker.Core.Crashes;

/// <summary>
/// Reads blue screens, sudden shutdowns, graphics driver resets and app crashes from the event logs and the crash dump
/// folders. Read-only: nothing on the PC is changed.
/// </summary>
public interface ICrashService
{
    /// <summary>The last scan, or null before the first one.</summary>
    CrashReport? Last { get; }

    /// <summary>Raised after every finished scan.</summary>
    event EventHandler<CrashReport>? Scanned;

    /// <summary>The folder Windows writes small memory dumps to (%SystemRoot%\Minidump).</summary>
    string MinidumpFolder { get; }

    /// <summary>Reads the last <see cref="CrashRules.WindowDays"/> days, newest first. Runs off the UI thread.</summary>
    Task<CrashReport> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// A quick count of blue screens and sudden shutdowns in the last <see cref="CrashRules.AttentionDays"/> days, for the
    /// Dashboard. Crashes marked as solved on the Crashes page are left out.
    /// </summary>
    Task<CrashGlance> GetGlanceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What happened around the crashes: PC Checker's History samples in the minutes before each one, the Action
    /// Journal and the Windows Update history. Read-only, off the UI thread; a source that cannot be read is left empty.
    /// </summary>
    Task<CrashContextData> LoadContextAsync(IReadOnlyList<CrashEvent> crashes, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up a driver a crash is blamed on (for example nvlddmkm.sys): its file, maker, signer, version, date and the
    /// device it runs. Null when the file cannot be found. Read-only.
    /// </summary>
    Task<CrashDriverDetails?> InspectDriverAsync(string module, CancellationToken cancellationToken);
}

/// <summary>The data <see cref="ICrashService.LoadContextAsync"/> returns.</summary>
public sealed record CrashContextData(
    IReadOnlyList<History.HistorySample> Samples,
    IReadOnlyList<Logging.ActionRecord> Journal,
    IReadOnlyList<UpdateInstallRecord> Updates)
{
    public static CrashContextData Empty { get; } = new([], [], []);
}
