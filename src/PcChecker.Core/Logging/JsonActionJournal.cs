using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Logging;

/// <summary>
/// Stores <see cref="ActionRecord"/>s as JSON lines in <c>actions.jsonl</c> next to the settings file. Append-only and
/// thread-safe; damaged lines are skipped; the file is archived once it reaches <see cref="JournalRotation.DefaultLimitBytes"/>.
/// </summary>
public sealed class JsonActionJournal : IActionJournal
{
    public const string FileName = "actions.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;

    public JsonActionJournal()
        : this(Path.Combine(JsonSettingsService.DefaultDirectory(), FileName))
    {
    }

    /// <summary>Keeps the journal next to the active settings file (so demo mode never touches the real journal).</summary>
    public JsonActionJournal(ISettingsService settings, ILogger<JsonActionJournal> logger)
        : this(Path.Combine(settings.SettingsDirectory, FileName), logger)
    {
    }

    public JsonActionJournal(string journalPath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        JournalPath = journalPath;
        _logger = logger ?? NullLogger.Instance;
    }

    public string JournalPath { get; }

    /// <summary>Size at which the live file is archived; tests lower it.</summary>
    public long RotationLimitBytes { get; set; } = JournalRotation.DefaultLimitBytes;

    /// <summary>Lines that could not be parsed during the last <see cref="ReadAsync"/>.</summary>
    public int LastSkippedLines { get; private set; }

    public async Task RecordAsync(ActionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(JournalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            RotateIfNeeded(line.Length);
            await File.AppendAllTextAsync(JournalPath, line).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ActionRecord>> ReadAsync(int take = 500)
    {
        if (!File.Exists(JournalPath))
        {
            LastSkippedLines = 0;
            return [];
        }

        string[] lines;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lines = await File.ReadAllLinesAsync(JournalPath).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var result = new List<ActionRecord>(Math.Min(Math.Max(take, 0), lines.Length));
        int skipped = 0;
        for (int i = lines.Length - 1; i >= 0 && result.Count < take; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<ActionRecord>(lines[i], JsonOptions);
                if (record is not null)
                {
                    result.Add(record);
                }
                else
                {
                    skipped++;
                }
            }
            catch (JsonException)
            {
                // Skip damaged lines rather than losing the whole journal.
                skipped++;
            }
        }

        LastSkippedLines = skipped;
        if (skipped > 0)
        {
            _logger.LogWarning("Skipped {Count} damaged line(s) in the action journal {Path}.", skipped, JournalPath);
        }

        return result;
    }

    /// <summary>Archived journal files next to the live one, newest first.</summary>
    public IReadOnlyList<string> ArchivedFiles()
    {
        try
        {
            var dir = Path.GetDirectoryName(JournalPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return [];
            }

            return Directory.EnumerateFiles(dir, JournalRotation.ArchivePattern(JournalPath))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        try
        {
            var info = new FileInfo(JournalPath);
            if (!info.Exists || !JournalRotation.ShouldRotate(info.Length, incomingBytes, RotationLimitBytes))
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var archived = JournalRotation.RotatedPath(JournalPath, now);
            for (int sequence = 1; File.Exists(archived) && sequence < 1000; sequence++)
            {
                archived = JournalRotation.RotatedPath(JournalPath, now, sequence);
            }

            File.Move(JournalPath, archived);
            _logger.LogInformation("Action journal reached {Size} bytes and was archived to {Archive}.", info.Length, archived);
        }
        catch (Exception ex)
        {
            // Rotation is housekeeping; appending must still succeed.
            _logger.LogWarning(ex, "Could not archive the action journal; continuing with the current file.");
        }
    }
}
