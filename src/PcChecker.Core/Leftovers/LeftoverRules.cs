using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.RegistryHealth;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// The decisions around a scan that are not scoring: whether a trace belongs to the program just uninstalled, when a
/// service or task counts as pointing at a missing program, ordering, and the summaries the page shows. Pure.
/// </summary>
public static class LeftoverRules
{
    /// <summary>The only service state in which a leftover service may be deleted (as reported by WMI, always English).</summary>
    public const string StoppedState = "Stopped";

    /// <summary>
    /// True when a folder or key named <paramref name="name"/> at <paramref name="path"/> belongs to the focus program:
    /// it is (or holds, or sits in) the recorded install folder, or its name closely matches the program or its maker.
    /// The install folder only counts when it is specific (see <see cref="LeftoverProgramIndex.IsSpecificLocation"/>):
    /// a program that recorded "C:\" or one of the scanned <paramref name="roots"/> does not claim everything in it.
    /// </summary>
    public static bool MatchesFocus(string? name, string? path, LeftoverFocus? focus, IEnumerable<string>? roots = null)
    {
        if (focus is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(path) && DiskSpacePaths.IsLocalAbsolute(path) &&
            LeftoverProgramIndex.IsSpecificLocation(focus.InstallLocation, roots) &&
            (DiskSpacePaths.IsSameOrInside(path, focus.InstallLocation!.Trim().Trim('"')) || DiskSpacePaths.IsSameOrInside(focus.InstallLocation.Trim().Trim('"'), path)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name) || LeftoverNameMatcher.IsGenericName(name))
        {
            return false;
        }

        return LeftoverNameMatcher.IsStrongMatch(name, focus.ProgramName) ||
               (!LeftoverNameMatcher.IsGenericName(focus.Publisher) && LeftoverNameMatcher.IsStrongMatch(name, focus.Publisher));
    }

    /// <summary>
    /// The missing program a scheduled task starts, or null when the task should be left alone: it has no Exec action,
    /// has other kinds of actions, or any of its programs exists or cannot be proven missing.
    /// </summary>
    public static string? MissingTaskTarget(LeftoverTaskActions? actions, Func<string, string?> extractPath, Func<string, TargetState> evaluate)
    {
        ArgumentNullException.ThrowIfNull(extractPath);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (actions is null || actions.HasOtherActions || actions.Commands.Count == 0)
        {
            return null;
        }

        string? first = null;
        foreach (var command in actions.Commands)
        {
            var path = extractPath(command);
            if (path is null || evaluate(path) != TargetState.Missing)
            {
                return null;
            }

            first ??= path;
        }

        return first;
    }

    /// <summary>
    /// True for the roots that hold settings and saved games rather than programs (AppData\Local, Roaming and
    /// LocalLow): games and portable programs with no uninstall entry keep their data there, so a folder in them is
    /// never rated High on its age alone.
    /// </summary>
    public static bool IsUserDataRoot(LeftoverRoot root) =>
        root is LeftoverRoot.LocalAppData or LeftoverRoot.RoamingAppData or LeftoverRoot.LocalLowAppData;

    /// <summary>Variables that expand to a different folder for every account.</summary>
    private static readonly string[] PerUserVariables =
        ["%LOCALAPPDATA%", "%APPDATA%", "%USERPROFILE%", "%HOMEPATH%", "%HOMEDRIVE%", "%HOMESHARE%", "%TEMP%", "%TMP%", "%ONEDRIVE%", "%USERNAME%"];

