using PcChecker.Core.Common;

namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Finds which game runtime DLLs are on the PC, which DLLs a game needs, and installs the official package that
/// provides a missing DLL. Never downloads or copies single DLL files.
/// </summary>
public interface IGameRuntimeService
{
    /// <summary>Checks every DLL in <see cref="RuntimeCatalog"/> for 32-bit and 64-bit programs. Only reads.</summary>
    Task<RuntimeScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the programs in a game folder (or the folder of a picked .exe) and works out which DLLs they need and
    /// whether each is there. Only reads; nothing in the folder is run.
    /// </summary>
    Task<GameScanResult> ScanGameAsync(string path, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>True when winget can be used for packages that have a winget id.</summary>
    Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Installs <paramref name="package"/> from its official source after the caller confirmed: a Windows feature is
    /// switched on, an installer is downloaded over HTTPS from the vendor, its signature is checked, and it runs
    /// through the change guard.
    /// </summary>
    Task<OperationResult> InstallAsync(RuntimePackage package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
