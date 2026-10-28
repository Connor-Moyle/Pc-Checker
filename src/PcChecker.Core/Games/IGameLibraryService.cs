using PcChecker.Core.Common;

namespace PcChecker.Core.Games;

/// <summary>
/// The Games page: finds installed games from every launcher's own files and registry entries (no launcher is
/// started to find them), starts a game or a launcher action as the signed-in user without administrator rights, and
/// moves a Steam game's shader cache to the Recycle Bin. PC Checker never deletes a game's folder itself; removing a
/// game is always handed to its launcher or to the Apps page.
/// </summary>
public interface IGameLibraryService
{
    /// <summary>Looks at every launcher. Problems with one launcher become warnings; the others still count.</summary>
    Task<GameLibraryScan> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Measures the folders of games whose launcher does not record a size and returns the list with those sizes
    /// filled in. Reading only; links inside the folders are not followed.
    /// </summary>
    Task<IReadOnlyList<InstalledGame>> MeasureMissingSizesAsync(IReadOnlyList<InstalledGame> games, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a launcher link or starts a program through the desktop, so it runs without administrator rights even though
    /// PC Checker has them. Fails (and starts nothing) when that is not possible.
    /// </summary>
    Task<OperationResult> RunAsync(GameCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Hands the game's removal to its launcher (the launcher asks the user and does the work). Recorded in the action
    /// journal; no restore point, because game files are not part of the system's configuration.
    /// </summary>
    Task<OperationResult> RequestUninstallAsync(InstalledGame game, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a Steam game's shader cache folder (steamapps\shadercache\&lt;appid&gt;, nothing else) to the Recycle Bin
    /// after checking it again. Steam rebuilds it the next time the game starts. Journaled; the Recycle Bin is the undo.
    /// The value is the number of bytes moved.
    /// </summary>
    Task<OperationResult<long>> CleanShaderCacheAsync(InstalledGame game, nint ownerWindow, CancellationToken cancellationToken);

    /// <summary>Opens the game's folder in File Explorer.</summary>
    OperationResult OpenFolder(InstalledGame game);
}
