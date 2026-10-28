using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// The RAM tuning page's data: what memory, processor, board and BIOS the PC has, the quick memory check and the saved
/// history of "Check my change". Nothing here changes a setting on the PC; BIOS settings can only be changed by the
/// user in the BIOS.
/// </summary>
public interface IRamTuningService
{
    /// <summary>The last detection, or null before the first.</summary>
    RamFacts? Last { get; }

    /// <summary>Reads the modules, processor, board, BIOS and DIMM temperatures. Runs off the UI thread.</summary>
    Task<RamFacts> DetectAsync(Translator? tr, CancellationToken cancellationToken);

    /// <summary>Memory Windows has free right now, or null when it cannot be read.</summary>
    long? GetFreeMemoryBytes();

    /// <summary>
    /// Runs the quick memory check on <paramref name="percent"/> of the free memory for <paramref name="duration"/> at
    /// below-normal priority. Stops by itself when Windows runs low on memory. Makes no change to the PC.
    /// </summary>
    Task<QuickCheckResult> RunQuickCheckAsync(int percent, TimeSpan duration, IProgress<QuickCheckProgress>? progress, Translator? tr, CancellationToken cancellationToken);

    /// <summary>The saved starting point and checks (empty when there are none).</summary>
    Task<RamTuningJournal> LoadJournalAsync(CancellationToken cancellationToken);

    /// <summary>Saves the starting point and checks.</summary>
    Task SaveJournalAsync(RamTuningJournal journal, CancellationToken cancellationToken);
}
