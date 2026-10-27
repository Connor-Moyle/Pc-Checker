namespace PcChecker.Core.Cleanup;

/// <summary>Pure safety rules: only delete inside the category roots, skip fresh/special files, never follow reparse points.</summary>
public static class CleanupSafety
{
    private static readonly string[] NeverDeleteNames = ["desktop.ini", "thumbs.db", "ntuser.dat", "ntuser.dat.log", "pagefile.sys", "hiberfil.sys", "swapfile.sys"];

    /// <summary>True when <paramref name="fullPath"/> lies strictly inside one of the allowed roots.</summary>
    public static bool IsPathAllowed(string fullPath, IEnumerable<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var path = Normalize(fullPath);
        if (path.Contains("\\..\\", StringComparison.Ordinal) || path.EndsWith("\\..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var r = Normalize(root);
            if (r.Length < 3 || IsDriveRoot(r))
            {
                // Refuse roots that are a whole drive.
                continue;
            }

            if (path.Length > r.Length && path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a file should be left alone: recently modified, a protected name, or a system marker.</summary>
    public static bool ShouldSkip(CleanupFile file, DateTime now, int skipNewerThanMinutes)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = FileNameOf(file.Path);
        if (NeverDeleteNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (skipNewerThanMinutes > 0 && file.LastWrite > now.AddMinutes(-skipNewerThanMinutes))
        {
            return true;
        }

        return false;
    }

    /// <summary>Reparse points (junctions, symlinks) must never be traversed or deleted through.</summary>
    public static bool IsReparsePoint(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>The deepest of <paramref name="allowedRoots"/> that <paramref name="fullPath"/> lies strictly inside, or null.</summary>
    public static string? RootOf(string fullPath, IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        string? best = null;
        foreach (var root in allowedRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) && IsPathAllowed(fullPath, [root]) && (best is null || Normalize(root).Length > best.Length))
            {
                best = Normalize(root);
            }
        }

        return best;
    }

    /// <summary>
    /// The root itself and every folder between it and <paramref name="fullPath"/> (the path's own last segment
    /// excluded), outermost first. Empty when the path is not strictly inside the root. Each of these must still be a
    /// plain folder, not a junction or symlink, when a file below them is deleted.
    /// </summary>
    public static IReadOnlyList<string> FoldersFromRoot(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !IsPathAllowed(fullPath, [root]))
        {
            return [];
        }

        var r = Normalize(root);
        var folders = new List<string> { r };
        var relative = Normalize(fullPath)[(r.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = r;
        for (int i = 0; i < relative.Length - 1; i++)
        {
            current += "\\" + relative[i];
            folders.Add(current);
        }

        return folders;
    }

    /// <summary>
    /// A path as GetFinalPathNameByHandle returns it (<c>\\?\C:\…</c> or <c>\\?\UNC\server\share\…</c>) in the plain
    /// form used everywhere else (<c>C:\…</c>, <c>\\server\share\…</c>). Other text is returned unchanged.
    /// </summary>
    public static string StripExtendedPrefix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>Simple wildcard match supporting '*' and '?' (case-insensitive).</summary>
    public static bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern == "*" || pattern == "*.*")
        {
            return true;
        }

        return MatchCore(fileName.AsSpan(), pattern.AsSpan());
    }

    public static bool MatchesAny(string fileName, IEnumerable<string> patterns) => patterns.Any(p => MatchesPattern(fileName, p));

    private static bool MatchCore(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        while (!pattern.IsEmpty)
        {
            char p = pattern[0];
            if (p == '*')
            {
                pattern = pattern[1..];
                if (pattern.IsEmpty)
                {
                    return true;
                }

                for (int i = 0; i <= text.Length; i++)
                {
                    if (MatchCore(text[i..], pattern))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (text.IsEmpty)
            {
                return false;
            }

            if (p != '?' && char.ToUpperInvariant(p) != char.ToUpperInvariant(text[0]))
            {
                return false;
            }

            text = text[1..];
            pattern = pattern[1..];
        }

        return text.IsEmpty;
    }

    /// <summary>Last path segment, treating both separators as separators regardless of host OS.</summary>
    public static string FileNameOf(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');

    private static bool IsDriveRoot(string normalized) => normalized.Length == 2 && normalized[1] == ':';
}
