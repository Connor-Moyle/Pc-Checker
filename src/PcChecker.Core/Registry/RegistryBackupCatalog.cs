using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.RegistryHealth;

/// <summary>The JSON sidecar written next to each .reg backup: what was backed up and why.</summary>
public sealed record RegistryBackupManifest(string Id, DateTimeOffset Created, int IssueCount, string Summary, IReadOnlyList<RegistryIssue> Issues);

/// <summary>
/// Naming, location, manifest (de)serialization and integrity checks of registry backups. Backups live in
/// <c>%LOCALAPPDATA%\PcChecker\registry-backups\&lt;id&gt;.reg</c> with a <c>&lt;id&gt;.json</c> sidecar. Any program the
/// user runs can change that folder, so each backup also gets a seal (a hash of both files) kept where only
/// administrators can write; the elevated restore imports a backup only when its seal still matches and every key in
/// it belongs to an entry the backup was made for. Pure apart from the environment lookup passed in.
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

    /// <summary>The bytes of a .reg file: UTF-16 with a byte-order mark, like regedit and reg export write it.</summary>
    public static byte[] EncodeRegFile(string regText)
    {
        ArgumentNullException.ThrowIfNull(regText);
        return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(regText)];
    }

    /// <summary>The text of a backup file (.reg or manifest), whatever byte-order mark it has (UTF-8 without one).</summary>
    public static string DecodeText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The bytes of a manifest sidecar (UTF-8 without a byte-order mark).</summary>
    public static byte[] EncodeManifest(RegistryBackupManifest manifest) => new UTF8Encoding(false).GetBytes(SerializeManifest(manifest));

    /// <summary>The seal of a backup: a SHA-256 over the hashes of its .reg file and its manifest, as hex.</summary>
    public static string Seal(ReadOnlySpan<byte> regFile, ReadOnlySpan<byte> manifest)
    {
        Span<byte> both = stackalloc byte[2 * SHA256.HashSizeInBytes];
        SHA256.HashData(regFile, both[..SHA256.HashSizeInBytes]);
        SHA256.HashData(manifest, both[SHA256.HashSizeInBytes..]);
        return Convert.ToHexString(SHA256.HashData(both));
    }

    /// <summary>True only when a seal was recorded and the files still produce it.</summary>
    public static bool SealMatches(ReadOnlySpan<byte> regFile, ReadOnlySpan<byte> manifest, string? recordedSeal) =>
        !string.IsNullOrWhiteSpace(recordedSeal) &&
        string.Equals(Seal(regFile, manifest), recordedSeal.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every key section of a .reg document, deletions (<c>[-…]</c>) included, that is neither one of the backed-up
    /// entries' keys nor below one. Empty for a backup PC Checker wrote; anything else must not be imported.
    /// </summary>
    public static IReadOnlyList<string> KeysOutsideBackup(string? regText, IEnumerable<RegistryIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var allowed = issues.Select(i => i.FullKeyPath.TrimEnd('\\')).ToList();
        var outside = new List<string>();
        foreach (var line in (regText ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            {
                continue;
            }

            var name = trimmed[1..^1].TrimStart('-').Trim().TrimEnd('\\');
            int cut = name.IndexOf('\\');
            var full = cut < 0 ? RegistryPathTools.FullKeyName(name, string.Empty) : RegistryPathTools.FullKeyName(name[..cut], name[(cut + 1)..]);
            bool inside = allowed.Any(a =>
                full.Equals(a, StringComparison.OrdinalIgnoreCase) || full.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase));
            if (!inside)
            {
                outside.Add(trimmed[1..^1]);
            }
        }

        return outside;
    }

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
