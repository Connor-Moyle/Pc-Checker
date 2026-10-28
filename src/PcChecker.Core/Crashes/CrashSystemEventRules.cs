namespace PcChecker.Core.Crashes;

/// <summary>Pure rules that connect the system events around crashes to the crashes themselves.</summary>
public static class CrashSystemEventRules
{
    /// <summary>
    /// Gives each WHEA_UNCORRECTABLE_ERROR (0x124) blue screen the WHEA-Logger record of the same crash: the nearest
    /// unused fatal WHEA event from an hour before the crash to <see cref="CrashRules.MaxReportDelay"/> after it (Windows
    /// writes it at the next start). Other crashes are returned unchanged.
    /// </summary>
    public static IReadOnlyList<CrashEvent> AttachWhea(IReadOnlyList<CrashEvent> crashes, IEnumerable<CrashSystemEvent> events)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        ArgumentNullException.ThrowIfNull(events);
        var fatal = events.Where(e => e.Kind == CrashSystemEventKind.WheaFatal && e.Whea is not null).OrderBy(e => e.Time).ToList();
        if (fatal.Count == 0)
        {
            return crashes;
        }

        var used = new HashSet<CrashSystemEvent>();
        var result = new List<CrashEvent>(crashes.Count);
        foreach (var crash in crashes)
        {
            if (crash.Kind != CrashKind.BlueScreen || crash.BugCheckCode != 0x124 || crash.Whea is not null)
            {
                result.Add(crash);
                continue;
            }

            var match = fatal.Where(f => !used.Contains(f) && f.Time >= crash.Time - TimeSpan.FromHours(1) && f.Time <= crash.Time + CrashRules.MaxReportDelay)
                .OrderBy(f => (f.Time - crash.Time).Duration())
                .FirstOrDefault();
            if (match is null)
            {
                result.Add(crash);
                continue;
            }

            used.Add(match);
            result.Add(crash with { Whea = match.Whea });
        }

        return result;
    }

    /// <summary>Driver installs from setupapi.dev.log as system events (the subject is the device or INF the section names).</summary>
    public static IReadOnlyList<CrashSystemEvent> FromSetupApi(IEnumerable<SetupApiInstall> installs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        return installs.Where(i => i.Succeeded)
            .Select(i => new CrashSystemEvent(new DateTimeOffset(DateTime.SpecifyKind(i.LocalTime, DateTimeKind.Local)), CrashSystemEventKind.DriverInstalled,
                SetupApiLogParser.Subject(i.Title)))
            .ToList();
    }
}
