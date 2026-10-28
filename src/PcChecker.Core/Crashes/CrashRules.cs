using PcChecker.Core.Crashes.Dumps;

using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>A dump file found on disk, with what its header said (null when it could not be read).</summary>
public sealed record DumpFile(string Path, DateTimeOffset FileTime, long Size, CrashDumpInfo? Info, bool IsGraphicsWatchdog = false)
{
    /// <summary>The crash time from the header, or the file's time.</summary>
    public DateTimeOffset Time => Info?.CrashTime ?? FileTime;
}

/// <summary>One program in the "Most crashing apps" list.</summary>
public sealed record AppCrashGroup(string AppName, int Crashes, int Hangs, DateTimeOffset Latest, string? TopModule, uint? TopExceptionCode)
{
    public int Total => Crashes + Hangs;
}

/// <summary>One day of the timeline, newest first.</summary>
public sealed record CrashDay(DateOnly Day, IReadOnlyList<CrashEvent> Events);

/// <summary>The four summary tiles: counts in the last <see cref="CrashRules.SummaryDays"/> days.</summary>
public sealed record CrashSummary(int BlueScreens, int SuddenShutdowns, int DriverResets, int AppCrashes);

/// <summary>
/// Pure rules for the Crashes page: merging events with dump files (one entry per incident), the timeline, the
/// summary counts, the "Most crashing apps" list and the filters.
/// </summary>
public static class CrashRules
{
    /// <summary>How far back the page looks.</summary>
    public const int WindowDays = 90;

    /// <summary>The summary tiles count this many days.</summary>
    public const int SummaryDays = 30;

    /// <summary>The Dashboard mentions blue screens and sudden shutdowns from this many days.</summary>
    public const int AttentionDays = 7;

    private const string OverclockProfileStockName = "Stock";

    /// <summary>A Kernel-Power 41 and a BugCheck event this close together are the same crash.</summary>
    public static readonly TimeSpan SameCrashWindow = TimeSpan.FromMinutes(60);

    /// <summary>An EventLog 6008 this close to a Kernel-Power 41 is the same shutdown.</summary>
    public static readonly TimeSpan SameShutdownWindow = TimeSpan.FromMinutes(15);

    /// <summary>A graphics watchdog dump this close to a Display 4101 event belongs to it.</summary>
    public static readonly TimeSpan SameResetWindow = TimeSpan.FromMinutes(5);

    /// <summary>How long after a crash Windows may write its BugCheck event (at the next start; the PC may stay off).</summary>
    public static readonly TimeSpan MaxReportDelay = TimeSpan.FromDays(7);

    /// <summary>
    /// Builds the page's list: one entry per incident, newest first, within <paramref name="days"/> of
    /// <paramref name="now"/>. Dumps are matched to their BugCheck event by path (or by stop code and time) and give
    /// it the crash time from the dump header; a MEMORY.DMP without a driver list also gets the small dump of the same
    /// crash. Dumps with no event become entries of their own unless they repeat a listed blue screen, a Kernel-Power 41
    /// that repeats a BugCheck is dropped, and an EventLog 6008 next to a Kernel-Power 41 is dropped.
    /// </summary>
    public static IReadOnlyList<CrashEvent> Merge(IEnumerable<CrashEvent> events, IEnumerable<DumpFile> dumps, DateTimeOffset now, int days = WindowDays)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(dumps);
        var cutoff = now.AddDays(-days);
        var list = events.Where(e => e.Time >= cutoff).ToList();
        var dumpList = dumps.ToList();
        var used = new HashSet<DumpFile>();

        var kernelDumps = dumpList.Where(d => !d.IsGraphicsWatchdog).ToList();

