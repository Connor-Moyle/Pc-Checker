using PcChecker.Core.DiskSpace;

namespace PcChecker.Core.Leftovers;

/// <summary>One scanned folder root and what it is.</summary>
public sealed record LeftoverRootFolder(LeftoverRoot Root, string Path);

/// <summary>
/// Where leftovers may be removed from. A folder must sit one or two levels inside one of the scanned roots
/// (<c>Program Files\Vendor</c> or <c>Program Files\Vendor\Product</c>), be a plain local path, not be a link
/// (junction or symbolic link) and not sit inside a linked folder; a shortcut must be a .lnk inside a Start menu folder.
/// Anything else is refused, even if a scan listed it. Pure: attributes come from the caller.
/// </summary>
public static class LeftoverPathRules
{
    /// <summary>How deep inside a root a leftover folder may be: 1 = the vendor folder, 2 = a product folder in it.</summary>
    public const int MaxFolderDepth = 2;

    /// <summary>How deep inside a Start menu folder a shortcut may be (Programs\Vendor\Sub\Shortcut.lnk).</summary>
    public const int MaxShortcutDepth = 4;

    private static readonly char[] Wildcards = ['*', '?', '<', '>', '|', '"'];

    /// <summary>The root that holds <paramref name="path"/>, or null when it is in none of them.</summary>
    public static LeftoverRootFolder? RootOf(string path, IEnumerable<LeftoverRootFolder> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // The longest root wins, so a root nested in another (AppData\Local inside the profile) is picked first.
        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path) && DiskSpacePaths.IsSameOrInside(path, r.Path))
            .OrderByDescending(r => DiskSpacePaths.Normalize(r.Path).Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Why <paramref name="path"/> may not be removed as a leftover folder, or null when it may. Checks the shape of the
    /// path only; <see cref="WhyRefusedByAttributes"/> checks what is on disk.
    /// </summary>
    public static string? WhyFolderRefused(string? path, IReadOnlyList<LeftoverRootFolder> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var basic = WhyNotPlainLocalPath(path);
        if (basic is not null)
        {
            return basic;
        }

        var root = RootOf(path!, roots);
        if (root is null)
        {
            return "It is not inside Program Files, ProgramData or AppData.";
        }

        var relative = DiskSpacePaths.RelativeSegments(path!, root.Path);
        if (relative is null || relative.Count == 0)
        {
            return "A whole system folder is never removed.";
        }

        if (relative.Count > MaxFolderDepth)
        {
            return "Only folders directly in a program folder, or one level further down, are removed.";
        }

        // A root nested inside this folder (say AppData\Local inside a profile root) would go with it.
        if (roots.Any(r => !ReferenceEquals(r, root) && DiskSpacePaths.IsSameOrInside(r.Path, path!)))
        {
            return "It contains another system folder.";
        }

        if (LeftoverAllowList.IsAllowedFolder(root.Root, relative[0]))
        {
            return $"{relative[0]} is shared by Windows or by several programs and is never removed.";
        }

        if (relative.Count == 2 && LeftoverAllowList.IsSharedInsideVendor(relative[1]))
        {
            return $"{relative[1]} may be shared by other programs of the same maker and is never removed on its own.";
        }

        return null;
    }

    /// <summary>Why a shortcut may not be removed, or null when it may (a .lnk inside one of the Start menu folders).</summary>
    public static string? WhyShortcutRefused(string? path, IEnumerable<string> startMenuFolders)
    {
        ArgumentNullException.ThrowIfNull(startMenuFolders);
        var basic = WhyNotPlainLocalPath(path);
        if (basic is not null)
        {
            return basic;
        }

        if (!path!.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "Only shortcut (.lnk) files are removed from the Start menu.";
        }

        foreach (var folder in startMenuFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var relative = DiskSpacePaths.RelativeSegments(path, folder);
            if (relative is { Count: > 0 })
            {
                return relative.Count <= MaxShortcutDepth ? null : "The shortcut is nested too deep in the Start menu.";
            }
        }

        return "It is not in a Start menu folder.";
    }

    /// <summary>
    /// Why the item may not be removed given its own attributes and those of every folder between the root and it
    /// (<paramref name="ancestors"/>), or null. Links are never followed or removed, and neither is anything inside one.
    /// </summary>
    public static string? WhyRefusedByAttributes(FileAttributes item, IEnumerable<FileAttributes> ancestors)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        if ((item & FileAttributes.ReparsePoint) != 0)
        {
            return "It is a link to another location (junction or symbolic link).";
        }

        if ((item & FileAttributes.System) != 0)
        {
            return "Windows marks it as a system item.";
        }

        if ((item & (FileAttributes.Offline | (FileAttributes)0x00400000)) != 0)
        {
            return "It is stored online only (a cloud placeholder).";
        }

        return ancestors.Any(a => (a & FileAttributes.ReparsePoint) != 0) ? "It is inside a linked folder." : null;
    }

    /// <summary>The folders between <paramref name="root"/> (excluded) and <paramref name="path"/> (excluded), outermost first.</summary>
    public static IReadOnlyList<string> FoldersBetween(string root, string path)
    {
        var relative = DiskSpacePaths.RelativeSegments(path, root);
        if (relative is null || relative.Count < 2)
        {
            return [];
        }

        var list = new List<string>();
        var current = DiskSpacePaths.Normalize(root);
        for (int i = 0; i < relative.Count - 1; i++)
        {
            current = DiskSpacePaths.Combine(current, relative[i]);
            list.Add(current);
        }

        return list;
    }

    /// <summary>A plain absolute path on a local drive letter: no UNC or device path, no "." or "..", no wildcards, no 8.3 short names.</summary>
    private static string? WhyNotPlainLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "The location is empty.";
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return "Network and device paths are never touched.";
        }

        if (!DiskSpacePaths.IsLocalAbsolute(path) || DiskSpacePaths.HasRelativeSegments(path) || path.IndexOfAny(Wildcards) >= 0 ||
            path.IndexOf(':', 2) >= 0)
        {
            return "The path is not a plain local path.";
        }

        if (DiskSpacePaths.Segments(path).Any(IsShortName))
        {
            return "Short (8.3) folder names are not accepted; the full name is needed.";
        }

        return null;
    }

    /// <summary>"PROGRA~1" style names could resolve to a different folder than they appear to.</summary>
    private static bool IsShortName(string segment)
    {
        int tilde = segment.IndexOf('~');
        return tilde > 0 && tilde < segment.Length - 1 && char.IsAsciiDigit(segment[tilde + 1]);
    }
}
