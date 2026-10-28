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

    /// <summary>A quick count of blue screens and sudden shutdowns in the last <see cref="CrashRules.AttentionDays"/> days, for the Dashboard.</summary>
    Task<CrashGlance> GetGlanceAsync(CancellationToken cancellationToken);
}
