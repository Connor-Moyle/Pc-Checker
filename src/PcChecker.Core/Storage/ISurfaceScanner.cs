namespace PcChecker.Core.Storage;

/// <summary>
/// Optional extension of <see cref="IStorageToolsService"/>: a surface scan with explicit options (quick or full).
/// <see cref="IStorageToolsService.SurfaceScanAsync"/> stays the full-scan entry point; the page uses this interface
/// when the service offers it.
/// </summary>
public interface ISurfaceScanner
{
    Task<SurfaceScanResult> SurfaceScanAsync(int diskNumber, SurfaceScanOptions options, IProgress<SurfaceScanProgress>? progress, CancellationToken cancellationToken);
}
