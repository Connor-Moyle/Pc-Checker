using PcChecker.Core.Common;

namespace PcChecker.Core.TaskManager;

/// <summary>What the "Replace Task Manager" option currently is.</summary>
/// <param name="Enabled">The value points at this PcChecker.exe.</param>
/// <param name="OwnedByUs">Same as <paramref name="Enabled"/>: the value is this copy's.</param>
/// <param name="Eligible">PC Checker runs from a protected install folder, so the option may be turned on.</param>
/// <param name="IneligibleReason">Plain reason when not eligible.</param>
/// <param name="PointsElsewhere">The value points at another PC Checker copy that still exists (offer to repoint).</param>
/// <param name="CurrentValue">The raw Debugger value, or null.</param>
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

    /// <summary>How the current value was classified.</summary>
    public DebuggerValueKind Kind { get; init; } = DebuggerValueKind.None;

    /// <summary>The program the current value starts (the other PC Checker copy or the third-party tool), or null.</summary>
    public string? CurrentTarget => TaskManagerReplacement.ParseDebuggerValue(CurrentValue)?.ExePath;

    /// <summary>Another program (not PC Checker) is set in Task Manager's place; turning the option on replaces it.</summary>
    public bool ThirdPartyPresent => Kind == DebuggerValueKind.ThirdParty;
}

/// <summary>What the start-up check found and did.</summary>
/// <param name="Note">A plain note to show when the check cleaned something up, else null.</param>
/// <param name="OtherCopyPath">Set when the value points at a different PC Checker copy that still exists, so the app can offer to point it at this one.</param>
public sealed record TaskManagerStartCheck(string? Note, string? OtherCopyPath)
{
    public static TaskManagerStartCheck Nothing { get; } = new(null, null);
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

    /// <summary>
    /// Turns the option on, or points a value left by another PC Checker copy at this one. A third-party value is saved
    /// first so turning the option off puts it back. Fails with a plain reason when PC Checker is not in an install location.
    /// </summary>
    Task<OperationResult> EnableAsync(CancellationToken cancellationToken);

    /// <summary>Turns the option off, restoring any value that was there before PC Checker set it.</summary>
    Task<OperationResult> DisableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Turns the option off and then opens the real Windows Task Manager. While the option is on, Windows starts PC
    /// Checker for every launch of taskmgr.exe, so this is the only way to reach Windows Task Manager.
    /// </summary>
    Task<OperationResult> DisableAndOpenWindowsTaskManagerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checked at every start: removes a stale value that pointed at a PC Checker that no longer exists, and reports
    /// when the value points at a different copy so the app can offer to fix it.
    /// </summary>
    Task<TaskManagerStartCheck> ReconcileOnStartAsync(CancellationToken cancellationToken);
}
