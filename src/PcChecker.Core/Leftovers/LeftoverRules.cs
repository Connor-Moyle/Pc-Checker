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
    /// </summary>
    public static bool MatchesFocus(string? name, string? path, LeftoverFocus? focus)
    {
        if (focus is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(focus.InstallLocation) &&
            DiskSpacePaths.IsLocalAbsolute(path) && DiskSpacePaths.IsLocalAbsolute(focus.InstallLocation) &&
            (DiskSpacePaths.IsSameOrInside(path, focus.InstallLocation) || DiskSpacePaths.IsSameOrInside(focus.InstallLocation, path)))
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

    /// <summary>True for a service program inside the Windows folder: those belong to Windows and are never reported.</summary>
    public static bool IsWindowsImage(string? imagePath, string windowsDirectory) =>
        !string.IsNullOrWhiteSpace(imagePath) && !string.IsNullOrWhiteSpace(windowsDirectory) &&
        DiskSpacePaths.IsLocalAbsolute(imagePath) && DiskSpacePaths.IsSameOrInside(imagePath, windowsDirectory);

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
