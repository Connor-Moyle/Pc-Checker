namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Which paths the Disk space page never offers to delete, and why. A path is refused when it is a protected location,
/// lies inside one, or contains one (deleting a parent would take the protected folder with it). Pure: the locations
/// come from the caller.
/// </summary>
public static class DiskSpaceProtection
{
    // Folders Windows keeps at the root of a drive (refused at any depth for the $ and System Volume ones).
    private static readonly string[] SystemFolderNamesAnywhere =
        ["System Volume Information", "$Recycle.Bin", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset", "$GetCurrent", "$Extend"];

    private static readonly string[] SystemFolderNamesAtRoot = ["Recovery", "Boot", "Config.Msi", "Documents and Settings", "EFI"];

    private static readonly string[] SystemFileNames =
        ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT", "BOOTSECT.BAK"];

    // Folders directly inside a profile's AppData: deleting one wipes the settings of every program.
    private static readonly string[] AppDataFolders = ["Local", "LocalLow", "Roaming"];

    /// <summary>
    /// A plain-English reason why <paramref name="path"/> must not be deleted, or null when it may be offered.
    /// </summary>
    public static string? WhyProtected(string path, ProtectedLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (string.IsNullOrWhiteSpace(path) || !DiskSpacePaths.IsLocalAbsolute(path) || DiskSpacePaths.HasRelativeSegments(path))
        {
            return "Only files and folders on a local drive can be removed.";
        }

        var p = DiskSpacePaths.Normalize(path);
        if (DiskSpacePaths.IsDriveRoot(p))
        {
            return "A whole drive cannot be removed.";
        }

        var segments = DiskSpacePaths.Segments(p);
        if (segments.Any(s => SystemFolderNamesAnywhere.Contains(s, StringComparer.OrdinalIgnoreCase))
            || SystemFolderNamesAtRoot.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return "Windows uses this folder to start, recover or keep restore points.";
        }

        if (segments[0].Equals("Windows.old", StringComparison.OrdinalIgnoreCase))
        {
            return "The previous Windows installation. Remove it with Windows Storage settings or Disk Cleanup.";
        }

        if (SystemFileNames.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
        {
            return "Windows uses this file for memory, hibernation or starting up. Change it in Windows settings instead.";
        }

        if (Touches(p, locations.WindowsDirectory))
        {
            return "Part of Windows.";
        }

        if (locations.ProgramDirectories.Any(d => Touches(p, d)))
        {
            return "Installed programs and their shared data live here. Uninstall programs from the Apps page instead.";
        }

        if (locations.AppDirectories.Any(d => Touches(p, d)))
        {
            return "PC Checker itself uses this folder.";
        }

        var profileReason = ProfileReason(p, locations.UsersRoot) ?? ProfileReason(p, ParentOrNull(locations.UserProfile));
        if (profileReason is not null)
        {
            return profileReason;
        }

        return null;
    }

    /// <summary>A reason from the file's own attributes (Windows marks its own files as System), or null.</summary>
    public static string? WhyProtected(FileAttributes attributes) =>
        (attributes & FileAttributes.System) != 0 ? "Windows marks this as a system file." : null;

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="location"/>, is inside it, or contains it.
    /// </summary>
    public static bool Touches(string path, string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        return DiskSpacePaths.IsSameOrInside(path, location) || DiskSpacePaths.IsSameOrInside(location, path);
    }

    /// <summary>
    /// The profile rule: the folder that holds the profiles, each profile, every item directly in a profile (Documents,
    /// Downloads, NTUSER.DAT…) and the Local, LocalLow and Roaming folders are refused; what is inside them is not.
    /// </summary>
    private static string? ProfileReason(string path, string? usersRoot)
    {
        if (string.IsNullOrWhiteSpace(usersRoot))
        {
            return null;
        }

        if (DiskSpacePaths.IsSameOrInside(usersRoot, path))
        {
            return "This folder holds the user profiles.";
        }

        var relative = DiskSpacePaths.RelativeSegments(path, usersRoot);
        if (relative is null)
        {
            return null;
        }

        if (relative.Count <= 2)
        {
            return "Part of a user profile. Open it and remove what is inside instead.";
        }

        if (relative.Count == 3 && relative[1].Equals("AppData", StringComparison.OrdinalIgnoreCase)
            && AppDataFolders.Contains(relative[2], StringComparer.OrdinalIgnoreCase))
        {
            return "Programs keep their settings here. Open it and remove what is inside instead.";
        }

        return null;
    }

    private static string? ParentOrNull(string? path) => string.IsNullOrWhiteSpace(path) ? null : DiskSpacePaths.ParentOf(path);
}
