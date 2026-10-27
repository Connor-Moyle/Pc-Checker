using PcChecker.Core.Common;

namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Shows what uses the space on a drive or in a folder, finds duplicate files and moves what the user picks to the
/// Recycle Bin. Scans only read; recycling goes through the change guard (journaled, no restore point: the Recycle Bin
/// is the undo) and refuses protected locations.
/// </summary>
public interface IDiskSpaceService
{
    /// <summary>The fixed drives that are ready, with their used and free space.</summary>
    Task<IReadOnlyList<DriveSpaceInfo>> GetDrivesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Measures everything under <paramref name="rootPath"/> (a drive root or a folder). Links (junctions, symbolic
    /// links, mounted folders) are not followed and folders that cannot be read are counted, not fatal.
    /// </summary>
    Task<DiskScanResult> ScanAsync(string rootPath, DiskScanOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Confirms which candidates have identical content: same size, then a quick hash, then SHA-256.</summary>
    Task<DuplicateScanResult> FindDuplicatesAsync(IReadOnlyList<DiskFileEntry> candidates, long minBytes, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken);

    /// <summary>Why <paramref name="path"/> may not be deleted, or null when it may be offered.</summary>
    string? WhyProtected(string path);

    /// <summary>
    /// Moves the items to the Recycle Bin one by one after checking each again (protected location, link, local fixed
    /// drive). Nested items are dropped when their folder is also listed. The value has one result per item.
    /// <paramref name="ownerWindow"/> (the app's main window handle, or 0) owns any prompt Windows shows, such as the
    /// warning that an item is too big for the Recycle Bin, so it opens in front of the app.
    /// </summary>
    Task<OperationResult<RecycleResult>> RecycleAsync(IReadOnlyList<RecycleRequest> items, nint ownerWindow, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken);

    /// <summary>Opens File Explorer at a folder, or at a file's folder with the file selected.</summary>
    OperationResult ShowInExplorer(string path, bool isFolder);
}
