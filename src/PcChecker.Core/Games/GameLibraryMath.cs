namespace PcChecker.Core.Games;

/// <summary>
/// Pure helpers for the game library: Windows path joining that does not depend on the OS running the tests, the
/// drive a game is on, totals per drive, sorting, filtering and searching, and merging the same game found twice.
/// </summary>
public static class GameLibraryMath
{
    /// <summary>Joins Windows path parts with back-slashes (Path.Combine would use '/' on Linux, where the tests run).</summary>
    public static string JoinPath(params string[] parts)
    {
        var pieces = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (pieces.Count == 0)
        {
            return string.Empty;
        }

        var result = pieces[0].Replace('/', '\\').TrimEnd('\\');
        foreach (var piece in pieces.Skip(1))
        {
            result += "\\" + piece.Replace('/', '\\').Trim('\\');
        }

        // "C:" alone would make "C:folder" (relative to C:'s current folder).
        return result.Length == 2 && result[1] == ':' ? result + "\\" : result;
    }

    /// <summary>The last folder or file name of a Windows path ("C:\Games\Hades\" gives "Hades").</summary>
    public static string LastSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().TrimEnd('\\', '/');
        int slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The drive letter of a local path ("D:"), or null for a share, a relative path or nothing.</summary>
    public static string? DriveOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim();
        return p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' ? char.ToUpperInvariant(p[0]) + ":" : null;
    }

    /// <summary>How many games, the space they use where known, and the same per drive (largest first).</summary>
    public static GameLibraryTotals Totals(IEnumerable<InstalledGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var list = games.ToList();
        var drives = list
            .Where(g => g.Drive is not null)
            .GroupBy(g => g.Drive!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DriveGameTotal(g.Key, g.Count(), g.Sum(x => Math.Max(0, x.SizeBytes ?? 0))))
            .OrderByDescending(d => d.SizeBytes)
            .ThenBy(d => d.Drive, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new GameLibraryTotals(
            list.Count,
            list.Sum(g => Math.Max(0, g.SizeBytes ?? 0)),
            list.Count(g => g.SizeBytes is null),
            drives);
    }

    /// <summary>
    /// Orders games: by name (A–Z), by size (largest first, unknown sizes last) or by last played (most recent first,
    /// never-played last). Ties fall back to the name so the order is stable.
    /// </summary>
    public static IReadOnlyList<InstalledGame> Sort(IEnumerable<InstalledGame> games, GameSortOrder order)
    {
        ArgumentNullException.ThrowIfNull(games);
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return order switch
        {
            GameSortOrder.Size => games.OrderBy(g => g.SizeBytes is null).ThenByDescending(g => g.SizeBytes ?? 0).ThenBy(g => g.Name, byName).ToList(),
            GameSortOrder.LastPlayed => games.OrderBy(g => g.LastPlayed is null).ThenByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue)
                .ThenBy(g => g.Name, byName).ToList(),
            _ => games.OrderBy(g => SortName(g.Name), byName).ToList(),
        };
    }

    /// <summary>
    /// Keeps the games that match every given filter: launcher (null for all), drive (null for all) and a search text
    /// matched against the name, the launcher's name and the install folder.
    /// </summary>
    public static IReadOnlyList<InstalledGame> Filter(IEnumerable<InstalledGame> games, GameLauncher? launcher, string? drive, string? search)
    {
        ArgumentNullException.ThrowIfNull(games);
        var text = search?.Trim();
        return games.Where(g =>
                (launcher is null || g.Launcher == launcher)
                && (string.IsNullOrEmpty(drive) || string.Equals(g.Drive, drive, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrEmpty(text)
                    || g.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || LauncherName(g.Launcher).Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || (g.InstallPath?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
    }

    /// <summary>
    /// Drops a game found twice (for example an EA game in both the EA registry keys and the Uninstall list): games of
    /// the same launcher in the same folder are one game, the first found wins and borrows what it lacks from the other.
    /// </summary>
    public static IReadOnlyList<InstalledGame> MergeDuplicates(IEnumerable<InstalledGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var result = new List<InstalledGame>();
        foreach (var game in games)
        {
            int index = result.FindIndex(r => r.Id == game.Id || (r.Launcher == game.Launcher && SameFolder(r.InstallPath, game.InstallPath)));
            if (index < 0)
            {
                result.Add(game);
                continue;
            }

            var kept = result[index];
            result[index] = kept with
            {
                SizeBytes = kept.SizeBytes ?? game.SizeBytes,
                LastPlayed = Latest(kept.LastPlayed, game.LastPlayed),
                LastUpdated = kept.LastUpdated ?? game.LastUpdated,
                ExePath = kept.ExePath ?? game.ExePath,
                CoverImagePath = kept.CoverImagePath ?? game.CoverImagePath,
                Play = kept.Play ?? game.Play,
                Verify = kept.Verify ?? game.Verify,
                Uninstall = kept.Uninstall ?? game.Uninstall,
            };
        }

        return result;
    }

    /// <summary>The launcher's name as people know it.</summary>
    public static string LauncherName(GameLauncher launcher) => launcher switch
    {
        GameLauncher.Steam => "Steam",
        GameLauncher.Epic => "Epic Games",
        GameLauncher.Gog => "GOG Galaxy",
        GameLauncher.Ea => "EA app",
        GameLauncher.Ubisoft => "Ubisoft Connect",
        GameLauncher.BattleNet => "Battle.net",
        GameLauncher.Xbox => "Xbox",
        GameLauncher.Riot => "Riot Client",
        _ => launcher.ToString(),
    };

    /// <summary>
    /// Whole calendar days between the day a game was last played and today, in local time (0 = today); null when
    /// the launcher does not record it. A time in the future (a wrong clock) counts as today.
    /// </summary>
    public static int? DaysSince(DateTimeOffset? lastPlayed, DateTimeOffset now)
    {
        if (lastPlayed is null)
        {
            return null;
        }

        int days = (int)(now.ToLocalTime().Date - lastPlayed.Value.ToLocalTime().Date).TotalDays;
        return Math.Max(0, days);
    }

    /// <summary>The game played most recently, or null when no launcher records it.</summary>
    public static InstalledGame? MostRecentlyPlayed(IEnumerable<InstalledGame> games) =>
        games.Where(g => g.LastPlayed is not null).MaxBy(g => g.LastPlayed);

    /// <summary>The name used for A–Z order: "The Witcher 3" sorts under W, as most launchers do.</summary>
    public static string SortName(string name)
    {
        var n = name.Trim();
        return n.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && n.Length > 4 ? n[4..] : n;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static bool SameFolder(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(SteamLibraryRules.NormalizeFolder(a), SteamLibraryRules.NormalizeFolder(b), StringComparison.OrdinalIgnoreCase);
}
