using System.Globalization;
using System.Text;
using System.Text.Json;
using PcChecker.Core.Leftovers;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Backups of tasks deleted on the Scheduled tasks page: each definition is saved as
/// <c>%LOCALAPPDATA%\PcChecker\task-backups\task-&lt;yyyyMMdd-HHmmss&gt;-&lt;name&gt;.xml</c> and listed in
/// <c>task-backups.json</c>. The folder is in the user's profile, so every backup also gets a seal where only
/// administrators can write (the same SHA-256 seal the Leftovers page uses); a restore only registers XML that still
/// matches it, under the same task path. Pure apart from the environment lookup.
/// </summary>
public static class TaskBackupNaming
{
    public const string Prefix = "task-";
    public const string Extension = ".xml";
    public const string ListFile = "task-backups.json";
    private const int MaxIdLength = 96;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultDirectory(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var local = environment("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }

        return Path.Combine(local, "PcChecker", "task-backups");
    }

    /// <summary><c>task-20261027-143005-adobe-acrobat-update-task</c>.</summary>
    public static string BackupId(DateTimeOffset created, string taskPath) =>
        Prefix + created.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + LeftoverBackupNaming.Slug(TaskPaths.NameOf(taskPath));

    /// <summary>Only ids PC Checker creates: no separators or dots, so a restore never reads outside the folder.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.StartsWith(Prefix, StringComparison.Ordinal) &&
        id.Length <= MaxIdLength &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>
    /// A path a backup may be registered under again: rooted, no "." or ".." parts, no forbidden characters, and never
    /// in Microsoft's or PC Checker's folder (only other companies' tasks are deleted here).
    /// </summary>
    public static bool IsRestorablePath(string? taskPath) => LeftoverBackupNaming.IsRemovableTaskPath(taskPath);

    /// <summary>The bytes saved for a definition: UTF-16 with a byte-order mark, as Windows stores task files.</summary>
    public static byte[] Encode(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var preamble = Encoding.Unicode.GetPreamble();
        var body = Encoding.Unicode.GetBytes(xml);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    public static string Seal(ReadOnlySpan<byte> xml, string taskPath) => LeftoverBackupNaming.Seal(xml, taskPath);

    public static bool SealMatches(ReadOnlySpan<byte> xml, string taskPath, string? recorded) => LeftoverBackupNaming.SealMatches(xml, taskPath, recorded);

    public static string Serialize(IEnumerable<TaskBackupEntry> entries) => JsonSerializer.Serialize(entries.ToList(), JsonOptions);

    /// <summary>The backup list; unreadable JSON gives an empty list and entries with bad ids or paths are dropped.</summary>
    public static IReadOnlyList<TaskBackupEntry> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<TaskBackupEntry>>(json, JsonOptions) ?? [];
            return entries.Where(e => e is not null && IsValidId(e.Id) && IsRestorablePath(e.TaskPath))
                .OrderByDescending(e => e.Created)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
