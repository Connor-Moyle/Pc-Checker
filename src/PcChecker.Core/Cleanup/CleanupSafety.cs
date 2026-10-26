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
