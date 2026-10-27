using PcChecker.Core.Drivers;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Turns raw Windows Update history records (<c>IUpdateSearcher.QueryHistory</c>) into the entries the History tab
/// shows: installs only, newest first, with the result in words and failures explained. Pure.
/// </summary>
public static class UpdateHistoryMapper
{
    /// <summary>The page shows this many installs, like Settings → Update history.</summary>
    public const int DefaultLimit = 100;

    /// <summary><c>UpdateOperation</c>: 1 = installation, 2 = uninstallation.</summary>
    public const int OperationInstallation = 1;

    /// <summary>Dates before this are placeholders written by the Windows Update Agent, not real install times.</summary>
    private static readonly DateTime EarliestRealDate = new(1995, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Maps, filters and sorts the records, newest first, and keeps at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<UpdateHistoryEntry> MapAll(IEnumerable<UpdateHistoryRecord> records, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .Select(Map)
            .OfType<UpdateHistoryEntry>()
            .OrderByDescending(e => e.Date ?? DateTimeOffset.MinValue)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <summary>One record as an entry, or null for uninstalls and entries without a title.</summary>
    public static UpdateHistoryEntry? Map(UpdateHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Operation != OperationInstallation || string.IsNullOrWhiteSpace(record.Title))
        {
            return null;
        }

        var title = record.Title.Trim();
        var outcome = MapOutcome(record.ResultCode);
        string? error = outcome is UpdateHistoryOutcome.Failed or UpdateHistoryOutcome.Cancelled or UpdateHistoryOutcome.SucceededWithErrors
            ? ExplainFailure(outcome, record.HResult)
            : null;

        return new UpdateHistoryEntry(
            title,
            UpdateClassifier.ExtractKb(title),
            ToDate(record.DateUtc),
            outcome,
            record.HResult,
            UpdateClassifier.Classify(record.CategoryIds, title),
            OutcomeText(outcome),
            error);
    }

    /// <summary>Maps <c>OperationResultCode</c> (0 not started … 5 aborted).</summary>
    public static UpdateHistoryOutcome MapOutcome(int resultCode) => resultCode switch
    {
        WuaResultCodes.Succeeded => UpdateHistoryOutcome.Succeeded,
        WuaResultCodes.SucceededWithErrors => UpdateHistoryOutcome.SucceededWithErrors,
        WuaResultCodes.Failed => UpdateHistoryOutcome.Failed,
        WuaResultCodes.Aborted => UpdateHistoryOutcome.Cancelled,
        WuaResultCodes.InProgress => UpdateHistoryOutcome.InProgress,
        _ => UpdateHistoryOutcome.Unknown,
    };

    public static string OutcomeText(UpdateHistoryOutcome outcome) => outcome switch
    {
        UpdateHistoryOutcome.Succeeded => "Installed",
        UpdateHistoryOutcome.SucceededWithErrors => "Installed with errors",
        UpdateHistoryOutcome.Failed => "Failed",
        UpdateHistoryOutcome.Cancelled => "Cancelled",
        UpdateHistoryOutcome.InProgress => "In progress",
        _ => "Unknown",
    };

    /// <summary>The newest successful install, or null when there is none.</summary>
    public static DateTimeOffset? LastSuccessfulInstall(IEnumerable<UpdateHistoryEntry> entries) =>
        entries.Where(e => (e.Outcome is UpdateHistoryOutcome.Succeeded or UpdateHistoryOutcome.SucceededWithErrors) && e.Date is not null)
            .Select(e => e.Date)
            .Max();

    /// <summary>The plain-English reason for a failed entry; an unknown code gets the code plus where to look it up.</summary>
    public static string ExplainFailure(UpdateHistoryOutcome outcome, int hresult)
    {
        if (hresult == 0)
        {
            return outcome == UpdateHistoryOutcome.Cancelled
                ? "The install was stopped before it finished, for example by a restart or shutdown."
                : "Windows Update did not record a reason.";
        }

        return WuaResultCodes.Explain(hresult);
    }

    private static DateTimeOffset? ToDate(DateTime? value)
    {
        if (value is not { } date)
        {
            return null;
        }

        var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);
        return utc < EarliestRealDate ? null : new DateTimeOffset(utc);
    }
}
