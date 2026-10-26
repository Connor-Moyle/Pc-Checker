using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Restore;

/// <summary>Default <see cref="IChangeGuard"/>: restore point (per settings) → change → journal + log. Never throws for a failed change.</summary>
public sealed class ChangeGuard : IChangeGuard
{
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
        bool before = options.CreateBefore ?? policy.CreateBeforeChanges;
        bool after = options.CreateAfter ?? policy.CreateAfterChanges;
        long? sequence = null;

        _logger.LogInformation("Starting change: {Description} ({Module} → {Target})", description, options.Module, options.Target);

        if (before)
        {
            sequence = await TryCreateRestorePointAsync($"PC Checker: before {description}", progress, cancellationToken).ConfigureAwait(false);
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
            _logger.LogWarning("Change cancelled: {Description}", description);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change failed with an exception: {Description}", description);
            await RecordAsync(options, description, "Failed: " + ex.Message, sequence).ConfigureAwait(false);
            return OperationResult<T>.Fail($"{description} failed: {ex.Message}", ex);
        }

        if (result.Success)
        {
            _logger.LogInformation("Change succeeded: {Description} — {Message}", description, result.Message);
        }
        else
        {
            _logger.LogWarning("Change did not succeed: {Description} — {Message}", description, result.Message);
        }

        if (after && result.Success)
        {
            await TryCreateRestorePointAsync($"PC Checker: after {description}", progress, cancellationToken).ConfigureAwait(false);
        }

        await RecordAsync(options, description, result.Success ? "Succeeded: " + result.Message : "Failed: " + result.Message, sequence).ConfigureAwait(false);
        return result;
    }

    private async Task<long?> TryCreateRestorePointAsync(string description, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report(new ProgressUpdate("Creating a restore point", null, description));
            var rp = await _restorePoints.CreateAsync(description, cancellationToken).ConfigureAwait(false);
            if (rp.Success && rp.Value is not null)
            {
                _logger.LogInformation("Restore point {Sequence} created: {Description}", rp.Value.SequenceNumber, description);
                return rp.Value.SequenceNumber;
            }

            _logger.LogWarning("Restore point was not created: {Message}", rp.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restore point creation threw; continuing without one.");
        }

        return null;
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
