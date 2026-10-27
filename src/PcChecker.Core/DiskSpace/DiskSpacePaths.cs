namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Path helpers that behave the same on every OS, so the tests run on Linux. Windows paths ("C:\…", "c:/…") use
/// backslashes and keep the drive root's backslash ("C:\"); a path starting with "/" (only seen in the tests) keeps its
/// forward slashes. Both separators are understood everywhere and comparisons ignore case.
/// </summary>
public static class DiskSpacePaths
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Trims spaces and trailing separators; Windows paths use backslashes and a drive root stays "C:\".</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = path.Trim();
        bool windows = IsWindowsStyle(p);
        if (windows)
        {
            p = p.Replace('/', '\\');
        }

        while (p.Length > 1 && Separators.Contains(p[^1]) && !(windows && p.Length == 3 && p[1] == ':'))
        {
            p = p[..^1];
        }

        if (p.Length == 2 && p[1] == ':')
        {
            p += "\\";
        }

        return p;
    }

    /// <summary>True for "C:", "C:\" and "c:/".</summary>
    public static bool IsDriveRoot(string path)
    {
        var p = Normalize(path);
        return p.Length == 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    /// <summary>True for an absolute local Windows path such as "C:\Users" (not UNC, not a device path, not relative).</summary>
    public static bool IsLocalAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var p = Normalize(path);
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    /// <summary>Joins a folder and a name with one separator of the folder's own kind.</summary>
    public static string Combine(string folder, string name)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(name);
        if (folder.Length > 0 && Separators.Contains(folder[^1]))
        {
            return folder + name;
        }

        char separator = folder.Contains('/') && !folder.Contains('\\') ? '/' : '\\';
        return folder + separator + name;
    }

    /// <summary>The last segment ("file.txt"), or the drive ("C:") for a drive root.</summary>
    public static string NameOf(string path)
    {
        var p = Normalize(path);
        if (IsDriveRoot(p))
        {
            return p[..2];
        }

        int cut = p.LastIndexOfAny(Separators);
        return cut < 0 || cut == p.Length - 1 ? p : p[(cut + 1)..];
    }

    /// <summary>The containing folder, or null for a drive root, "/" or a bare name.</summary>
    public static string? ParentOf(string path)
    {
        var p = Normalize(path);
        if (IsDriveRoot(p) || p == "/")
        {
            return null;
        }

        int cut = p.LastIndexOfAny(Separators);
        if (cut < 0)
        {
            return null;
        }

        if (cut == 0)
        {
            return p[..1];
        }

        return cut == 2 && p[1] == ':' ? p[..3] : p[..cut];
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or anything inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var p = Comparable(path);
        var f = Comparable(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = f.EndsWith('\\') ? f : f + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The segments after the drive: "C:\Users\Alex" → ["Users", "Alex"].</summary>
    public static IReadOnlyList<string> Segments(string path)
    {
        var p = Normalize(path);
        var start = p.Length >= 3 && p[1] == ':' ? 3 : 0;
        return p.Length <= start ? [] : p[start..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The path relative to <paramref name="folder"/> as segments, or null when it is not inside it.
    /// The folder itself gives an empty list.
    /// </summary>
    public static IReadOnlyList<string>? RelativeSegments(string path, string folder)
    {
        if (!IsSameOrInside(path, folder))
        {
            return null;
        }

        var p = Normalize(path);
        var f = Normalize(folder);
        if (p.Length == f.Length)
        {
            return [];
        }

        var rest = p[(Separators.Contains(f[^1]) ? f.Length : f.Length + 1)..];
        return rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>True when any segment is "." or "..": such paths are refused rather than resolved.</summary>
    public static bool HasRelativeSegments(string path) =>
        path.Split(Separators).Any(s => s is "." or "..");

    private static bool IsWindowsStyle(string p) =>
        (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0])) || p.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Comparable(string path) => Normalize(path).Replace('/', '\\');
}
