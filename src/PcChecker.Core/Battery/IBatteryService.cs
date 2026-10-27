using PcChecker.Core.Common;

namespace PcChecker.Core.Battery;

/// <summary>
/// Reads the battery for the Battery page: capacities and cycles from WMI, the live charge state, and the capacity
/// history, recent usage and life estimates from Windows' battery report. Only reads; nothing on the PC changes.
/// </summary>
public interface IBatteryService
{
    /// <summary>The last loaded overview, or null before the first load.</summary>
    BatteryOverview? Last { get; }

    /// <summary>Where <see cref="SaveFullReportAsync"/> writes the HTML report.</summary>
    string ReportsFolder { get; }

    /// <summary>Reads everything. Never throws for a missing battery or an unreadable source; those become warnings.</summary>
    Task<BatteryOverview> LoadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Has Windows write its full HTML battery report to <see cref="ReportsFolder"/>; the value is the file path.</summary>
    Task<OperationResult<string>> SaveFullReportAsync(CancellationToken cancellationToken);
}
