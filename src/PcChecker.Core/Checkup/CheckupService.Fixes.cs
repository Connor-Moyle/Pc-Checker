using Microsoft.Extensions.Logging;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Fans;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.Core.Checkup;

/// <summary>
/// Applies the ticked fixes through the owning services, one step after another, inside one change batch so the steps
/// share a single restore point. Every service still runs its own change through the change guard (journal, log, its
/// own safety checks); this class only decides the order and collects one result per item.
/// </summary>
public sealed partial class CheckupService
{
    private const string FixRunName = "Fixing";

    public async Task<CheckupFixReport> FixAsync(CheckupReport report, CheckupFixPlan plan, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<CheckupFixResult>();
        results.AddRange(plan.Skipped.Select(s => new CheckupFixResult(s.Item, false, "Skipped: " + s.Reason)));
        if (plan.IsEmpty)
        {
            return new CheckupFixReport(results, ChangeBatchOutcome.None, false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool cancelled = false;
        ChangeBatchOutcome batch = ChangeBatchOutcome.None;
        try
        {
            var description = plan.ItemCount == 1 ? "Check-up fix" : $"Check-up fixes ({plan.ItemCount} items)";
            batch = await _batch.RunBatchAsync(description, plan.SharedRestorePoint, async ct =>
            {
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    var step = plan.Steps[i];
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        results.AddRange(plan.Steps.Skip(i).SelectMany(s => s.Items).Select(item => new CheckupFixResult(item, false, "Not started: cancelled.")));
                        return;
                    }

                    int index = i;
                    var stepProgress = new InlineProgress(p => progress?.Report(CheckupProgress.Combine(FixRunName, index, plan.Steps.Count, StepName(step.Kind), p)));
                    progress?.Report(CheckupProgress.Combine(FixRunName, index, plan.Steps.Count, StepName(step.Kind), null));
                    try
                    {
                        // Driver installs and removals always run to the end once started, as on their own pages.
                        results.AddRange(await RunStepAsync(report, step, stepProgress, step.CanStopPartWay ? ct : CancellationToken.None).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Only the user's cancel counts; a service's own time-out falls through to the failure below.
                        cancelled = true;
                        results.AddRange(step.Items.Concat(plan.Steps.Skip(i + 1).SelectMany(s => s.Items))
                            .Where(item => results.All(r => r.Item.Id != item.Id))
                            .Select(item => new CheckupFixResult(item, false, "Not finished: cancelled.")));
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Check-up fix step {Step} failed.", step.Kind);
                        results.AddRange(step.Items.Select(item => new CheckupFixResult(item, false, ex.Message)));
                    }
                }

                // A step that stops between its own items (startup apps, optimizations, updates) reports the rest itself.
                cancelled |= ct.IsCancellationRequested;
            }, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled while the shared restore point was being made: nothing was changed.
            cancelled = true;
            results.AddRange(plan.Steps.SelectMany(s => s.Items).Where(item => results.All(r => r.Item.Id != item.Id))
                .Select(item => new CheckupFixResult(item, false, "Not started: cancelled.")));
        }
        finally
        {
            _gate.Release();
        }

        var fixReport = new CheckupFixReport(results, batch, cancelled);
        if (fixReport.Succeeded > 0 && Last is { } last && last.Captured == report.Captured)
        {
            // The Dashboard reads Last: drop what was fixed so it does not keep showing installed updates as waiting.
            Last = CheckupAfterFix.Apply(last, fixReport);
            LastChanged?.Invoke(this, Last);
        }

        _logger.LogInformation("Check-up fixes finished: {Ok} done, {Failed} not done{Cancelled}.", fixReport.Succeeded, fixReport.Failed,
            cancelled ? " (cancelled)" : string.Empty);
        return fixReport;
    }

    /// <summary>The name shown in progress for a step.</summary>
    public static string StepName(CheckupFixKind kind) => kind switch
    {
        CheckupFixKind.CleanSafeJunk => "Cleaning temporary files",
        CheckupFixKind.RemoveOldDrivers => "Removing old drivers",
        CheckupFixKind.DisableStartupItem => "Turning off startup apps",
        CheckupFixKind.ApplyOptimization => "Applying optimizations",
        CheckupFixKind.ApplyRecommendedSettings => "Applying recommended settings",
        CheckupFixKind.InstallDriverUpdates => "Installing driver updates",
        CheckupFixKind.InstallWindowsUpdates => "Installing Windows updates",
        CheckupFixKind.InstallPawnIo => "Installing the PawnIO driver",
        _ => "Working",
    };

    private Task<IReadOnlyList<CheckupFixResult>> RunStepAsync(CheckupReport report, CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct) =>
        step.Kind switch
        {
            CheckupFixKind.CleanSafeJunk => CleanAsync(report, step, progress, ct),
            CheckupFixKind.RemoveOldDrivers => RemoveOldDriversAsync(report, step, progress, ct),
            CheckupFixKind.DisableStartupItem => DisableStartupItemsAsync(report, step, ct),
            CheckupFixKind.ApplyOptimization => ApplyOptimizationsAsync(step, ct),
            CheckupFixKind.ApplyRecommendedSettings => ApplyRecommendedSettingsAsync(step, progress, ct),
            CheckupFixKind.InstallDriverUpdates => InstallDriverUpdatesAsync(report, step, progress, ct),
            CheckupFixKind.InstallWindowsUpdates => InstallWindowsUpdatesAsync(report, step, progress, ct),
            CheckupFixKind.InstallPawnIo => InstallPawnIoAsync(step, progress, ct),
            _ => Task.FromResult<IReadOnlyList<CheckupFixResult>>(step.Items.Select(i => new CheckupFixResult(i, false, "Nothing to do here.")).ToList()),
        };

    private static IReadOnlyList<CheckupFixResult> AllItems(CheckupFixStep step, bool success, string message, bool reboot = false) =>
        step.Items.Select(i => new CheckupFixResult(i, success, message, reboot)).ToList();

    /// <summary>Deletes the scanned junk, but only in categories that are still rated Safe (never Windows.old).</summary>
    private async Task<IReadOnlyList<CheckupFixResult>> CleanAsync(CheckupReport report, CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        var safe = _scanner.Categories.Where(c => c.Risk == RiskLevel.Safe && !CleanupRules.IsReportOnly(c.Id)).Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = step.Targets.Where(safe.Contains).ToList();
        if (report.Results.Cleanup is not { } scan || ids.Count == 0)
        {
            return AllItems(step, false, "Nothing to clean. Run the check-up again.");
        }

        var run = await _cleaner.CleanAsync(scan, ids, dryRun: false, progress, ct).ConfigureAwait(false);
        return AllItems(step, true, CleanupRules.Summarize(run));
    }

    private async Task<IReadOnlyList<CheckupFixResult>> RemoveOldDriversAsync(CheckupReport report, CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        if (report.Results.OldDrivers is not { } scan)
        {
            return AllItems(step, false, "The old driver scan is missing. Run the check-up again.");
        }

        var keys = step.Targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = scan.Items.Where(i => keys.Contains(CheckupItemMapper.OldDriverKey(i)) && i.Status == DriverCleanupStatus.SafeToRemove).ToList();
        var removal = DriverCleanupRules.BuildRemovalPlan(selected, scan.Items, advancedMode: false);
        if (removal.Count == 0)
        {
            return AllItems(step, false, "None of these drivers can be removed any more.");
        }

        var result = await _driverCleanup.RemoveAsync(removal, progress, ct).ConfigureAwait(false);
        if (result.Value is { } value)
        {
            return AllItems(step, result.Success && value.Failed == 0, value.Summary, value.RebootRequired);
        }

        return AllItems(step, false, result.Message);
    }

    /// <summary>
    /// Runs a step whose items are separate calls. Cancelling stops between items: the finished ones keep their real
    /// result, the one that was interrupted and the ones not started say so.
    /// </summary>
    private static async Task<IReadOnlyList<CheckupFixResult>> RunEachAsync(CheckupFixStep step, Func<CheckupItem, Task<CheckupFixResult>> fix, CancellationToken ct)
    {
        var results = new List<CheckupFixResult>();
        foreach (var item in step.Items)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new CheckupFixResult(item, false, "Not started: cancelled."));
                continue;
            }

            try
            {
                results.Add(await fix(item).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results.Add(new CheckupFixResult(item, false, "Not finished: cancelled."));
            }
        }

        return results;
    }