        // Windows writes the BugCheck event (and Kernel-Power 41, EventLog 6008) at the next start, which can be hours
        // after the crash. Entries move to the dump's crash time, so keep the logged time for matching those events.
        var loggedTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e.Kind != CrashKind.BlueScreen || e.EventId != 1001)
            {
                continue;
            }

            var dump = kernelDumps.FirstOrDefault(d => !used.Contains(d) && e.DumpPath is not null && SamePath(d.Path, e.DumpPath))
                ?? kernelDumps.Where(d => !used.Contains(d) && d.Info?.BugCheckCode == e.BugCheckCode && IsReportDelay(d.Time, e.Time))
                    .OrderBy(d => Math.Abs((d.Time - e.Time).Ticks)).FirstOrDefault();
            if (dump is null)
            {
                continue;
            }

            used.Add(dump);
            loggedTimes[e.Id] = e.Time;
            var merged = WithDump(e, dump);

            // With "Automatic memory dump" one crash writes MEMORY.DMP (named by the event, no driver list) and a small
            // dump in %SystemRoot%\Minidump. Use the small dump for the driver list and the "probably caused by" guess.
            if (dump.Info is not { Modules.Count: > 0 })
            {
                var small = FindCompanionDump(kernelDumps, used, merged.BugCheckCode, merged.Time);
                if (small is not null)
                {
                    used.Add(small);
                    var withSmall = WithDump(merged with { Blame = null }, small);
                    merged = withSmall with { DumpPath = merged.DumpPath, Time = merged.Time, Blame = withSmall.Blame ?? merged.Blame };
                }
            }

            list[i] = merged;
        }

        // Dumps without an event (the log was cleared or is too small): their own entries, unless a blue screen with the
        // same stop code is already listed at that time (a second dump of the same crash).
        var listedBlueScreens = list.Where(e => e.Kind == CrashKind.BlueScreen && e.EventId != 41).ToList();
        foreach (var d in kernelDumps.Where(d => !used.Contains(d) && d.Info?.BugCheckCode is not null && d.Time >= cutoff))
        {
            if (listedBlueScreens.Any(b => b.BugCheckCode == d.Info!.BugCheckCode && Near(b.Time, d.Time, SameCrashWindow)))
            {
                continue;
            }

            var entry = WithDump(new CrashEvent
            {
                Id = "dump-" + d.Path.ToUpperInvariant(),
                Kind = CrashKind.BlueScreen,
                Time = d.Time,
                Source = T("Memory dump file"),
                BugCheckCode = d.Info!.BugCheckCode,
            }, d);
            list.Add(entry);
            listedBlueScreens.Add(entry);
        }

        // A Kernel-Power 41 with a stop code repeats a blue screen already listed.
        var blueScreens = list.Where(e => e.Kind == CrashKind.BlueScreen && e.EventId != 41).ToList();
        list.RemoveAll(e => e.Kind == CrashKind.BlueScreen && e.EventId == 41
            && blueScreens.Any(b => (b.BugCheckCode == e.BugCheckCode || b.BugCheckCode is null) && NearLogged(b, e.Time, SameCrashWindow)));

        // EventLog 6008 next to a Kernel-Power 41 (with or without a stop code) is the same incident.
        var power = list.Where(e => e.EventId == 41).ToList();
        var bugChecks = list.Where(e => e.Kind == CrashKind.BlueScreen).ToList();
        list.RemoveAll(e => e.EventId == 6008
            && (power.Any(p => Near(p.Time, e.Time, SameShutdownWindow)) || bugChecks.Any(b => NearLogged(b, e.Time, SameShutdownWindow))));

        // Graphics watchdog dumps: attach to a Display 4101 event, or list on their own.
        var watchdogs = dumpList.Where(d => d.IsGraphicsWatchdog && d.Time >= cutoff).OrderBy(d => d.Time).ToList();
        foreach (var w in watchdogs)
        {
            int index = list.FindIndex(e => e.Kind == CrashKind.DriverReset && e.DumpPath is null && Near(e.Time, w.Time, SameResetWindow));
            if (index >= 0)
            {
                list[index] = list[index] with { DumpPath = w.Path, BugCheckCode = list[index].BugCheckCode ?? w.Info?.BugCheckCode, Parameters = w.Info?.Parameters ?? [] };
            }
            else
            {
                list.Add(new CrashEvent
                {
                    Id = "watchdog-" + w.Path.ToUpperInvariant(),
                    Kind = CrashKind.DriverReset,
                    Time = w.Time,
                    Source = T("Graphics driver report (LiveKernelReports)"),
                    DumpPath = w.Path,
                    BugCheckCode = w.Info?.BugCheckCode,
                    Parameters = w.Info?.Parameters ?? [],
                    Driver = w.Info?.Modules.FirstOrDefault(m => CrashBlameRules.IsGraphicsDriver(m.Name))?.Name,
                });
            }
        }

        return list.OrderByDescending(e => e.Time).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

        bool NearLogged(CrashEvent b, DateTimeOffset time, TimeSpan window) =>
            Near(b.Time, time, window) || (loggedTimes.TryGetValue(b.Id, out var logged) && Near(logged, time, window));
    }

    /// <summary>
    /// An unused small dump (one with a driver list) with the same stop code written for the same crash, to go with a
    /// MEMORY.DMP that has no driver list.
    /// </summary>
    private static DumpFile? FindCompanionDump(IEnumerable<DumpFile> dumps, HashSet<DumpFile> used, uint? code, DateTimeOffset crashTime) =>
        dumps.Where(d => !used.Contains(d) && d.Info is { Modules.Count: > 0 } info && info.BugCheckCode == code && Near(d.Time, crashTime, SameCrashWindow))
            .OrderBy(d => (d.Time - crashTime).Duration())
            .FirstOrDefault();

    /// <summary>True when <paramref name="crashTime"/> could be the crash an event logged at <paramref name="logged"/> reports.</summary>
    private static bool IsReportDelay(DateTimeOffset crashTime, DateTimeOffset logged) =>
        crashTime <= logged + SameCrashWindow && crashTime >= logged - MaxReportDelay;

    /// <summary>Adds what the dump knows (parameters, build, loaded drivers and the "probably caused by" guess).</summary>
    public static CrashEvent WithDump(CrashEvent e, DumpFile dump)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(dump);
        var info = dump.Info;
        if (info is null)
        {
            return e with { DumpPath = dump.Path };
        }

        var parameters = e.Parameters.Count > 0 ? e.Parameters : info.Parameters;
        var code = e.BugCheckCode ?? info.BugCheckCode;

        // The dump header has the real crash time; the event is written at the next start, maybe much later.
        var time = info.CrashTime is { } crash && IsReportDelay(crash, e.Time) ? crash : e.Time;
        return e with
        {
            Time = time,
            DumpPath = dump.Path,
            BugCheckCode = code,
            Parameters = parameters,
            OsBuild = e.OsBuild ?? info.OsBuild,
            Modules = info.Modules.Count > 0 ? info.Modules : e.Modules,
            Blame = e.Blame ?? CrashBlameRules.Guess(code, parameters, info.ExceptionAddress, info.Modules, info.StackValues),
        };
    }

    /// <summary>The timeline: one group per local day, newest day first, events newest first inside.</summary>
    public static IReadOnlyList<CrashDay> ByDay(IEnumerable<CrashEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events
            .GroupBy(e => DateOnly.FromDateTime(e.Time.LocalDateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => new CrashDay(g.Key, g.OrderByDescending(e => e.Time).ToList()))
            .ToList();
    }

    /// <summary>The tiles: each kind counted over the last <paramref name="days"/> days (crashes and hangs together for apps).</summary>
    public static CrashSummary Summarize(IEnumerable<CrashEvent> events, DateTimeOffset now, int days = SummaryDays)
    {
        ArgumentNullException.ThrowIfNull(events);
        var recent = events.Where(e => e.Time >= now.AddDays(-days)).ToList();
        return new CrashSummary(
            recent.Count(e => e.Kind == CrashKind.BlueScreen),
            recent.Count(e => e.Kind == CrashKind.SuddenShutdown),
            recent.Count(e => e.Kind == CrashKind.DriverReset),
            recent.Count(e => e.Kind is CrashKind.AppCrash or CrashKind.AppHang));
    }

    /// <summary>The Dashboard's count of blue screens and sudden shutdowns in the last <paramref name="days"/> days.</summary>
    public static CrashGlance Glance(IEnumerable<CrashEvent> events, DateTimeOffset now, int days = AttentionDays)
    {
        ArgumentNullException.ThrowIfNull(events);
        var recent = events.Where(e => e.Time >= now.AddDays(-days) && e.Kind is CrashKind.BlueScreen or CrashKind.SuddenShutdown).ToList();
        return new CrashGlance(
            recent.Count(e => e.Kind == CrashKind.BlueScreen),
            recent.Count(e => e.Kind == CrashKind.SuddenShutdown),
            recent.Count == 0 ? null : recent.Max(e => e.Time));
    }

    /// <summary>
    /// Programs by how often they crashed or hung, most first (then most recent). The app name is compared without
    /// case; the module and exception shown are the most common ones for that app.
    /// </summary>
    public static IReadOnlyList<AppCrashGroup> AppGroups(IEnumerable<CrashEvent> events, int max = 10)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events
            .Where(e => e.Kind is CrashKind.AppCrash or CrashKind.AppHang && !string.IsNullOrWhiteSpace(e.AppName))
            .GroupBy(e => e.AppName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var crashes = g.Where(e => e.Kind == CrashKind.AppCrash).ToList();
                return new AppCrashGroup(
                    g.OrderByDescending(e => e.Time).First().AppName!.Trim(),
                    crashes.Count,
                    g.Count(e => e.Kind == CrashKind.AppHang),
                    g.Max(e => e.Time),
                    MostCommon(crashes.Select(e => e.ModuleName)),
                    crashes.Where(e => e.ExceptionCode is not null).GroupBy(e => e.ExceptionCode).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).FirstOrDefault()?.Key);
            })
            .OrderByDescending(a => a.Total)
            .ThenByDescending(a => a.Latest)
            .ThenBy(a => a.AppName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, max))
            .ToList();
    }

    /// <summary>The page filter: the kinds to show (empty = all) and the earliest time.</summary>
    public static IReadOnlyList<CrashEvent> Filter(IEnumerable<CrashEvent> events, IReadOnlyCollection<CrashKind> kinds, DateTimeOffset? from, string? app = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(kinds);
        return events
            .Where(e => kinds.Count == 0 || kinds.Contains(e.Kind))
            .Where(e => from is null || e.Time >= from)
            .Where(e => string.IsNullOrWhiteSpace(app) || string.Equals(e.AppName?.Trim(), app.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// True when PC Checker applied an overclock profile that is not stock (it clears the active profile on reset or
    /// after an unclean shutdown). Overclocks set in the BIOS or other tools are invisible here.
    /// </summary>
    public static bool IsOverclockApplied(Settings.OverclockSettings? settings)
    {
        if (settings?.ActiveProfileName is not { Length: > 0 } name)
        {
            return false;
        }

        var profile = settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return profile is null ? !string.Equals(name, OverclockProfileStockName, StringComparison.OrdinalIgnoreCase) : !profile.IsStock;
    }

    /// <summary>The vendor of a graphics driver file ("NVIDIA", "AMD", "Intel"), or null.</summary>
    public static string? GraphicsVendor(string? driver) => Health.HardwareErrorRules.DriverVendor([driver]);

    private static string? MostCommon(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Key;

    private static bool Near(DateTimeOffset a, DateTimeOffset b, TimeSpan window) => (a - b).Duration() <= window;

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        // The event may say %SystemRoot%\Minidump\… or C:\Windows\Minidump\…: compare from the folder name on.
        int mini = p.IndexOf(@"\Minidump\", StringComparison.OrdinalIgnoreCase);
        if (mini >= 0)
        {
            return p[mini..];
        }

        return p.EndsWith(@"\MEMORY.DMP", StringComparison.OrdinalIgnoreCase) ? @"\MEMORY.DMP" : p;
    }
}
