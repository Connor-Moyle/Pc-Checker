using PcChecker.Core.Common;

namespace PcChecker.Core.Startup;

/// <summary>Schedules and cancels a plain Windows restart using the shared <see cref="RestartPrompt"/> countdown.</summary>
public interface IRestartScheduler
{
    /// <summary>Schedules a restart in <see cref="RestartPrompt.CountdownSeconds"/> seconds. <paramref name="reason"/> reads "to …".</summary>
    Task<OperationResult> ScheduleAsync(string reason, CancellationToken cancellationToken);

    /// <summary>Aborts a pending restart (shutdown /a). Succeeds when there was nothing to cancel.</summary>
    Task<OperationResult> CancelAsync(CancellationToken cancellationToken);
}

/// <summary>Real restarts through shutdown.exe.</summary>
public sealed class WindowsRestartScheduler : IRestartScheduler
{
    public async Task<OperationResult> ScheduleAsync(string reason, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Restarting is only available on Windows.");
        }

        var result = await CommandRunner.RunAsync("shutdown.exe", RestartPrompt.RestartArguments(reason), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok($"Windows restarts in {RestartPrompt.CountdownSeconds} seconds {reason.Trim().TrimEnd('.')}. Save your work now.", rebootRequired: true)
            : OperationResult.Fail($"Could not schedule the restart: {result.FirstLine}");
    }

    public async Task<OperationResult> CancelAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Restarting is only available on Windows.");
        }

        var result = await CommandRunner.RunAsync("shutdown.exe", RestartPrompt.CancelArguments, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return OperationResult.Ok("The restart was cancelled.");
        }

        // 1116: no shutdown in progress, nothing to cancel.
        return result.ExitCode == 1116
            ? OperationResult.Ok("There was no restart to cancel.")
            : OperationResult.Fail($"Could not cancel the restart: {result.FirstLine}. Run 'shutdown /a' in a command prompt right away.");
    }
}

/// <summary>Demo mode: never restarts the PC.</summary>
public sealed class DemoRestartScheduler : IRestartScheduler
{
    private bool _pending;

    public Task<OperationResult> ScheduleAsync(string reason, CancellationToken cancellationToken)
    {
        _pending = true;
        return Task.FromResult(OperationResult.Ok($"Would restart in {RestartPrompt.CountdownSeconds} seconds {reason.Trim().TrimEnd('.')} (demo).", rebootRequired: true));
    }

    public Task<OperationResult> CancelAsync(CancellationToken cancellationToken)
    {
        bool had = _pending;
        _pending = false;
        return Task.FromResult(OperationResult.Ok(had ? "The restart was cancelled (demo)." : "There was no restart to cancel (demo)."));
    }
}