    private Task<IReadOnlyList<CheckupFixResult>> DisableStartupItemsAsync(CheckupReport report, CheckupFixStep step, CancellationToken ct) =>
        RunEachAsync(step, async item =>
        {
            var key = item.Targets.FirstOrDefault();
            var entry = report.Results.Startup?.FirstOrDefault(e => string.Equals(CheckupItemMapper.StartupKey(e.Item), key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return new CheckupFixResult(item, false, "This startup app is no longer listed.");
            }

            var outcome = await _startup.SetStartupItemEnabledAsync(entry.Item, false, ct).ConfigureAwait(false);
            return new CheckupFixResult(item, outcome.Success,
                outcome.Success ? $"{entry.Item.Name} no longer starts with Windows. Turn it back on from the Startup page." : outcome.Message, outcome.RebootRequired);
        }, ct);

    /// <summary>Applies each optimization on its own; only reversible actions that exist in this version are run.</summary>
    private Task<IReadOnlyList<CheckupFixResult>> ApplyOptimizationsAsync(CheckupFixStep step, CancellationToken ct)
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return RunEachAsync(step, async item =>
        {
            var id = item.Targets.FirstOrDefault();
            var action = _optimization.Actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (action is null || !action.Reversible || action.Id.Equals(OptimizationRisk.NavigationOnlyActionId, StringComparison.OrdinalIgnoreCase))
            {
                return new CheckupFixResult(item, false, "This optimization is not available here. Open the Optimize page.");
            }

            if (!applied.Add(action.Id))
            {
                return new CheckupFixResult(item, true, $"Done together with an earlier item ({action.Title}).");
            }

            var outcome = await _optimization.ApplyAsync(action.Id, ct).ConfigureAwait(false);
            return new CheckupFixResult(item, outcome.Success, $"{action.Title}: {outcome.Message}", outcome.Success && (outcome.RebootRequired || action.RequiresRestart));
        }, ct);
    }

