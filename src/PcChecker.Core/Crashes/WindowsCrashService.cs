using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Crashes.Dumps;
using PcChecker.Core.Health;
using PcChecker.Core.Settings;

using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>
/// Reads crashes from the System and Application event logs (EventLogReader with XPath filters; values taken from the
/// event XML, never from the translated message) and the dump folders (%SystemRoot%\Minidump, MEMORY.DMP,
/// LiveKernelReports\WATCHDOG). Read-only. Every source is guarded: a missing log or an unreadable folder becomes a
/// warning and the rest still loads.
/// </summary>
public sealed class WindowsCrashService : ICrashService
{
    private const int MaxEventsPerLog = 2000;
    private const int MaxDumps = 200;

    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsCrashService> _logger;
    private readonly string _windows;

    public WindowsCrashService(ISettingsService settings, ILogger<WindowsCrashService> logger)
    {
        _settings = settings;
        _logger = logger;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _windows = string.IsNullOrEmpty(windows) ? @"C:\Windows" : windows;
    }

    public CrashReport? Last { get; private set; }

    public event EventHandler<CrashReport>? Scanned;

    public string MinidumpFolder => Path.Combine(_windows, "Minidump");

    public Task<CrashReport> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var warnings = new List<CrashWarning>();
            var now = DateTimeOffset.Now;
            var events = new List<CrashEvent>();

            progress?.Report(new ProgressUpdate(T("Reading the System log…"), 10));
            events.AddRange(ReadLog("System", CrashEventMapper.SystemQuery(CrashRules.WindowDays), warnings, cancellationToken));
            progress?.Report(new ProgressUpdate(T("Reading the Application log…"), 35));
            events.AddRange(ReadLog("Application", CrashEventMapper.ApplicationQuery(CrashRules.WindowDays), warnings, cancellationToken));

            progress?.Report(new ProgressUpdate(T("Reading crash dump files…"), 60));
            var dumps = ReadDumps(now, warnings, cancellationToken);

            progress?.Report(new ProgressUpdate(T("Counting hardware error reports…"), 85));
            int? whea = CountWhea(cancellationToken);

            var report = new CrashReport
            {
                Captured = now,
                Events = CrashRules.Merge(events, dumps, now),
                WheaErrors = whea,
                OverclockApplied = CrashRules.IsOverclockApplied(_settings.Current.Overclock),
                Warnings = warnings,
            };
            Last = report;
            Scanned?.Invoke(this, report);
            return report;
        }, cancellationToken);

    public Task<CrashGlance> GetGlanceAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (Last is { } last && DateTimeOffset.Now - last.Captured < TimeSpan.FromMinutes(10))
            {
                return CrashRules.Glance(last.Events, DateTimeOffset.Now);
            }

            var now = DateTimeOffset.Now;
            var events = ReadLog("System", CrashEventMapper.SystemQuery(CrashRules.AttentionDays), [], cancellationToken);
            return CrashRules.Glance(CrashRules.Merge(events, [], now, CrashRules.AttentionDays), now);
        }, cancellationToken);

    private List<CrashEvent> ReadLog(string log, string xpath, List<CrashWarning> warnings, CancellationToken ct)
    {
        var result = new List<CrashEvent>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int read = 0;
            for (EventRecord? record = reader.ReadEvent(); record is not null && read < MaxEventsPerLog; record = reader.ReadEvent())
            {
                ct.ThrowIfCancellationRequested();
                using (record)
                {
                    read++;
                    string? xml;
                    try
                    {
                        xml = record.ToXml();
                    }
                    catch (EventLogException)
                    {
                        continue;
                    }

                    if (EventXmlRecord.Parse(xml) is { } parsed && CrashEventMapper.Map(parsed) is { } crash)
                    {
                        result.Add(crash);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EventLogNotFoundException)
        {
            // The log does not exist on this edition: nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the {Log} event log for crashes.", log);
            warnings.Add(new CrashWarning(T("The {0} event log could not be read ({1})."), log, ex.Message));
        }

        return result;
    }

    private List<DumpFile> ReadDumps(DateTimeOffset now, List<CrashWarning> warnings, CancellationToken ct)
    {
        var cutoff = now.AddDays(-CrashRules.WindowDays);
        var dumps = new List<DumpFile>();
        void AddFolder(string folder, bool watchdog)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.dmp")
                             .Where(f => new DateTimeOffset(f.LastWriteTime) >= cutoff)
                             .OrderByDescending(f => f.LastWriteTime)
                             .Take(MaxDumps))
                {
                    ct.ThrowIfCancellationRequested();
                    dumps.Add(new DumpFile(file.FullName, new DateTimeOffset(file.LastWriteTime), file.Length, DumpFileReader.ReadFile(file.FullName), watchdog));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                warnings.Add(new CrashWarning(T("{0} needs administrator rights to read, so crash dumps there were skipped."), folder));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dump folder {Folder} could not be read.", folder);
            }
        }

        AddFolder(MinidumpFolder, watchdog: false);
        AddFolder(Path.Combine(_windows, "LiveKernelReports", "WATCHDOG"), watchdog: true);
        try
        {
            var memory = new FileInfo(Path.Combine(_windows, "MEMORY.DMP"));
            if (memory.Exists && new DateTimeOffset(memory.LastWriteTime) >= cutoff)
            {
                dumps.Add(new DumpFile(memory.FullName, new DateTimeOffset(memory.LastWriteTime), memory.Length, DumpFileReader.ReadFile(memory.FullName)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "MEMORY.DMP could not be inspected.");
        }

        return dumps;
    }

    /// <summary>WHEA-Logger reports in the Health page's window; the Health page shows them in full.</summary>
    private int? CountWhea(CancellationToken ct)
    {
        try
        {
            long windowMs = HardwareErrorRules.WindowDays * 86_400_000L;
            string ids = string.Join(" or ", HardwareErrorRules.WheaEventIds.Select(i => $"EventID={i}"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and ({ids}) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]");
            using var reader = new EventLogReader(query);
            int count = 0;
            for (EventRecord? record = reader.ReadEvent(); record is not null && count < 10_000; record = reader.ReadEvent())
            {
                ct.ThrowIfCancellationRequested();
                record.Dispose();
                count++;
            }

            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WHEA events could not be counted.");
            return null;
        }
    }
}
