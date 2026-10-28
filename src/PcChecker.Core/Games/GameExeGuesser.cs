namespace PcChecker.Core.Games;

/// <summary>A program file found in a game's folder: its path relative to the folder and its size.</summary>
public sealed record ExeCandidate(string RelativePath, long SizeBytes);

/// <summary>
/// Picks a game's main program when its launcher does not say which one it is (Steam, Ubisoft, Riot). Uninstallers,
/// setup programs, redistributables, crash reporters and anti-cheat services are never picked; among the rest, a name
/// like the game's wins, then a shallow, large file. Only used for the icon and for a game profile, never to start
/// the game (that always goes through the launcher's own link when there is one).
/// </summary>
public static class GameExeGuesser
{
    private static readonly string[] ExcludedNameParts =
    [
        "unins", "uninstall", "setup", "vcredist", "vc_redist", "dxsetup", "dotnet", "crash", "bugreport", "errorreport",
        "easyanticheat", "beservice", "battleye", "eac_launcher", "cefprocess", "qtwebengine", "webhelper", "updater",
        "touchup", "cleanup", "redist", "prereq", "installer", "unitycrashhandler", "dumper",
    ];

    private static readonly string[] ExcludedFolders =
        ["_commonredist", "redist", "redistributables", "__installer", "directx", "support", "_redist", "installers", "prereqs", "dotnet", "vcredist"];

    /// <summary>The most likely main program, or null when every candidate is a helper.</summary>
    public static string? Pick(string? gameName, string? folderName, IEnumerable<ExeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var game = Simplify(gameName);
        var folder = Simplify(folderName);
        return candidates
            .Where(c => IsCandidate(c.RelativePath))
            .Select(c => (c.RelativePath, Score: Score(c, game, folder)))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.RelativePath.Length)
            .Select(c => c.RelativePath)
            .FirstOrDefault();
    }

    /// <summary>False for helper programs and anything inside a redistributables or installer folder.</summary>
    public static bool IsCandidate(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var name = parts[^1].ToLowerInvariant();
        if (ExcludedNameParts.Any(name.Contains))
        {
            return false;
        }

        return !parts[..^1].Any(p => ExcludedFolders.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private static int Score(ExeCandidate candidate, string game, string folder)
    {
        var parts = candidate.RelativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var stem = Simplify(Path.GetFileNameWithoutExtension(parts[^1]));
        int score = 0;
        foreach (var target in new[] { game, folder }.Where(t => t.Length > 0))
        {
            if (stem == target)
            {
                score = Math.Max(score, 100);
            }
            else if (stem.Length >= 3 && (target.Contains(stem, StringComparison.Ordinal) || stem.Contains(target, StringComparison.Ordinal)))
            {
                score = Math.Max(score, 60);
            }
            else if (CommonPrefix(stem, target) >= 4)
            {
                score = Math.Max(score, 30);
            }
        }

        if (stem.Contains("launcher", StringComparison.Ordinal))
        {
            score -= 25;
        }

        if (stem.Contains("server", StringComparison.Ordinal) || stem.Contains("editor", StringComparison.Ordinal)
            || stem.Contains("config", StringComparison.Ordinal) || stem.Contains("settings", StringComparison.Ordinal))
        {
            score -= 20;
        }

        score -= 4 * (parts.Length - 1);
        long mb = Math.Max(1, candidate.SizeBytes / (1024 * 1024));
        score += Math.Min(15, (int)Math.Log2(mb) * 2);
        return score;
    }

    /// <summary>Lower-case letters and digits only ("Baldur's Gate 3" → "baldursgate3").</summary>
    public static string Simplify(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int CommonPrefix(string a, string b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n])
        {
            n++;
        }

        return n;
    }
}
