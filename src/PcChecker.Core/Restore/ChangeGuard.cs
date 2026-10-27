using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Restore;

/// <summary>
/// Default <see cref="IChangeGuard"/>: restore point (per settings) → change → journal + log. Never throws for a failed
/// change. When the "before" restore point fails the change still runs (with a note in the result) unless the options
/// ask for <see cref="RestorePointFailedBehavior.Abort"/>. Inside a batch (<see cref="IChangeBatchRunner"/>) a change
/// makes no restore point of its own and is journaled with the batch's.
/// </summary>
public sealed class ChangeGuard : IChangeGuard, IChangeBatchRunner
{
    /// <summary>Journal module of the entry that marks a batch and its shared restore point.</summary>
    public const string BatchModule = "Restore points";

    public const string RestorePointHint =
        "Turn on System Protection (Settings → Restore points) or switch off \"Create a restore point before making changes\" to continue.";

    private readonly IRestorePointService _restorePoints;
    private readonly IActionJournal _journal;
    private readonly ISettingsService _settings;
    private readonly ILogger<ChangeGuard> _logger;

    public ChangeGuard(IRestorePointService restorePoints, IActionJournal journal, ISettingsService settings, ILogger<ChangeGuard> logger)
    {
        _restorePoints = restorePoints;
        _journal = journal;
        _settings = settings;
        _logger = logger;
    }

    public async Task<OperationResult> RunGuardedAsync(string description, ChangeOptions options,
        Func<CancellationToken, Task<OperationResult>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var typed = await RunGuardedAsync<object?>(description, options,
            async ct => (await change(ct).ConfigureAwait(false)).WithValue<object?>(null), progress, cancellationToken).ConfigureAwait(false);
        return typed.AsUntyped();
    }

    public async Task<OperationResult<T>> RunGuardedAsync<T>(string description, ChangeOptions options,
        Func<CancellationToken, Task<OperationResult<T>>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(change);

        var policy = _settings.Current.RestorePoints;
        var batch = ChangeBatch.Current;
        var (before, after, sequence) = ChangeBatch.Resolve(options, policy.CreateBeforeChanges, policy.CreateAfterChanges, batch);
        string? restorePointWarning = null;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Starting change: {Description} ({Module} → {Target})", description, options.Module, options.Target);
        if (batch is not null)
        {
            _logger.LogInformation("Part of \"{Batch}\": using its restore point {Sequence}.", batch.Description, batch.RestorePointSequence);
        }

        if (before)
        {
            (sequence, restorePointWarning) = await TryCreateRestorePointAsync($"PC Checker: before {description}", progress, cancellationToken).ConfigureAwait(false);
            if (sequence is null && options.OnRestorePointFailed == RestorePointFailedBehavior.Abort)
            {
                var reason = restorePointWarning ?? "the restore point could not be created";
                var message = $"{description} was not started because {reason}. {RestorePointHint}";
                _logger.LogWarning("Change aborted: {Description} — {Reason}", description, reason);
                await RecordAsync(options, description, "Aborted: " + reason, null).ConfigureAwait(false);
                return OperationResult<T>.Fail(message);
            }
        }

        OperationResult<T> result;
        try
        {
            progress?.Report(new ProgressUpdate(description));
            result = await change(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(options, description, "Cancelled", sequence).ConfigureAwait(false);
            _logger.LogWarning("Change cancelled after {Elapsed:N1} s: {Description}", stopwatch.Elapsed.TotalSeconds, description);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change failed with an exception after {Elapsed:N1} s: {Description}", stopwatch.Elapsed.TotalSeconds, description);
            await RecordAsync(options, description, "Failed: " + ex.Message, sequence).ConfigureAwait(false);
            return OperationResult<T>.Fail($"{description} failed: {ex.Message}", ex);
        }

        if (result.Success)
        {
            _logger.LogInformation("Change succeeded in {Elapsed:N1} s: {Description} — {Message}", stopwatch.Elapsed.TotalSeconds, description, result.Message);
        }
        else
        {
            _logger.LogWarning("Change did not succeed after {Elapsed:N1} s: {Description} — {Message}", stopwatch.Elapsed.TotalSeconds, description, result.Message);
        }

        if (after && result.Success)
        {
            var (_, afterWarning) = await TryCreateRestorePointAsync($"PC Checker: after {description}", progress, cancellationToken).ConfigureAwait(false);
            restorePointWarning ??= afterWarning;
        }

        if (restorePointWarning is not null)
        {
            result = result with { Message = $"{result.Message} (Note: no restore point was created — {restorePointWarning}.)" };
        }

        var outcome = (result.Success ? "Succeeded: " : "Failed: ") + result.Message;
        await RecordAsync(options, description, outcome, sequence).ConfigureAwait(false);
        return result;
    }

    public async Task<ChangeBatchOutcome> RunBatchAsync(string description, bool createRestorePoint, Func<CancellationToken, Task> work,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(work);

        if (ChangeBatch.Current is { } outer)
        {
            await work(cancellationToken).ConfigureAwait(false);
            return new ChangeBatchOutcome(outer.RestorePointSequence, null, Joined: true);
        }

        if (!createRestorePoint)
        {
            await work(cancellationToken).ConfigureAwait(false);
            return ChangeBatchOutcome.None;
        }

        var (sequence, warning) = await TryCreateRestorePointAsync($"PC Checker: before {description}", progress, cancellationToken).ConfigureAwait(false);
        if (sequence is not { } shared)
        {
            // Without a shared point every change decides for itself, so one that needs a restore point still refuses.
            _logger.LogWarning("Batch \"{Description}\" runs without a shared restore point: {Warning}", description, warning);
            await work(cancellationToken).ConfigureAwait(false);
            return new ChangeBatchOutcome(null, warning);
        }

        bool finished = false;
        using (ChangeBatch.Enter(new ChangeBatchState(description, shared)))
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
                finished = true;
            }
            finally
            {
                await RecordAsync(ChangeOptions.For(BatchModule, description), description,
                    finished ? "Batch finished; each change is listed separately" : "Batch stopped early; finished changes are listed separately",
                    shared).ConfigureAwait(false);
            }
        }

        if (_settings.Current.RestorePoints.CreateAfterChanges)
        {
            var (_, afterWarning) = await TryCreateRestorePointAsync($"PC Checker: after {description}", progress, CancellationToken.None).ConfigureAwait(false);
            warning ??= afterWarning;
        }

        return new ChangeBatchOutcome(shared, warning);
    }

    /// <summary>Returns the new sequence number, or null plus a short reason when no point was created.</summary>
    private async Task<(long? Sequence, string? Warning)> TryCreateRestorePointAsync(string description, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report(new ProgressUpdate("Creating a restore point", null, description));
            var rp = await _restorePoints.CreateAsync(description, cancellationToken).ConfigureAwait(false);
            if (rp.Success && rp.Value is not null)
            {
                _logger.LogInformation("Restore point {Sequence} created: {Description}", rp.Value.SequenceNumber, description);
                return (rp.Value.SequenceNumber, null);
            }

            _logger.LogWarning("Restore point was not created: {Message}", rp.Message);
            return (null, rp.Message.TrimEnd('.'));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restore point creation threw; continuing without one.");
            return (null, ex.Message.TrimEnd('.'));
        }
    }

    private async Task RecordAsync(ChangeOptions options, string description, string outcome, long? sequence)
    {
        try
        {
            await _journal.RecordAsync(ActionRecord.Create(options.Module, description, options.Target, outcome,
                reversible: options.Reversible, undoHint: options.UndoHint, restorePointSequence: sequence)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write to the action journal.");
        }
    }
}
