using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Health;

/// <summary>
/// Event-log, reliability-record, crash-dump and memory-diagnostic collectors of <see cref="WindowsHealthService"/>.
/// </summary>
public sealed partial class WindowsHealthService
{
    // ---------------------------------------------------------------- event log

    private void CollectSystemErrors(AnalysisContext ctx, CancellationToken ct)
    {
        var all = new List<SystemErrorEntry>();
        var notes = new List<string>();
        foreach (var log in new[] { "System", "Application" })
        {
            try
            {
                all.AddRange(ReadErrors(log, ErrorWindowDays, MaxErrorEntries, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the {Log} event log.", log);
                notes.Add($"{log} log: {Friendly(ex)}");
            }
        }

        ctx.SystemErrors.AddRange(all.OrderByDescending(e => e.Time).Take(MaxErrorEntries));
        ctx.Items.Add(HealthItemRules.SystemErrors(ctx.SystemErrors, ErrorWindowDays));
        foreach (var n in notes)
        {
            ctx.Warnings.Add("Event log errors: " + n);
        }
    }

    private static List<SystemErrorEntry> ReadErrors(string log, int days, int max, CancellationToken ct)
    {
        long windowMs = days * 86_400_000L;
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]")
        {
            ReverseDirection = true,
        };
        var result = new List<SystemErrorEntry>();
        using var reader = new EventLogReader(query);
        for (EventRecord? record = reader.ReadEvent(); record is not null && result.Count < max; record = reader.ReadEvent())
        {
            ct.ThrowIfCancellationRequested();
            using (record)
            {
                result.Add(new SystemErrorEntry(
                    record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now,
                    log,
                    record.ProviderName ?? "Unknown",
                    record.Level == 1 ? Severity.Critical : Severity.High,
                    record.Id,
                    DescribeEvent(record)));
            }
        }

        return result;
    }

    private static string DescribeEvent(EventRecord record)
    {
        string? text = null;
        try
        {
            text = record.FormatDescription();
        }
        catch (Exception)
        {
            // The provider's message DLL is missing or the event is malformed; fall back below.
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var props = record.Properties.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).Take(4);
                text = $"Event {record.Id} from {record.ProviderName}" + (props.Any() ? ": " + string.Join(", ", props) : string.Empty);
            }
            catch (Exception)
            {
                text = $"Event {record.Id} from {record.ProviderName}";
            }
        }

        var oneLine = Whitespace().Replace(text!, " ").Trim();
        return oneLine.Length <= 600 ? oneLine : oneLine[..600] + "…";
    }

    private void CollectMemoryDiagnostic(AnalysisContext ctx)
    {
        int? eventId = null;
        DateTimeOffset? when = null;
        string? message = null;
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1201 or EventID=1202)]]")
        {
            ReverseDirection = true,
        };
        using (var reader = new EventLogReader(query))
        using (var record = reader.ReadEvent())
        {
            if (record is not null)
            {
                eventId = record.Id;
                when = record.TimeCreated is { } t ? new DateTimeOffset(t) : null;
                message = DescribeEvent(record);
            }
        }

        ctx.Items.Add(HealthItemRules.MemoryDiagnostic(eventId, when, message, ctx.Now));
    }

    // ---------------------------------------------------------------- reliability

    private void CollectReliability(AnalysisContext ctx, CancellationToken ct)
    {
        var cutoff = ctx.Now.AddDays(-ReliabilityWindowDays);
        var events = new List<ReliabilityEvent>();

        // Filter server-side: the RAC store can hold years of records and WMI compares DMTF timestamps as strings.
        string since = ManagementDateTimeConverter.ToDmtfDateTime(cutoff.LocalDateTime);
        string wql = $"SELECT SourceName, EventIdentifier, TimeGenerated, Message FROM Win32_ReliabilityRecords WHERE TimeGenerated >= '{since}'";
        foreach (var row in Query(Cimv2, wql, ct))
        {
            if (!WmiPropertyMapper.TryParseDmtfDate(Str(row, "TimeGenerated"), out var time))
            {
                continue;
            }

            var when = new DateTimeOffset(time);
            if (when < cutoff)
            {
                continue;
            }

            var source = Str(row, "SourceName") ?? "Unknown";
            long id = Long(row, "EventIdentifier") ?? 0;
            var message = Whitespace().Replace(Str(row, "Message") ?? string.Empty, " ").Trim();
            events.Add(new ReliabilityEvent(when, source, ReliabilityClassifier.Classify(source, id), message.Length <= 400 ? message : message[..400] + "…"));
        }

        ctx.Reliability.AddRange(events.OrderByDescending(e => e.Time).Take(MaxReliabilityEntries));
    }

    // ---------------------------------------------------------------- crash dumps

    private void CollectCrashDumps(AnalysisContext ctx)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            windows = @"C:\Windows";
        }

        var files = new List<FileInfo>();
        var miniDir = Path.Combine(windows, "Minidump");
        try
        {
            if (Directory.Exists(miniDir))
            {
                files.AddRange(new DirectoryInfo(miniDir).EnumerateFiles("*.dmp"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Minidump folder could not be read.");
        }

        try
        {
            var memory = new FileInfo(Path.Combine(windows, "MEMORY.DMP"));
            if (memory.Exists)
            {
                files.Add(memory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MEMORY.DMP could not be inspected.");
        }

        foreach (var f in files.OrderByDescending(f => f.LastWriteTime))
        {
            ctx.CrashDumps.Add(new CrashDumpInfo(f.FullName, new DateTimeOffset(f.LastWriteTime), f.Length));
        }

        ctx.Items.Add(HealthItemRules.CrashDumps(ctx.CrashDumps, ctx.Now));
    }
}