    private async Task<IReadOnlyList<CheckupFixResult>> ApplyRecommendedSettingsAsync(CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        var safe = _debloat.Tweaks.Where(t => t.Risk == RiskLevel.Safe && !t.AdvancedOnly).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = step.Targets.Where(safe.Contains).ToList();
        if (ids.Count == 0)
        {
            return AllItems(step, false, "None of these settings can be applied from here. Open the Debloat page.");
        }

        var run = await _debloat.ApplyAsync(ids, progress, ct).ConfigureAwait(false);
        return AllItems(step, run.Failed == 0 && run.Succeeded > 0, run.Summary, run.RebootRequired);
    }

    /// <summary>Downloads what is not on disk yet, then installs what arrived, as the Drivers page does.</summary>
    private async Task<IReadOnlyList<CheckupFixResult>> InstallDriverUpdatesAsync(CheckupReport report, CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        var ids = step.Targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = report.Results.DriverUpdates?.Updates.Where(u => ids.Contains(u.Id) && !u.IsInstalled).ToList() ?? [];
        if (updates.Count == 0)
        {
            return AllItems(step, false, "These driver updates are no longer offered. Run the check-up again.");
        }

        var pending = updates.Where(u => !u.IsDownloaded).ToList();
        if (pending.Count > 0)
        {
            var download = await _drivers.DownloadAsync(pending, progress, ct).ConfigureAwait(false);
            var downloaded = await DownloadedIdsAsync(pending, download.Success, ct).ConfigureAwait(false);
            updates = updates.Select(u => downloaded.Contains(u.Id) ? u with { IsDownloaded = true } : u).ToList();
        }

        var ready = updates.Where(u => u.IsDownloaded).ToList();
        if (ready.Count == 0)
        {
            return AllItems(step, false, "None of the driver updates finished downloading, so nothing was installed.");
        }

        var result = await _drivers.InstallAsync(ready, progress, ct).ConfigureAwait(false);
        int ok = result.Results.Count(r => r.Result.Success);
        var text = $"{ok} of {updates.Count} driver update(s) installed." + (result.RebootRequired ? " Restart Windows to finish." : string.Empty);
        var failures = result.Results.Where(r => !r.Result.Success).Select(r => r.Update.Title + ": " + r.Result.Message).ToList();
        if (failures.Count > 0)
        {
            text += " Not installed: " + string.Join("; ", failures);
        }

        return AllItems(step, ok == updates.Count, text, result.RebootRequired);
    }

