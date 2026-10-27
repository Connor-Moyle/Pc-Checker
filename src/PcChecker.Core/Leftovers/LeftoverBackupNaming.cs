using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcChecker.Core.RegistryHealth;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Names, location and integrity of leftover backups. Each removed scheduled task is exported to
/// <c>%LOCALAPPDATA%\PcChecker\leftover-backups\leftover-task-&lt;yyyyMMdd-HHmmss&gt;-&lt;task name&gt;.xml</c>, and
/// <c>restore-list.json</c> lists every undo entry (task backups here, registry backups in the Registry module's
/// folder). The folder is in the user's profile, so each task backup also gets a seal kept where only administrators
/// can write; a restore only registers a task whose XML and name still match it. Pure apart from the environment lookup.
/// </summary>
public static class LeftoverBackupNaming
{
    public const string TaskPrefix = "leftover-task-";
    public const string TaskExtension = ".xml";
    public const string RestoreListFile = "restore-list.json";
    private const int MaxSlugLength = 40;
    private const int MaxIdLength = 96;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The backup folder for the current user.</summary>
    public static string DefaultDirectory(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var local = environment("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }

        return Path.Combine(local, "PcChecker", "leftover-backups");
    }

    /// <summary>A sortable, readable id for the backup of a task: <c>leftover-task-20261027-143005-avast-update</c>.</summary>
    public static string TaskBackupId(DateTimeOffset created, string taskPath) =>
        TaskPrefix + created.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Slug(LeafName(taskPath));

    /// <summary>The first of <paramref name="id"/>, <c>id-2</c>, <c>id-3</c>… that <paramref name="exists"/> says is free.</summary>
    public static string Unique(string id, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(id))
        {
            return id;
        }

        for (int n = 2; n < 1000; n++)
        {
            var candidate = id + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return id + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// True for ids PC Checker itself creates. Anything else (separators, "..", other files) is refused, so a restore
    /// can never read outside the backup folder.
    /// </summary>
    public static bool IsValidTaskBackupId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.StartsWith(TaskPrefix, StringComparison.Ordinal) &&
        id.Length <= MaxIdLength &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>Reads the creation time back from a task backup id; null when it has none.</summary>
    public static DateTimeOffset? CreatedFromId(string? id)
    {
        if (!IsValidTaskBackupId(id) || id!.Length < TaskPrefix.Length + 15)
        {
            return null;
        }

        var stamp = id.Substring(TaskPrefix.Length, 15);
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local)
            : null;
    }

    /// <summary>
    /// A file-name-safe version of a task name: ASCII letters and digits, other characters as single hyphens, lower
    /// case, at most 40 characters; "task" when nothing is left.
    /// </summary>
    public static string Slug(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }

            if (sb.Length >= MaxSlugLength)
            {
                break;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "task" : slug;
    }

    /// <summary>The last part of a task path: <c>\Vendor\Update Task</c> → <c>Update Task</c>.</summary>
    public static string LeafName(string? taskPath)
    {
        var trimmed = (taskPath ?? string.Empty).TrimEnd('\\');
        int cut = trimmed.LastIndexOf('\\');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>
    /// True for a task path the leftover checks may touch: rooted (<c>\Name</c> or <c>\Folder\Name</c>), no "." or ".."
    /// parts, no characters Task Scheduler forbids, and never in Microsoft's own folder.
    /// </summary>
    public static bool IsRemovableTaskPath(string? taskPath)
    {
        if (string.IsNullOrWhiteSpace(taskPath) || !taskPath.StartsWith('\\') || taskPath.Length > 260 || taskPath.EndsWith('\\'))
        {
            return false;
        }

        var parts = taskPath[1..].Split('\\');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Trim() != p || p.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
                           p.Any(char.IsControl)))
        {
            return false;
        }

        return !parts[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && !parts[0].Equals("PC Checker", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The seal of a task backup: SHA-256 over the XML bytes and the task path, as hex.</summary>
    public static string Seal(ReadOnlySpan<byte> xml, string taskPath)
    {
        ArgumentNullException.ThrowIfNull(taskPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(xml);
        hash.AppendData(Encoding.UTF8.GetBytes("\n" + taskPath));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>True only when a seal was recorded and the backup still produces it.</summary>
    public static bool SealMatches(ReadOnlySpan<byte> xml, string taskPath, string? recordedSeal) =>
        !string.IsNullOrWhiteSpace(recordedSeal) &&
        string.Equals(Seal(xml, taskPath), recordedSeal.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string SerializeRestoreList(IEnumerable<LeftoverRestoreEntry> entries) =>
        JsonSerializer.Serialize(entries.ToList(), JsonOptions);

    /// <summary>Parses the restore list; unreadable or foreign JSON gives an empty list, and entries with bad ids are dropped.</summary>
    public static IReadOnlyList<LeftoverRestoreEntry> DeserializeRestoreList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<LeftoverRestoreEntry>>(json, JsonOptions) ?? [];
            return entries
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.BackupId))
                .Where(e => e.Kind == LeftoverRestoreKind.ScheduledTask
                    ? IsValidTaskBackupId(e.BackupId) && IsRemovableTaskPath(e.Location)
                    : RegistryBackupCatalog.IsValidId(e.BackupId))
                .GroupBy(e => e.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderByDescending(e => e.Created)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
