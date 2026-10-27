namespace PcChecker.Core.Checkup;

/// <summary>
/// What a check-up report looks like after some of its fixes were applied: the fixed items leave the to-do list, and
/// Windows updates that were installed leave the pending update list, so summaries built from the report (the
/// Dashboard's Windows Update tile and check-up strip) stay true until the next check-up. Pure.
/// </summary>
public static class CheckupAfterFix
{
    public static CheckupReport Apply(CheckupReport report, CheckupFixReport fixes)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(fixes);
        var fixedItems = fixes.Results.Where(r => r.Success).Select(r => r.Item).ToList();
        if (fixedItems.Count == 0)
        {
            return report;
        }

        var fixedIds = fixedItems.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = fixedItems.Where(i => i.Fix == CheckupFixKind.InstallWindowsUpdates)
            .SelectMany(i => i.Targets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = report.Results;
        if (installed.Count > 0 && results.PendingUpdates is { } pending)
        {
            results = results with { PendingUpdates = pending with { Updates = pending.Updates.Where(u => !installed.Contains(u.Id)).ToList() } };
        }

        return new CheckupReport(results, report.Items.Where(i => !fixedIds.Contains(i.Id)).ToList());
    }
}
