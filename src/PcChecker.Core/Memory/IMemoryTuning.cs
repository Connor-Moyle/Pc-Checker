using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>
/// Extra memory controls that are not part of the shared <see cref="IMemoryService"/> contract. Both the Windows and
/// the demo service implement it; <see cref="MemoryActionsProvider"/> looks for it with a type check.
/// </summary>
public interface IMemoryTuning
{
    /// <summary>Starts (and sets to automatic) or stops (and disables) the SysMain/Superfetch service.</summary>
    Task<OperationResult> SetSysMainAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Puts back a remembered page file configuration exactly: automatic management, or each page file with its sizes,
    /// removing any page file that was not there before.
    /// </summary>
    Task<OperationResult> RestorePageFilesAsync(PageFileSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>"Intel", "AMD" or null when unknown; known after the first overview. Only changes XMP/EXPO wording.</summary>
    string? PlatformVendor { get; }
}
