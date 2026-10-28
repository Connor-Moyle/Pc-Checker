using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.RamTuning;

/// <summary>
/// Reads and writes the RAM tuning history (<c>ram-tuning.json</c> next to the settings). A missing or unreadable file
/// is an empty history, never an error. Times are stored as ISO 8601, so the file is locale independent.
/// </summary>
public static class RamTuningJournalFile
{
    public const string FileName = "ram-tuning.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(RamTuningJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return JsonSerializer.Serialize(journal, Options);
    }

    /// <summary>The journal in <paramref name="json"/>, or an empty one when the text is not a journal.</summary>
    public static RamTuningJournal Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RamTuningJournal();
        }

        try
        {
            var journal = JsonSerializer.Deserialize<RamTuningJournal>(json, Options) ?? new RamTuningJournal();
            journal.Checks ??= [];
            if (journal.Checks.Count > RamTuningJournal.MaxChecks)
            {
                journal.Checks.RemoveRange(RamTuningJournal.MaxChecks, journal.Checks.Count - RamTuningJournal.MaxChecks);
            }

            return journal;
        }
        catch (JsonException)
        {
            return new RamTuningJournal();
        }
    }

    public static async Task<RamTuningJournal> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) ? Deserialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : new RamTuningJournal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RamTuningJournal();
        }
    }

    /// <summary>Writes through a temporary file so a crash never leaves half a file.</summary>
    public static async Task SaveAsync(string path, RamTuningJournal journal, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, Serialize(journal), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