    private async Task<IReadOnlySet<string>> DownloadedIdsAsync(IReadOnlyList<DriverUpdateInfo> updates, bool overallSuccess, CancellationToken ct)
    {
        if (_drivers is IDriverUpdateStateProvider state)
        {
            try
            {
                return await state.GetDownloadedUpdateIdsAsync(updates.Select(u => u.Id), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Downloaded driver updates could not be read; using the overall download result.");
            }
        }

        return overallSuccess ? updates.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
    }

    /// <summary>
    /// Installs PawnIO, then reloads the sensors (fans back to automatic first) so the motherboard fans appear without
    /// restarting the app, and says how many showed up.
    /// </summary>
    private async Task<IReadOnlyList<CheckupFixResult>> InstallPawnIoAsync(CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        if (_pawnIo is null)
        {
            return AllItems(step, false, "Installing PawnIO is not available here. Open the Fans page.");
        }

        var result = await _pawnIo.InstallAsync(progress, ct).ConfigureAwait(false);
        string message = result.Message;
        if (result.Success && result.After.IsRunning && _fans is IFanDiagnostics diagnostics)
        {
            try
            {
                progress.Report(new ProgressUpdate("Reloading sensors"));
                var reload = await diagnostics.ReloadSensorsAsync(ct).ConfigureAwait(false);
                message += " " + reload.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Reloading sensors after the PawnIO install failed.");
                message += " Open the Fans page and click \"Reload sensors\" to read the motherboard fans.";
            }
        }

        return AllItems(step, result.Success, message, result.RebootRequired);
    }

    /// <summary>Installs the recommended updates (never optional ones or ones that need Settings), one at a time.</summary>
    private async Task<IReadOnlyList<CheckupFixResult>> InstallWindowsUpdatesAsync(CheckupReport report, CheckupFixStep step, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        var ids = step.Targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = UpdateInstallSummary.InstallAllSelection(report.Results.PendingUpdates?.Updates ?? []).Where(u => ids.Contains(u.Id)).ToList();
        if (updates.Count == 0)
        {
            return AllItems(step, false, "These updates are no longer waiting. Run the check-up again.");
        }

        var result = await _updates.InstallAsync(updates, progress, ct).ConfigureAwait(false);
        if (result.Value is { } value)
        {
            var text = UpdateInstallSummary.Summarize(value);
            var failed = value.Items.Where(i => !i.Success).Select(i => $"{i.Update.Title}: {i.Message}").ToList();
            if (failed.Count > 0)
            {
                text += " " + string.Join("; ", failed);
            }

            return AllItems(step, result.Success && value.Failed == 0 && value.Installed > 0, text, value.RebootRequired);
        }

        return AllItems(step, false, result.Message);
    }
}
