using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Crashes.Dumps;
using PcChecker.Core.Health;
using PcChecker.Core.History;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;
using PcChecker.Core.WindowsUpdate;

using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>
/// Reads crashes from the System and Application event logs (EventLogReader with XPath filters; values taken from the
/// event XML, never from the translated message) and the dump folders (%SystemRoot%\Minidump, MEMORY.DMP,
/// LiveKernelReports\WATCHDOG). Read-only. Every source is guarded: a missing log or an unreadable folder becomes a
/// warning and the rest still loads. Around the crashes it also reads WHEA, disk, sleep/wake, start and driver-install
/// events and the end of setupapi.dev.log (<see cref="CrashReport.SystemEvents"/>), and on request the History samples,
/// the Action Journal and the Windows Update history (<see cref="LoadContextAsync"/>) and the blamed driver's file and
/// device (<see cref="InspectDriverAsync"/>).
/// </summary>
public sealed partial class WindowsCrashService : ICrashService
{
    private const int MaxEventsPerLog = 2000;
    private const int MaxContextEvents = 4000;
    private const int MaxCorrectedWheaEvents = 2000;
    private const int MaxDumps = 200;

    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsCrashService> _logger;
    private readonly IActionJournal? _journal;
    private readonly IWindowsUpdateService? _updates;
    private readonly IHistoryService? _history;
    private readonly string _windows;

    public WindowsCrashService(ISettingsService settings, ILogger<WindowsCrashService> logger)
        : this(settings, logger, null, null, null)
    {
    }

    public WindowsCrashService(ISettingsService settings, ILogger<WindowsCrashService> logger, IActionJournal? journal, IWindowsUpdateService? updates,
        IHistoryService? history)
    {
        _settings = settings;
        _logger = logger;
        _journal = journal;
        _updates = updates;
        _history = history;
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

            progress?.Report(new ProgressUpdate(T("Counting hardware error reports…"), 75));
            int? whea = CountWhea(cancellationToken);

            progress?.Report(new ProgressUpdate(T("Reading events around the crashes…"), 85));
            var system = ReadXml("System", CrashEventMapper.ContextQuery(CrashRules.WindowDays), MaxContextEvents, CrashEventMapper.MapSystemEvent, warnings, cancellationToken);
            system.AddRange(ReadXml("System", CrashEventMapper.CorrectedWheaQuery(CrashRules.WindowDays), MaxCorrectedWheaEvents, CrashEventMapper.MapSystemEvent, warnings, cancellationToken));
            system.AddRange(CrashSystemEventRules.FromSetupApi(ReadSetupApiLog(now)));

            var report = new CrashReport
            {
                Captured = now,
                Events = CrashSystemEventRules.AttachWhea(CrashRules.Merge(events, dumps, now), system),
                WheaErrors = whea,
                OverclockApplied = CrashRules.IsOverclockApplied(_settings.Current.Overclock),
                Warnings = warnings,
                SystemEvents = system.OrderBy(e => e.Time).ToList(),
            };
            Last = report;
            Scanned?.Invoke(this, report);
            return report;
        }, cancellationToken);

    public Task<CrashGlance> GetGlanceAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var solved = SolvedIds();
            if (Last is { } last && DateTimeOffset.Now - last.Captured < TimeSpan.FromMinutes(10))
            {
                return CrashRules.Glance(last.Events, DateTimeOffset.Now, CrashRules.AttentionDays, solved);
            }

            var now = DateTimeOffset.Now;
            var events = ReadLog("System", CrashEventMapper.SystemQuery(CrashRules.AttentionDays), [], cancellationToken);

            // Merge with the dump files as the full scan does, so a crash gets the same id here as on the Crashes page
            // (a dump-only blue screen would otherwise be its Kernel-Power 41 event) and "Mark as solved" matches.
            var dumps = ReadDumps(now, [], cancellationToken);
            return CrashRules.Glance(CrashRules.Merge(events, dumps, now, CrashRules.AttentionDays), now, CrashRules.AttentionDays, solved);
        }, cancellationToken);

    private IReadOnlyCollection<string> SolvedIds()
    {
        try
        {
            return _settings.Current.Crashes?.SolvedIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Solved crash ids could not be read.");
            return [];
        }
    }

    private List<CrashEvent> ReadLog(string log, string xpath, List<CrashWarning> warnings, CancellationToken ct) =>
        ReadXml(log, xpath, MaxEventsPerLog, CrashEventMapper.Map, warnings, ct);

    private List<TItem> ReadXml<TItem>(string log, string xpath, int max, Func<EventXmlRecord, TItem?> map, List<CrashWarning> warnings, CancellationToken ct)
        where TItem : class
    {
        var result = new List<TItem>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int read = 0;
            for (EventRecord? record = reader.ReadEvent(); record is not null && read < max; record = reader.ReadEvent())
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

                    if (EventXmlRecord.Parse(xml) is { } parsed && map(parsed) is { } item)
                    {
                        result.Add(item);
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
