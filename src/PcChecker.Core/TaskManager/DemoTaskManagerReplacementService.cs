using PcChecker.Core.Common;

namespace PcChecker.Core.TaskManager;

/// <summary>Demo version: the option looks available and toggles an in-memory flag; nothing in the registry changes.</summary>
public sealed class DemoTaskManagerReplacementService : ITaskManagerReplacementService
{
    private bool _enabled;

    public TaskManagerReplacementStatus GetStatus() =>
        new(_enabled, OwnedByUs: _enabled, Eligible: true, IneligibleReason: null, PointsElsewhere: false,
            CurrentValue: _enabled ? "\"C:\\Program Files\\PC Checker\\PcChecker.exe\" --taskmgr" : null);

    public Task<OperationResult> EnableAsync(CancellationToken cancellationToken)
    {
        _enabled = true;
        return Task.FromResult(OperationResult.Ok("Ctrl+Shift+Esc now opens PC Checker on the Processes tab (demo)."));
    }

    public Task<OperationResult> DisableAsync(CancellationToken cancellationToken)
    {
        _enabled = false;
        return Task.FromResult(OperationResult.Ok("Windows Task Manager opens again (demo)."));
    }

    public Task<OperationResult> DisableAndOpenWindowsTaskManagerAsync(CancellationToken cancellationToken)
    {
        _enabled = false;
        return Task.FromResult(OperationResult.Ok("Turned the option off and opened Windows Task Manager (demo)."));
    }

    public Task<string?> ReconcileOnStartAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
