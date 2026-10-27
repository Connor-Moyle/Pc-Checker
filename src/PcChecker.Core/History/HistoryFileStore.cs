using System.Text;

namespace PcChecker.Core.History;

/// <summary>What one maintenance pass did.</summary>
public sealed record HistoryMaintenanceResult(int DeletedFiles, int CompactedFiles, int DroppedLines);

/// <summary>
/// The history files on disk: one JSON Lines file per UTC day in a single folder (by default
/// <c>%LOCALAPPDATA%\PcChecker\history</c>). Appends go to the end of the day's file and are flushed straight away, so
/// a crash loses at most the line being written; a cut-off line is skipped when reading and dropped at the next
/// roll-up. Old days are rewritten at a coarser resolution through a temporary file, and days past the retention
/// setting are deleted. Plain .NET file access, so it works (and is tested) on any OS. Thread-safe.
/// </summary>
public sealed class HistoryFileStore
{
    private const string TempExtension = ".tmp";
    private readonly object _gate = new();

    public HistoryFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>The default folder: <c>%LOCALAPPDATA%\PcChecker\history</c>.</summary>
    public static string DefaultDirectory() => Path.Combine(Settings.JsonSettingsService.DefaultDirectory(), "history");

    public string PathFor(DateOnly day) => Path.Combine(Directory, HistoryRetention.FileName(day));

    /// <summary>Appends one sample to the file of the UTC day it belongs to.</summary>
    public void Append(HistorySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var line = Encoding.UTF8.GetBytes(HistoryLineCodec.Format(sample) + "\n");
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var stream = new FileStream(PathFor(HistoryRetention.DayOf(sample.Time)), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length > 0)
            {
                // A crash in the middle of a write leaves a line without its newline; start on a fresh line so the
                // new sample is not glued to the broken one.
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    stream.WriteByte((byte)'\n');
                }
            }

            stream.Seek(0, SeekOrigin.End);
            stream.Write(line);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Every readable sample from <paramref name="from"/> to <paramref name="to"/> (inclusive), oldest first.</summary>
    public HistoryData Read(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var samples = new List<HistorySample>();
        int corrupt = 0;
        foreach (var day in HistoryRetention.DaysBetween(from, to))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (daySamples, dayCorrupt) = ReadDay(day);
            corrupt += dayCorrupt;
            samples.AddRange(daySamples.Where(s => s.Time >= from && s.Time <= to));
        }

        return new HistoryData(from, to, samples.OrderBy(s => s.Time).ToList(), corrupt);
    }

    /// <summary>Every day that has a history file, oldest first.</summary>
    public IReadOnlyList<DateOnly> Days()
    {
        lock (_gate)
        {
            return DayFiles().Select(f => f.Day).Order().ToList();
        }
    }

    public HistoryStorageInfo GetInfo()
    {
        lock (_gate)
        {
            var files = DayFiles().ToList();
            if (files.Count == 0)
            {
                return HistoryStorageInfo.None;
            }

            long bytes = 0;
            foreach (var (path, _) in files)
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }

            return new HistoryStorageInfo(files.Count, bytes, files.Min(f => f.Day));
        }
    }

    /// <summary>
    /// Deletes the days past <paramref name="retentionDays"/> and rewrites older days at their roll-up resolution
    /// (15 minutes after a week, an hour after 30 days). Today's file is never touched.
    /// </summary>
    public HistoryMaintenanceResult Maintain(DateOnly today, int retentionDays, CancellationToken cancellationToken = default)
    {
        int deleted = 0, compacted = 0, dropped = 0;
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return new HistoryMaintenanceResult(0, 0, 0);
            }

            DeleteLeftoverTempFiles();
            var files = DayFiles().ToList();
            var expired = HistoryRetention.Expired(files.Select(f => f.Day), today, retentionDays).ToHashSet();
            foreach (var (path, day) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expired.Contains(day))
                {
                    if (TryDelete(path))
                    {
                        deleted++;
                    }

                    continue;
                }

                if (day >= today || HistoryRollup.StorageBucketFor(day, today) is not { } bucket)
                {
                    continue;
                }

                var (samples, corrupt) = ReadDayUnlocked(path);
                if (corrupt == 0 && !HistoryRollup.NeedsCompaction(samples, bucket))
                {
                    continue;
                }

                Rewrite(path, HistoryRollup.Compact(samples, bucket));
                compacted++;
                dropped += corrupt;
            }
        }

        return new HistoryMaintenanceResult(deleted, compacted, dropped);
    }

    private (IReadOnlyList<HistorySample> Samples, int Corrupt) ReadDay(DateOnly day)
    {
        lock (_gate)
        {
            return ReadDayUnlocked(PathFor(day));
        }
    }

    private static (IReadOnlyList<HistorySample> Samples, int Corrupt) ReadDayUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return ([], 0);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return HistoryLineCodec.ParseLines(lines);
        }
        catch (IOException)
        {
            return ([], 0);
        }
        catch (UnauthorizedAccessException)
        {
            return ([], 0);
        }
    }

    private static void Rewrite(string path, IReadOnlyList<HistorySample> samples)
    {
        var temp = path + TempExtension;
        var sb = new StringBuilder();
        foreach (var sample in samples)
        {
            sb.Append(HistoryLineCodec.Format(sample)).Append('\n');
        }

        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

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

    private void DeleteLeftoverTempFiles()
    {
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + HistoryRetention.Extension + TempExtension))
        {
            TryDelete(path);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
