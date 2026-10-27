namespace PcChecker.Core.LiveUsage;

/// <summary>
/// Live resource use for the Live usage page (a Task Manager Performance tab): the list of resources with their static
/// facts, and one reading of every resource per call. Rates (processor, disk, network, GPU) are measured between two
/// calls, so the first reading after <see cref="ResetRates"/> shows zero for them.
/// </summary>
public interface ILiveUsageService
{
    /// <summary>Reads the processor, memory, disks, active network adapters and graphics cards. Never throws for missing parts.</summary>
    Task<LiveUsageInventory> GetInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Takes one reading of every resource.</summary>
    Task<LiveUsageSample> SampleAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the previous reading so the next rates are measured over a fresh interval (after a pause).</summary>
    void ResetRates();

    /// <summary>
    /// Readings for the time before the page opened, oldest first. Demo mode fills the graphs this way so screenshots
    /// show a full minute; on a real PC there is no earlier data and the list is empty.
    /// </summary>
    IReadOnlyList<LiveUsageSample> Backfill(int count);
}
