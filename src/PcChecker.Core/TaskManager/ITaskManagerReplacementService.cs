using PcChecker.Core.Common;

namespace PcChecker.Core.TaskManager;

/// <summary>What the "Replace Task Manager" option currently is.</summary>
public sealed record TaskManagerReplacementStatus(
    bool Enabled,
    bool OwnedByUs,
    bool Eligible,
    string? IneligibleReason,
    bool PointsElsewhere,
    string? CurrentValue)
{
    /// <summary>A disabled, eligible, nothing-set starting point (used in demo mode and off Windows).</summary>
    public static TaskManagerReplacementStatus Off { get; } = new(false, false, true, null, false, null);
}

/// <summary>
/// Turns the "open PC Checker instead of Windows Task Manager" option on and off through the Image File Execution
/// Options Debugger value, the way Process Explorer does. Only allowed from a Program Files install. Every change
/// goes through the change guard and stores the previous value so it can be undone.
/// </summary>
public interface ITaskManagerReplacementService
{
    /// <summary>Reads the current state: on/off, whether PC Checker owns the value, and whether it is allowed here.</summary>
    TaskManagerReplacementStatus GetStatus();

    /// <summary>Turns the option on. Fails with a plain reason when PC Checker is not in an install location.</summary>
    Task<OperationResult> EnableAsync(CancellationToken cancellationToken);

    /// <summary>Turns the option off, restoring any value that was there before PC Checker set it.</summary>
    Task<OperationResult> DisableAsync(CancellationToken cancellationToken);

    /// <summary>Turns the option off and then opens the real Windows Task Manager.</summary>
    Task<OperationResult> DisableAndOpenWindowsTaskManagerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checked at every start: removes a stale value that pointed at a PC Checker that no longer exists, and reports
    /// when the value points at a different copy so the app can offer to fix it. Returns a note when it acted, else null.
    /// </summary>
    Task<string?> ReconcileOnStartAsync(CancellationToken cancellationToken);
}
