using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.RegistryHealth;

/// <summary>The JSON sidecar written next to each .reg backup: what was backed up and why.</summary>
public sealed record RegistryBackupManifest(string Id, DateTimeOffset Created, int IssueCount, string Summary, IReadOnlyList<RegistryIssue> Issues);

/// <summary>
/// Naming, location and manifest (de)serialization of registry backups. Backups live in
/// <c>%LOCALAPPDATA%\PcChecker\registry-backups\&lt;id&gt;.reg</c> with a <c>&lt;id&gt;.json</c> sidecar. Pure apart from
/// the environment lookup passed in.
/// </summary>
public static class RegistryBackupCatalog
{
    public const string FilePrefix = "registry-backup-";
    public const string RegExtension = ".reg";
    public const string ManifestExtension = ".json";

    /// <summary>Backups older than this are offered for cleanup (never removed automatically).</summary>
    public const int OldBackupDays = 90;

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

        return Path.Combine(local, "PcChecker", "registry-backups");
    }

    /// <summary>A sortable id for a backup taken at <paramref name="created"/>: <c>registry-backup-20251027-143005</c>.</summary>
    public static string NewId(DateTimeOffset created) =>
        FilePrefix + created.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// True for ids PC Checker itself creates. Anything else (path separators, "..", other files) is refused, so a
    /// restore or delete can never reach outside the backup folder.
    /// </summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.StartsWith(FilePrefix, StringComparison.Ordinal) &&
        id.Length <= 64 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>Reads the creation time back from an id; null when the id has no timestamp.</summary>
    public static DateTimeOffset? CreatedFromId(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        string stamp = id[FilePrefix.Length..];
        if (stamp.Length > 15)
        {
            stamp = stamp[..15];
        }

        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local)
            : null;
    }

    /// <summary>Backups created more than <paramref name="days"/> days before <paramref name="now"/>.</summary>
    public static IReadOnlyList<RegistryBackup> OlderThan(IEnumerable<RegistryBackup> backups, DateTimeOffset now, int days = OldBackupDays) =>
        backups.Where(b => now - b.Created > TimeSpan.FromDays(days)).ToList();

    public static string SerializeManifest(RegistryBackupManifest manifest) => JsonSerializer.Serialize(manifest, JsonOptions);

    /// <summary>Parses a sidecar; returns null for unreadable or foreign JSON instead of throwing.</summary>
    public static RegistryBackupManifest? DeserializeManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<RegistryBackupManifest>(json, JsonOptions);
            return manifest is not null && IsValidId(manifest.Id) ? manifest with { Issues = manifest.Issues ?? [] } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The list entry for a backup, from its manifest when there is one.</summary>
    public static RegistryBackup ToBackup(string id, string regFilePath, RegistryBackupManifest? manifest, DateTimeOffset fallbackCreated) =>
        manifest is null
            ? new RegistryBackup(id, CreatedFromId(id) ?? fallbackCreated, regFilePath, 0, "Registry backup")
            : new RegistryBackup(id, manifest.Created, regFilePath, manifest.IssueCount, manifest.Summary);
}
