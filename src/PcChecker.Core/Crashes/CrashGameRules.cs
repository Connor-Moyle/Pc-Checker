using PcChecker.Core.Games;

namespace PcChecker.Core.Crashes;

/// <summary>Links a crashed program to a game the Games page found (by its folder). Pure.</summary>
public static class CrashGameRules
{
    /// <summary>
    /// The game whose program is <paramref name="appPath"/> or whose install folder contains it; the deepest folder wins
    /// when libraries are nested. Null when the program is not part of a known game.
    /// </summary>
    public static InstalledGame? FindGame(string? appPath, IEnumerable<InstalledGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return null;
        }

        var app = Normalize(appPath);
        InstalledGame? best = null;
        int bestLength = -1;
        foreach (var game in games)
        {
            if (game.ExePath is { Length: > 0 } exe && string.Equals(Normalize(exe), app, StringComparison.OrdinalIgnoreCase))
            {
                return game;
            }

            if (game.InstallPath is not { Length: > 0 } install)
            {
                continue;
            }

            var folder = Normalize(install).TrimEnd('\\') + "\\";
            if (app.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && folder.Length > bestLength)
            {
                best = game;
                bestLength = folder.Length;
            }
        }

        return best;
    }

    /// <summary>The folder to scan for missing DLLs: the game's folder, or the program's own folder.</summary>
    public static string? ScanFolder(string? appPath, InstalledGame? game)
    {
        if (game?.InstallPath is { Length: > 0 } install)
        {
            return install;
        }

        if (string.IsNullOrWhiteSpace(appPath))
        {
            return null;
        }

        var p = Normalize(appPath);
        int slash = p.LastIndexOf('\\');
        return slash > 0 ? p[..slash] : null;
    }

    private static string Normalize(string path) => path.Trim().Trim('"').Replace('/', '\\');
}
