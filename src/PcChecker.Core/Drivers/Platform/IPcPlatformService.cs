using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// Reads this PC's board, model, BIOS and chipset driver and, when asked to go online, compares them with what the
/// makers publish (AMD's chipset page, Lenovo's catalog). Only looks; never changes the system. Never throws for
/// network problems: the report says what could not be checked.
/// </summary>
public interface IPcPlatformService
{
    /// <summary>The last report, or null before the first check.</summary>
    PcPlatformReport? Last { get; }

    /// <summary>
    /// Builds the report. <paramref name="online"/> false reads only this PC and saved maker answers (no network);
    /// true asks the makers too (answers are reused for 12 hours unless <paramref name="forceRefresh"/>).
    /// </summary>
    Task<PcPlatformReport> CheckAsync(bool online, bool forceRefresh, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
