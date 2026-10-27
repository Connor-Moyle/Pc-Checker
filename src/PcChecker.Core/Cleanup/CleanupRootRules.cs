using System.Text.RegularExpressions;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Pure helpers behind the computed template variables and the run-time exclusions: Steam's library list, where
/// Windows writes crash dumps, which folders under C:\AMD an installed program still needs, and which partial Steam
/// downloads are still queued.
/// </summary>
public static partial class CleanupRootRules
{
    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    // Steam's older libraryfolders.vdf: "1"  "D:\\SteamLibrary" directly under "LibraryFolders".
    [GeneratedRegex("^\\s*\"\\d+\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"\\s*$", RegexOptions.Multiline)]
    private static partial Regex VdfLegacyPath();

    /// <summary>
    /// Library folders listed in Steam's steamapps\libraryfolders.vdf (both the current and the old format), as plain
    /// Windows paths. Network and relative paths are dropped; duplicates removed.
    /// </summary>
    public static IReadOnlyList<string> ParseSteamLibraries(string? vdf)
    {
        if (string.IsNullOrWhiteSpace(vdf))
        {
            return [];
        }

        var found = VdfPath().Matches(vdf).Select(m => m.Groups[1].Value)
            .Concat(VdfLegacyPath().Matches(vdf).Select(m => m.Groups[1].Value))
            .Select(UnescapeVdf)
            .Select(NormalizeWindowsPath)
            .Where(p => p is not null && CleanupPathResolver.IsAcceptableRoot(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return found;
    }

    /// <summary>
    /// The Steam folder from HKCU\Software\Valve\Steam\SteamPath, which Steam writes with forward slashes and in lower
    /// case ("c:/program files (x86)/steam"). Null when missing or not a local path.
    /// </summary>
    public static string? NormalizeSteamPath(string? registryValue)
    {
        var path = NormalizeWindowsPath(registryValue);
        return CleanupPathResolver.IsAcceptableRoot(path) ? path : null;
    }

    /// <summary>
    /// Folders in steamapps\downloading that belong to a game Steam still knows about (an appmanifest_&lt;id&gt;.acf exists),
    /// that is an active, queued or paused download. Only the others are abandoned leftovers.
    /// </summary>
    public static IReadOnlyList<string> ActiveSteamDownloads(string downloadingFolder, IEnumerable<string> downloadSubfolderNames, IEnumerable<string> steamappsFileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadingFolder);
        var manifests = new HashSet<string>(
            (steamappsFileNames ?? [])
                .Select(n => CleanupSafety.FileNameOf(n))
                .Where(n => n.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".acf", StringComparison.OrdinalIgnoreCase))
                .Select(n => n["appmanifest_".Length..^4]),
            StringComparer.OrdinalIgnoreCase);
        var folder = downloadingFolder.TrimEnd('\\');
        return (downloadSubfolderNames ?? [])
            .Select(n => CleanupSafety.FileNameOf(n.TrimEnd('\\')))
            .Where(n => n.Length > 0 && (manifests.Contains(n) || !n.All(char.IsAsciiDigit)))
            .Select(n => folder + "\\" + n)
            .ToList();
    }

    /// <summary>
    /// The folder of a crash dump setting from HKLM\SYSTEM\CurrentControlSet\Control\CrashControl: DumpFile is a file
    /// (%SystemRoot%\MEMORY.DMP), MinidumpDir a folder. %variables% are expanded with <paramref name="expand"/>.
    /// Null when unset, not local, or a whole drive.
    /// </summary>
    public static string? DumpFolder(string? value, bool isFile, Func<string, string> expand)
    {
        ArgumentNullException.ThrowIfNull(expand);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded;
        try
        {
            expanded = expand(value.Trim().Trim('"'));
        }
        catch
        {
            return null;
        }

        var path = NormalizeWindowsPath(expanded);
        if (path is null)
        {
            return null;
        }

        if (isFile)
        {
            int cut = path.LastIndexOf('\\');
            path = cut > 0 ? path[..cut] : null;
        }

        return CleanupPathResolver.IsAcceptableRoot(path) ? path : null;
    }

    /// <summary>
    /// Folders directly under <paramref name="root"/> (for example C:\AMD\Chipset_Software) that an UninstallString,
    /// QuietUninstallString or InstallSource refers to, so the program can still be uninstalled. When a value points
    /// at a file directly in the root, the root itself is returned (keep everything).
    /// </summary>
    public static IReadOnlyList<string> FoldersReferencedUnder(string root, IEnumerable<string?> registryValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var r = root.Trim().Replace('/', '\\').TrimEnd('\\');
        var prefix = r + "\\";
        var result = new List<string>();
        foreach (var raw in registryValues ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var value = raw.Replace('/', '\\');
            int start = 0;
            while ((start = value.IndexOf(prefix, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                // The character before the root must not be part of a longer path (D:\X\C:\AMD is not C:\AMD).
                bool boundary = start == 0 || value[start - 1] is '"' or ' ' or '=' or '\'' or ',';
                int after = start + prefix.Length;
                if (boundary)
                {
                    int end = after;
                    while (end < value.Length && value[end] is not ('\\' or '"' or '\''))
                    {
                        end++;
                    }

                    var first = value[after..end].Trim();
                    bool isFolder = end < value.Length && value[end] == '\\';
                    if (first.Length == 0)
                    {
                        // "C:\AMD\" on its own: the whole folder is referenced.
                        result.Add(r);
                    }
                    else if (isFolder)
                    {
                        result.Add(r + "\\" + first);
                    }
                    else
                    {
                        // A file directly in the root, or a folder written without a trailing backslash followed by
                        // arguments. Keep the folder if the text names one, otherwise keep everything.
                        var candidate = first.Split(' ')[0];
                        result.Add(candidate.Contains('.') ? r : r + "\\" + first);
                    }
                }

                start = after;
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>When the uninstall entries cannot be read: the newest folder whose name mentions the chipset driver.</summary>
    public static string? NewestChipsetFolder(IEnumerable<(string Path, DateTime LastWrite)> folders) =>
        (folders ?? [])
            .Where(f => CleanupSafety.FileNameOf(f.Path.TrimEnd('\\')).Contains("chipset", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWrite)
            .Select(f => f.Path.TrimEnd('\\'))
            .FirstOrDefault();

    /// <summary>Turns forward slashes into backslashes, trims quotes and a trailing separator. Null for blank input.</summary>
    public static string? NormalizeWindowsPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var p = value.Trim().Trim('"').Replace('/', '\\');
        while (p.Contains(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            p = p.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        p = p.TrimEnd('\\');
        if (p.Length >= 2 && p[1] == ':')
        {
            p = char.ToUpperInvariant(p[0]) + p[1..];
        }

        return p.Length == 0 ? null : p;
    }

    private static string UnescapeVdf(string value) => value.Replace(@"\\", @"\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
}
