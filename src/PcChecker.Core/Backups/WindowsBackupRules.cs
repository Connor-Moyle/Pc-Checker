using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Backups;

/// <summary>
/// Pure rules for the read-only "Windows backups" card: File History's configuration files, which personal folders
/// OneDrive keeps (the folders sit inside the OneDrive folder once folder backup is on) and the system image versions
/// <c>wbadmin get versions</c> lists. Nothing here depends on the Windows display language.
/// </summary>
public static partial class WindowsBackupRules
{
    /// <summary>File History keeps its settings here once it has been set up for the user.</summary>
    public const string FileHistoryConfigFolder = @"Microsoft\Windows\FileHistory\Configuration";

    /// <summary>Opens File History (it is hidden from the Windows 11 Settings app but still works).</summary>
    public const string FileHistoryCommand = "control.exe";

    public const string FileHistoryArguments = "/name Microsoft.FileHistory";

    /// <summary>The Windows Backup page of the Settings app (Windows 11) or the Backup page (Windows 10).</summary>
    public const string WindowsBackupUri = "ms-settings:backup";

    /// <summary>Backup and Restore (Windows 7), where system images are made and restored.</summary>
    public const string BackupAndRestoreArguments = "/name Microsoft.BackupAndRestoreCenter";

    /// <summary>
    /// wbadmin names each backup version by its time in a fixed form, "MM/dd/yyyy-HH:mm" (UTC), whatever the display
    /// language; only the words around it are translated.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(\d{2})/(\d{2})/(\d{4})-(\d{2}):(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdentifier();

    /// <summary>File History is set up when its configuration folder holds a Config*.xml file.</summary>
    public static BackupFeatureState FileHistoryState(bool folderExists, IEnumerable<string>? fileNames)
    {
        if (!folderExists)
        {
            return BackupFeatureState.Off;
        }

        return (fileNames ?? []).Any(n => Path.GetFileName(n).StartsWith("Config", StringComparison.OrdinalIgnoreCase)
                                        && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ? BackupFeatureState.On
            : BackupFeatureState.Off;
    }

    /// <summary>
    /// The personal folders (name → current path) that sit inside one of the OneDrive folders, so OneDrive backs them up.
    /// Paths are compared whole folder by whole folder, ignoring case and a trailing backslash.
    /// </summary>
    public static IReadOnlyList<string> FoldersInOneDrive(IEnumerable<KeyValuePair<string, string?>> folders, IEnumerable<string?> oneDriveRoots)
    {
        var roots = (oneDriveRoots ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Normalize(r!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            return [];
        }

        return (folders ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Where(f => roots.Any(root => IsInside(Normalize(f.Value!), root)))
            .Select(f => f.Key)
            .ToList();
    }

    /// <summary>"Desktop, Documents and Pictures" style list of folder names.</summary>
    public static string JoinNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>The backup versions in wbadmin's output, newest first, as UTC times.</summary>
    public static IReadOnlyList<DateTimeOffset> ParseWbadminVersions(string? output)
    {
        var list = new List<DateTimeOffset>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return list;
        }

        foreach (Match m in VersionIdentifier().Matches(output))
        {
            int month = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int hour = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12 || year < 1 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
            {
                continue;
            }

            var time = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
            if (!list.Contains(time))
            {
                list.Add(time);
            }
        }

        return list.OrderByDescending(t => t).ToList();
    }

    /// <summary>Plain words for the result of <c>wbadmin get versions</c>.</summary>
    public static SystemImageStatus DescribeSystemImages(int exitCode, string? output, DateTimeOffset now)
    {
        var versions = ParseWbadminVersions(output);
        if (versions.Count > 0)
        {
            var newest = versions[0];
            int days = Math.Max(0, (int)Math.Floor((now - newest).TotalDays));
            string age = days == 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
            return new SystemImageStatus(true, versions.Count, newest,
                versions.Count == 1 ? $"1 Windows backup found; it was made {age}." : $"{versions.Count} Windows backups found; the newest was made {age}.");
        }

        return exitCode == 0
            ? new SystemImageStatus(true, 0, null, "No system image backup was found on this PC's drives.")
            : new SystemImageStatus(true, 0, null, "No system image backup was found. Windows Backup and Restore has not made one, or its drive is not connected.");
    }

    private static string Normalize(string path) => path.Trim().TrimEnd('\\', '/').Replace('/', '\\');

    private static bool IsInside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