    /// <summary>True when a command uses a per-user variable such as %LOCALAPPDATA%.</summary>
    public static bool UsesPerUserVariables(string? command) =>
        !string.IsNullOrWhiteSpace(command) && PerUserVariables.Any(v => command.Contains(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a task principal is the account PC Checker runs as: the same SID, the same DOMAIN\user, or the same
    /// user name without a domain. A missing principal, a group or another account is not.
    /// </summary>
    public static bool IsCurrentAccount(string? principalUserId, string? currentUser, string? currentSid)
    {
        if (string.IsNullOrWhiteSpace(principalUserId))
        {
            return false;
        }

        var id = principalUserId.Trim();
        if (!string.IsNullOrWhiteSpace(currentSid) && id.Equals(currentSid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(currentUser) || id.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var user = currentUser.Trim();
        if (id.Equals(user, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "alex" against "PC\alex": only a bare name may match the name part.
        int slash = user.LastIndexOf('\\');
        return !id.Contains('\\') && slash >= 0 && id.Equals(user[(slash + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a task's programs cannot be checked from this account: a command uses a per-user variable and the task
    /// runs as another account (or a group, or SYSTEM). Expanding it with PC Checker's own profile would make a file in
    /// that account's profile look missing, so such a task is never reported.
    /// </summary>
    public static bool TaskBelongsToAnotherProfile(LeftoverTaskActions? actions, string? currentUser, string? currentSid) =>
        actions is not null && actions.Commands.Any(UsesPerUserVariables) && !IsCurrentAccount(actions.UserId, currentUser, currentSid);

    /// <summary>True for a service program inside the Windows folder: those belong to Windows and are never reported.</summary>
    public static bool IsWindowsImage(string? imagePath, string windowsDirectory) =>
        !string.IsNullOrWhiteSpace(imagePath) && !string.IsNullOrWhiteSpace(windowsDirectory) &&
        DiskSpacePaths.IsLocalAbsolute(imagePath) && DiskSpacePaths.IsSameOrInside(imagePath, windowsDirectory);

    /// <summary>
    /// True for a program inside a folder whose name starts with "Microsoft" or "Windows" (Windows Media Player,
    /// Microsoft\EdgeUpdate, ProgramData\Microsoft\Windows Defender Advanced Threat Protection…): those belong to
    /// Windows features or Microsoft products, which come and go with Windows, and are never reported.
    /// </summary>
    public static bool IsMicrosoftImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !DiskSpacePaths.IsLocalAbsolute(imagePath))
        {
            return false;
        }

        var segments = DiskSpacePaths.Normalize(imagePath).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Skip(1).SkipLast(1).Any(s =>
            s.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Windows", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for a folder named like a version ("120.0.6099.71", "v2.1", "app-1.4.2") that self-updating programs install into.</summary>
    public static bool IsVersionFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^(app-)?v?\d+(\.\d+){1,3}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The folder of the program a file belongs to: its parent folder, skipping version folders, so
    /// <c>…\Google\Chrome\Application\120.0.6099.71\elevation_service.exe</c> gives <c>…\Google\Chrome\Application</c>.
    /// A missing version folder means an update, not a removed program. Null when the path has no parent.
    /// </summary>
    public static string? ProgramFolderOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var folder = DiskSpacePaths.ParentOf(filePath);
        while (folder is not null && IsVersionFolderName(folder[(folder.LastIndexOfAny(['\\', '/']) + 1)..]) && DiskSpacePaths.ParentOf(folder) is { } up && !DiskSpacePaths.IsDriveRoot(up))
        {
            folder = up;
        }

        return folder;
    }

    /// <summary>A service may be deleted only when its program is missing and it is stopped.</summary>
    public static bool CanDeleteService(bool imageMissing, string? state) =>
        imageMissing && string.Equals(state?.Trim(), StoppedState, StringComparison.OrdinalIgnoreCase);

    /// <summary>Findings in display order: most confident first, the focus program's traces first within that, then by kind and name.</summary>
    public static IReadOnlyList<LeftoverItem> Order(IEnumerable<LeftoverItem> items) =>
        items.OrderByDescending(i => i.Confidence)
            .ThenByDescending(i => i.MatchesFocus)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Plain name of a kind, singular or plural.</summary>
    public static string KindName(LeftoverKind kind, int count = 1) => (kind, count == 1) switch
    {
        (LeftoverKind.Folder, true) => "folder",
        (LeftoverKind.Folder, false) => "folders",
        (LeftoverKind.EmptyFolder, true) => "empty folder",
        (LeftoverKind.EmptyFolder, false) => "empty folders",
        (LeftoverKind.RegistryKey, true) => "registry key",
        (LeftoverKind.RegistryKey, false) => "registry keys",
        (LeftoverKind.Service, true) => "service",
        (LeftoverKind.Service, false) => "services",
        (LeftoverKind.ScheduledTask, true) => "scheduled task",
        (LeftoverKind.ScheduledTask, false) => "scheduled tasks",
        (LeftoverKind.Shortcut, true) => "shortcut",
        _ => "shortcuts",
    };

    /// <summary>"2 folders (1.4 GB), 1 registry key and 1 scheduled task".</summary>
    public static string Summarize(IEnumerable<LeftoverItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var parts = items.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g =>
        {
            int count = g.Count();
            long bytes = g.Sum(i => i.SizeBytes ?? 0);
            var text = $"{count} {KindName(g.Key, count)}";
            return g.Key is LeftoverKind.Folder && bytes > 0 ? $"{text} ({ByteSize.Format(bytes)})" : text;
        }).ToList();

        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    /// <summary>The headline after a removal.</summary>
    public static string DescribeResult(LeftoverRemovalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcomes.Count == 0)
        {
            return "Nothing was removed.";
        }

        var text = result.Removed == 1 ? "Removed 1 leftover" : $"Removed {result.Removed} leftovers";
        if (result.FreedBytes > 0)
        {
            text += $", freeing {ByteSize.Format(result.FreedBytes)}";
        }

        text += ".";
        if (result.NotRemoved > 0)
        {
            text += result.NotRemoved == 1 ? " 1 item was kept; see the details below." : $" {result.NotRemoved} items were kept; see the details below.";
        }

        return text;
    }

    /// <summary>The registry folders vendor keys are read from: 64-bit and 32-bit machine settings and the user's settings.</summary>
    public static IReadOnlyList<(string Hive, string Path)> RegistryRoots { get; } =
    [
        ("HKLM", "SOFTWARE"),
        ("HKLM", @"SOFTWARE\WOW6432Node"),
        ("HKCU", "Software"),
    ];

    /// <summary>
    /// Why a registry key may not be removed as a leftover, or null when it may: it must be a vendor key or a product
    /// key under one of <see cref="RegistryRoots"/>, the vendor must not be allow-listed, and the Registry module's
    /// protected paths apply.
    /// </summary>
    public static string? WhyKeyRefused(string? hive, string? keyPath)
    {
        var h = RegistryPathTools.NormalizeHive(hive);
        if (h is not ("HKLM" or "HKCU") || string.IsNullOrWhiteSpace(keyPath))
        {
            return "Only program settings in HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER are removed.";
        }

        var path = keyPath.Trim().Trim('\\');
        if (path.Contains(@"\\", StringComparison.Ordinal) || path.Split('\\').Any(s => s.Length == 0 || s is "." or ".."))
        {
            return "The key path is not valid.";
        }

        var root = RegistryRoots
            .Where(r => r.Hive == h && path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Path.Length)
            .Select(r => r.Path)
            .FirstOrDefault();
        if (root is null)
        {
            return "Only keys directly under Software are removed.";
        }

        var rest = path[(root.Length + 1)..].Split('\\');
        if (rest.Length > 2)
        {
            return "Only a maker's key or one product key inside it is removed.";
        }

        if (LeftoverAllowList.IsAllowedRegistryVendor(rest[0]))
        {
            return $"{rest[0]} is used by Windows or by several programs and is never removed.";
        }

        if (rest.Length == 2 && LeftoverAllowList.IsSharedInsideVendor(rest[1]))
        {
            return $"{rest[1]} may be shared by other programs of the same maker and is never removed on its own.";
        }

        // The Registry module's protected locations apply; its three-level minimum does not, because a maker's own
        // key (Software\Vendor) is exactly what a removed program leaves behind.
        if (ProtectedRegistryPaths.Reason(h, path) is { } reason)
        {
            return reason;
        }

        return ProtectedRegistryPaths.IsContainer(path) ? "This key holds the settings of many programs and is never removed." : null;
    }

    /// <summary>Splits a short key ("HKCU\Software\Vendor") into hive and path; null when it has no hive.</summary>
    public static (string Hive, string Path)? SplitKey(string? shortKey)
    {
        if (string.IsNullOrWhiteSpace(shortKey))
        {
            return null;
        }

        int cut = shortKey.IndexOf('\\');
        if (cut <= 0 || cut == shortKey.Length - 1)
        {
            return null;
        }

        var hive = RegistryPathTools.NormalizeHive(shortKey[..cut]);
        return hive is "HKLM" or "HKCU" or "HKCR" ? (hive, shortKey[(cut + 1)..]) : null;
    }

    /// <summary>A stable id for a finding, so a rescan keeps the user's ticks.</summary>
    public static string IdFor(LeftoverKind kind, string location) =>
        kind switch
        {
            LeftoverKind.Folder or LeftoverKind.EmptyFolder => "folder:",
            LeftoverKind.RegistryKey => "key:",
            LeftoverKind.Service => "service:",
            LeftoverKind.ScheduledTask => "task:",
            _ => "shortcut:",
        } + location.Trim().ToUpperInvariant();
}
