namespace PcChecker.Core.Games;

/// <summary>The game launchers and stores PC Checker reads installed games from.</summary>
public enum GameLauncher
{
    Steam,
    Epic,
    Gog,
    Ea,
    Ubisoft,
    BattleNet,
    Xbox,
    Riot,
}

/// <summary>What a <see cref="GameCommand"/> does when it runs.</summary>
public enum GameCommandKind
{
    /// <summary>Opens a launcher link (steam://, uplay://, ms-settings:…) through the desktop, without administrator rights.</summary>
    Uri,

    /// <summary>Starts a program file through the desktop, without administrator rights.</summary>
    Program,

    /// <summary>Nothing PC Checker can open directly: the app sends the user to its own Apps page instead.</summary>
    AppsPage,
}

/// <summary>
/// One thing the page can do for a game: start it, ask its launcher to check its files, or ask its launcher to remove
/// it. <see cref="Target"/> is the link or program file; <see cref="Note"/> tells the user what happens next when the
/// launcher (not PC Checker) does the work.
/// </summary>
public sealed record GameCommand(GameCommandKind Kind, string Target, string? Arguments = null, string? WorkingDirectory = null, string? Note = null)
{
    public static GameCommand Link(string uri, string? note = null) => new(GameCommandKind.Uri, uri, null, null, note);

    public static GameCommand Program(string file, string? arguments = null, string? workingDirectory = null, string? note = null) =>
        new(GameCommandKind.Program, file, arguments, workingDirectory, note);

    public static GameCommand AppsPage(string note) => new(GameCommandKind.AppsPage, string.Empty, null, null, note);
}

/// <summary>
/// One installed game, found from its launcher's own files or registry entries (the launcher is never started to
/// find it). Sizes and dates are null when the launcher does not record them and they could not be measured.
/// </summary>
public sealed record InstalledGame(
    string Id,
    GameLauncher Launcher,
    string LauncherGameId,
    string Name,
    string? InstallPath,
    long? SizeBytes,
    DateTimeOffset? LastPlayed,
    DateTimeOffset? LastUpdated,
    string? ExePath,
    string? CoverImagePath,
    GameCommand? Play,
    GameCommand? Verify,
    GameCommand? Uninstall,
    string? ShaderCachePath = null,
    long? ShaderCacheBytes = null,
    string? Note = null)
{
    /// <summary>The drive the game is on ("C:"), or null when the folder is not known.</summary>
    public string? Drive => GameLibraryMath.DriveOf(InstallPath);
}

/// <summary>Whether a launcher is installed and how many games it has.</summary>
public sealed record LauncherSummary(GameLauncher Launcher, bool IsInstalled, int GameCount, string? Note = null);

/// <summary>The result of one look at every launcher.</summary>
public sealed record GameLibraryScan(
    IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<LauncherSummary> Launchers,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ScannedAt)
{
    public static GameLibraryScan Empty { get; } = new([], [], [], DateTimeOffset.MinValue);
}

/// <summary>Space used on one drive by the games on it.</summary>
public sealed record DriveGameTotal(string Drive, int GameCount, long SizeBytes);

/// <summary>Totals over a list of games: how many, the space they use (where known) and the same per drive.</summary>
public sealed record GameLibraryTotals(int GameCount, long SizeBytes, int UnknownSizeCount, IReadOnlyList<DriveGameTotal> Drives);

/// <summary>How the game grid is ordered.</summary>
public enum GameSortOrder
{
    Name,
    Size,
    LastPlayed,
}

/// <summary>Asks the Missing DLLs page to scan one game's folder (sent from the Games page).</summary>
public sealed record GameFolderScanRequest(string Folder, string GameName);

/// <summary>Asks the Overlay page to open a game profile for one game's program (sent from the Games page).</summary>
public sealed record GameProfileRequest(string GameName, string ExePath);
