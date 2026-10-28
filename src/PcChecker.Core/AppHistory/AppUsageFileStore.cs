using System.Text;
using System.Text.Json;
using PcChecker.Core.History;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// PC Checker's own app usage recording on disk, inside the History folder
/// (<c>%LOCALAPPDATA%\PcChecker\history\apps</c>): one JSON Lines file per UTC day, named like the History files, one
/// line per app per period. Days before today are rolled up to one line per app per hour, and days past the History
/// retention setting are deleted. Plain .NET file access, so it is tested on any OS. Thread-safe.
/// </summary>
public sealed class AppUsageFileStore
{
    private const string TempExtension = ".tmp";
    private readonly object _gate = new();

    public AppUsageFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>The default folder: <c>%LOCALAPPDATA%\PcChecker\history\apps</c>.</summary>
    public static string DefaultDirectory() => Path.Combine(HistoryFileStore.DefaultDirectory(), "apps");

    /// <summary>Appends records, each to the file of the UTC day its period ended on.</summary>
    public void Append(IEnumerable<AppUsageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var byDay = records.GroupBy(r => HistoryRetention.DayOf(r.End)).ToList();
        if (byDay.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var day in byDay)
            {
                var text = new StringBuilder();
                foreach (var record in day)
                {
                    text.Append(AppUsageLineCodec.Format(record)).Append('\n');
                }

                AppendText(PathFor(day.Key), Encoding.UTF8.GetBytes(text.ToString()));
            }
        }
    }

    /// <summary>Every readable record whose period overlaps <paramref name="from"/>–<paramref name="to"/>, oldest first.</summary>
    public IReadOnlyList<AppUsageRecord> Read(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var result = new List<AppUsageRecord>();
        lock (_gate)
        {
            foreach (var (path, day) in DayFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (day < HistoryRetention.DayOf(from).AddDays(-1) || day > HistoryRetention.DayOf(to).AddDays(1))
                {
                    continue;
                }

                result.AddRange(ReadFile(path).Where(r => r.End >= from && r.Start <= to));
            }
        }

        return result.OrderBy(r => r.Start).ToList();
    }

    /// <summary>The first day with a recording, or null when there is none.</summary>
    public DateOnly? OldestDay()
    {
        lock (_gate)
        {
            var days = DayFiles().Select(f => f.Day).ToList();
            return days.Count == 0 ? null : days.Min();
        }
    }

    /// <summary>
    /// Deletes the days past <paramref name="retentionDays"/> and rolls days before <paramref name="today"/> up to one
    /// line per app per hour. Returns how many files were deleted and rewritten.
    /// </summary>
    public (int Deleted, int Compacted) Maintain(DateOnly today, int retentionDays, CancellationToken cancellationToken = default)
    {
        int deleted = 0, compacted = 0;
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return (0, 0);
            }

            foreach (var temp in System.IO.Directory.EnumerateFiles(Directory, "*" + HistoryRetention.Extension + TempExtension))
            {
                TryDelete(temp);
            }

            var files = DayFiles().ToList();
            var expired = HistoryRetention.Expired(files.Select(f => f.Day), today, retentionDays).ToHashSet();
            foreach (var (path, day) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expired.Contains(day))
                {
                    deleted += TryDelete(path) ? 1 : 0;
                    continue;
                }

                if (day >= today)
                {
                    continue;
                }

                var (records, lines) = ReadFileCounted(path);
                var hourly = AppUsageRollup.ToHours(records);
                if (hourly.Count == lines)
                {
                    continue;
                }

                var temp = path + TempExtension;
                File.WriteAllText(temp, string.Concat(hourly.Select(r => AppUsageLineCodec.Format(r) + "\n")), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                compacted++;
            }
        }

        return (deleted, compacted);
    }

    /// <summary>Deletes every day of the recording. Returns the number of files deleted; throws when one could not be.</summary>
    public int DeleteAll()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return 0;
            }

            int count = 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + HistoryRetention.Extension + "*"))
            {
                File.Delete(path);
                count++;
            }

            return count;
        }
    }

    private string PathFor(DateOnly day) => Path.Combine(Directory, HistoryRetention.FileName(day));

    private IEnumerable<(string Path, DateOnly Day)> DayFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            yield break;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + HistoryRetention.Extension))
        {
            if (HistoryRetention.TryParseFileName(Path.GetFileName(path), out var day))
            {
                yield return (path, day);
            }
        }
    }

    private static IEnumerable<AppUsageRecord> ReadFile(string path) => ReadFileCounted(path).Records;

    private static (List<AppUsageRecord> Records, int Lines) ReadFileCounted(string path)
    {
        var records = new List<AppUsageRecord>();
        int lines = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                lines++;
                if (AppUsageLineCodec.TryParse(line, out var record))
                {
                    records.Add(record);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read right now is skipped; the next read tries again.
        }

        return (records, lines);
    }

    private static void AppendText(string path, byte[] bytes)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length > 0)
                {
                    // A crash in the middle of a write leaves a line without its newline; start on a fresh line.
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                    {
                        stream.WriteByte((byte)'\n');
                    }
                }

                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return;
            }
            catch (IOException ex) when (attempt < HistoryFileStore.AppendAttempts && HistoryFileStore.IsSharingViolation(ex))
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>One recording line: <c>{"k":"c:\\...\\chrome.exe","s":1761600000,"e":1761600600,"c":12.5,"r":1024,"w":2048}</c>.</summary>
public static class AppUsageLineCodec
{
    public static string Format(AppUsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("k", record.AppKey);
            writer.WriteNumber("s", record.Start.ToUnixTimeSeconds());
            writer.WriteNumber("e", record.End.ToUnixTimeSeconds());
            writer.WriteNumber("c", Math.Round(record.CpuSeconds, 3));
            if (record.DiskReadBytes is { } read)
            {
                writer.WriteNumber("r", read);
            }

            if (record.DiskWriteBytes is { } write)
            {
                writer.WriteNumber("w", write);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static bool TryParse(string? line, out AppUsageRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("k", out var key) || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString())
                || !root.TryGetProperty("s", out var start) || !start.TryGetInt64(out long s)
                || !root.TryGetProperty("e", out var end) || !end.TryGetInt64(out long e)
                || !root.TryGetProperty("c", out var cpu) || !cpu.TryGetDouble(out double c)
                || double.IsNaN(c) || c < 0 || e < s)
            {
                return false;
            }

            long? r = root.TryGetProperty("r", out var read) && read.TryGetInt64(out long rv) && rv >= 0 ? rv : null;
            long? w = root.TryGetProperty("w", out var write) && write.TryGetInt64(out long wv) && wv >= 0 ? wv : null;
            record = new AppUsageRecord(key.GetString()!, DateTimeOffset.FromUnixTimeSeconds(s), DateTimeOffset.FromUnixTimeSeconds(e), c, null, null, null, r, w);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Rolls PC Checker's own recording up to one record per app per UTC hour (older days need no more detail).</summary>
public static class AppUsageRollup
{
    public static IReadOnlyList<AppUsageRecord> ToHours(IEnumerable<AppUsageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .GroupBy(r => (Key: r.AppKey.ToLowerInvariant(), Hour: HourOf(r.Start)))
            .Select(g => new AppUsageRecord(g.First().AppKey, g.Min(r => r.Start), g.Max(r => r.End),
                Math.Round(g.Sum(r => r.CpuSeconds), 3), null, null, null,
                g.Any(r => r.DiskReadBytes is not null) ? g.Sum(r => r.DiskReadBytes ?? 0) : null,
                g.Any(r => r.DiskWriteBytes is not null) ? g.Sum(r => r.DiskWriteBytes ?? 0) : null))
            .OrderBy(r => r.Start)
            .ThenBy(r => r.AppKey, StringComparer.Ordinal)
            .ToList();
    }

    private static long HourOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / 3600;
}
