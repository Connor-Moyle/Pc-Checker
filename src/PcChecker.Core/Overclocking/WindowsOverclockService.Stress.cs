using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Overclocking;

/// <summary>Crash-watchdog half of <see cref="WindowsOverclockService"/> (the stress test lives in WindowsOverclockService.GpuStress.cs).</summary>
public sealed partial class WindowsOverclockService
{
    /// <summary>
    /// Runs the crash watchdog at most once per process: concurrent or repeated callers share the first run's result,
    /// so an unclean session is detected, reset and reported exactly once. A cancelled run may be retried.
    /// </summary>
    public Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken)
    {
        lock (_watchdogLock)
        {
            if (_watchdogRun is null || _watchdogRun.IsCanceled)
            {
                _watchdogRun = CheckUncleanShutdownAndRevertCoreAsync(cancellationToken);
            }

            return _watchdogRun;
        }
    }

    private async Task<OperationResult> CheckUncleanShutdownAndRevertCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        var session = OverclockSessionStore.Load(SessionPath);
        if (session is null)
        {
            if (!_settings.Current.Overclock.LastSessionClean)
            {
                _settings.Current.Overclock.LastSessionClean = true;
                _settings.Current.Overclock.ActiveProfileName = null;
                await _settings.SaveAsync().ConfigureAwait(false);
            }

            return OperationResult.Ok("No overclock session to recover.");
        }

        if (session.Clean && OverclockSessionStore.IsFromCurrentBoot(session, OverclockSessionStore.CurrentBootUtc()))
        {
            // The app was closed and reopened without a reboot: the clean exit left the tuning in place, so it is
            // still live. Keep the session (and its XTU ids) so the page, Reset and the stress-test revert know about it.
            _applied = session.Profile;
            session.Clean = false;
            OverclockSessionStore.Save(SessionPath, session);
            _settings.Current.Overclock.ActiveProfileName = session.Profile.Name;
            _settings.Current.Overclock.LastSessionClean = false;
            await _settings.SaveAsync().ConfigureAwait(false);
            return OperationResult.Ok($"The overclock profile \"{session.Profile.Name}\" is still applied from earlier in this Windows session.");
        }

        if (session.Clean)
        {
            // The PC has rebooted since: profiles never survive a reboot (driver deltas are volatile); start fresh.
            OverclockSessionStore.Delete(SessionPath);
            _settings.Current.Overclock.ActiveProfileName = null;
            _settings.Current.Overclock.LastSessionClean = true;
            await _settings.SaveAsync().ConfigureAwait(false);
            return OperationResult.Ok("The last session ended cleanly.");
        }

        _logger.LogWarning("Unclean shutdown detected with overclock profile \"{Profile}\" applied at {When}; reverting.", session.Profile.Name, session.AppliedUtc);
        try
        {
            if (Capabilities is null)
            {
                await DetectAsync(cancellationToken).ConfigureAwait(false);
            }

            _applied = session.Profile;
            // Guarded like every other overclock change (no restore point: nothing new is applied), so the journal
            // shows what the watchdog reverted.
            var options = new ChangeOptions(false, false, ModuleName, Capabilities?.GpuName ?? "System", false, null);
            var reset = await _guard.RunGuardedAsync($"Revert overclock profile \"{session.Profile.Name}\" after an unclean shutdown", options, ResetCoreAsync, null, cancellationToken).ConfigureAwait(false);
            OverclockSessionStore.Delete(SessionPath);
            return reset.Success
                ? OperationResult.Ok($"The previous overclock (\"{session.Profile.Name}\") was reverted after an unclean shutdown. {reset.Message}")
                : OperationResult.Ok($"The previous session ended uncleanly while \"{session.Profile.Name}\" was applied. Driver clock offsets do not survive a reboot, so the card is at stock; the explicit reset reported: {reset.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reverting after an unclean shutdown failed.");
            OverclockSessionStore.Delete(SessionPath);
            return OperationResult.Fail("The previous overclock session ended uncleanly and the automatic reset failed: " + ex.Message, ex);
        }
    }

    public async Task MarkSessionCleanAsync()
    {
        try
        {
            var session = OverclockSessionStore.Load(SessionPath);
            if (session is not null && !session.Clean)
            {
                session.Clean = true;
                OverclockSessionStore.Save(SessionPath, session);
            }

            if (!_settings.Current.Overclock.LastSessionClean)
            {
                _settings.Current.Overclock.LastSessionClean = true;
                await _settings.SaveAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark the overclock session clean.");
        }
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
