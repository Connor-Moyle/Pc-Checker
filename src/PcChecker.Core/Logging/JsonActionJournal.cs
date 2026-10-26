using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Logging;

/// <summary>Stores <see cref="ActionRecord"/>s as JSON lines in <c>%LOCALAPPDATA%\PcChecker\actions.jsonl</c>.</summary>
public sealed class JsonActionJournal : IActionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonActionJournal()
        : this(Path.Combine(Settings.JsonSettingsService.DefaultDirectory(), "actions.jsonl"))
    {
    }

    public JsonActionJournal(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        JournalPath = journalPath;
    }

    public string JournalPath { get; }

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

        var result = new List<ActionRecord>(Math.Min(take, lines.Length));
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
            }
            catch (JsonException)
            {
                // Skip damaged lines rather than losing the whole journal.
            }
        }

        return result;
    }
}
