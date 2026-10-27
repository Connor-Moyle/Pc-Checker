using PcChecker.Core.Common;

namespace PcChecker.Core.Checkup;

/// <summary>
/// The Home page check-up: runs the scans of the Health, Cleanup, Drivers, Windows Update, Startup, Speed check, Debloat
/// and Old drivers pages through their own services, turns the results into one ranked to-do list, and applies the fixes
/// the user ticks through the same services (each one guarded, all sharing one restore point when possible).
/// </summary>
public interface ICheckupService
{
    /// <summary>The last finished check-up in this session, or null.</summary>
    CheckupReport? Last { get; }

    /// <summary>Raised (on any thread) after a check-up finishes.</summary>
    event EventHandler<CheckupReport>? Completed;

    /// <summary>Runs every part one after another. A part that fails is listed in the results' gaps; the rest still run.</summary>
    Task<CheckupReport> RunAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="plan"/> (from <see cref="CheckupBatchPlanner"/>) to what <paramref name="report"/> found,
    /// step by step. Never throws for a failed fix; each item gets its own result. Cancelling stops before the next step
    /// (a driver install or removal that has started always finishes).
    /// </summary>
    Task<CheckupFixReport> FixAsync(CheckupReport report, CheckupFixPlan plan, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
