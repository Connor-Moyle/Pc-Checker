using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Demo command runner: believable DISM, restore point and Disk Cleanup results for the sample PC; changes nothing.</summary>
public sealed class DemoCleanupCommandService : ICleanupCommandService
{
    public Task<CommandEstimate> EstimateAsync(CleanupCategory category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        return Task.FromResult(Sample(category));
    }

    /// <summary>The estimate the demo shows for a command category (shared with the demo scanner).</summary>
    public static CommandEstimate Sample(CleanupCategory category) => category.Command switch
    {
        CleanupCommand.ComponentCleanup => new CommandEstimate(3_400_000_000, true, ["DISM recommends a cleanup (4 reclaimable packages). The size is DISM's estimate."]),
        CleanupCommand.ComponentResetBase => CommandEstimate.Unknown("Included in the Windows Update cleanup size."),
        CleanupCommand.RestorePointsKeepNewest => new CommandEstimate(9_800_000_000, true, ["4 restore points use 13.1 GB of up to 47.6 GB. The size is an estimate; restore points share data."]),
        CleanupCommand.ClipboardClear => CommandEstimate.Unknown("Frees no disk space."),
        _ => CommandEstimate.Unknown("Windows works out the size while it cleans."),
    };

    public async Task<OperationResult> RunAsync(CleanupCategory category, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        progress?.Report(new ProgressUpdate(category.Name, null, "Running the Windows tool…"));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Message(category));
    }

    /// <summary>What the demo reports after running a command category.</summary>
    public static string Message(CleanupCategory category) => category.Command switch
    {
        CleanupCommand.ComponentCleanup => "DISM cleaned up the component store.",
        CleanupCommand.ComponentResetBase => "DISM removed superseded components and reset the base.",
        CleanupCommand.RestorePointsKeepNewest => "Deleted 3 older restore points; the newest one stays.",
        CleanupCommand.ClipboardClear => "The clipboard and the clipboard history were emptied. Pinned items stay.",
        CleanupCommand.DiskCleanupHandlers => "Windows Disk Cleanup removed: " + string.Join(", ", category.DiskCleanupHandlers) + ".",
        _ => "Done.",
    };
}
