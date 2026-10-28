using System.Globalization;
using PcChecker.Core.Logging;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>What a crash pattern is about.</summary>
public enum CrashPatternKind
{
    SameStopCode,
    SameDriver,
    SameModuleAcrossApps,
    AfterDriverInstall,
    AfterWindowsUpdate,
    AfterPcCheckerChange,
    UnderLoad,
    AtWake,
    Overheating,
}

/// <summary>
/// One pattern across crashes, as a plain sentence with a confidence word in it ("often", "every time since…").
/// <see cref="Strength"/> is 1 (a hint), 2 (likely) or 3 (strong). <see cref="CrashIds"/> are the crashes it covers.
/// </summary>
public sealed record CrashPattern(CrashPatternKind Kind, CrashLine Text, int Strength, IReadOnlyList<string> CrashIds)
{
    /// <summary>The driver, update or change the pattern names, when there is one.</summary>
    public string? Subject { get; init; }

    /// <summary>When the thing the pattern names happened (install, update, change).</summary>
    public DateTimeOffset? Since { get; init; }
}

/// <summary>A Windows Update install from the update history.</summary>
public sealed record UpdateInstallRecord(DateTimeOffset Time, string Title, bool IsDriver);

/// <summary>Everything the pattern rules look at.</summary>
public sealed record CrashPatternInput
{
    public IReadOnlyList<CrashEvent> Crashes { get; init; } = [];

    public IReadOnlyList<CrashSystemEvent> SystemEvents { get; init; } = [];

    public IReadOnlyList<UpdateInstallRecord> Updates { get; init; } = [];

    public IReadOnlyList<ActionRecord> Journal { get; init; } = [];

    /// <summary>Context per crash id (from <see cref="CrashContextRules"/>); missing ids had no context loaded.</summary>
    public IReadOnlyDictionary<string, CrashContext> Contexts { get; init; } = new Dictionary<string, CrashContext>();
}

/// <summary>
/// Finds patterns across crashes: the same stop code or driver again and again, crashes that started after a driver
/// install, a Windows update or a PC Checker change, crashes under load or at wake, and overheating. Pure.
/// </summary>
public static class CrashPatternRules
{
    /// <summary>How long before and after an install the rules compare.</summary>
    public const int CompareDays = 30;

    /// <summary>A crash this soon after something PC Checker changed is linked to it.</summary>
    public static readonly TimeSpan ChangeWindow = TimeSpan.FromHours(6);

    public static IReadOnlyList<CrashPattern> Find(CrashPatternInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var serious = input.Crashes.Where(IsSerious).OrderBy(c => c.Time).ToList();
        var patterns = new List<CrashPattern>();
        SameStopCode(serious, patterns);
        SameDriver(serious, patterns);
        SameModuleAcrossApps(input.Crashes, patterns);
        AfterDriverInstall(serious, input, patterns);
        AfterWindowsUpdate(serious, input.Updates, patterns);
        AfterPcCheckerChange(serious, input.Journal, patterns);
        UnderLoadAndHeat(serious, input, patterns);
        AtWake(serious, input.SystemEvents, patterns);
        return patterns.OrderByDescending(p => p.Strength).ThenBy(p => p.Kind).ToList();
    }

    /// <summary>The patterns that cover one crash.</summary>
    public static IReadOnlyList<CrashPattern> For(string crashId, IEnumerable<CrashPattern> patterns) =>
        patterns.Where(p => p.CrashIds.Contains(crashId, StringComparer.Ordinal)).ToList();

    /// <summary>Blue screens, sudden shutdowns and graphics driver resets: the crashes that affect the whole PC.</summary>
    public static bool IsSerious(CrashEvent e) => e.Kind is CrashKind.BlueScreen or CrashKind.SuddenShutdown or CrashKind.DriverReset;

    /// <summary>The driver a crash points at, as a lower-case file name without extension ("nvlddmkm"), or null.</summary>
    public static string? DriverKey(CrashEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var name = e.Kind == CrashKind.DriverReset ? e.Driver : e.Blame is { IsWindowsModule: false } b ? b.Module : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var file = name.Trim();
        int slash = file.LastIndexOfAny(['\\', '/']);
        file = slash >= 0 ? file[(slash + 1)..] : file;
        int dot = file.LastIndexOf('.');
        return (dot > 0 ? file[..dot] : file).ToLowerInvariant();
    }

    /// <summary>
    /// True when an install's subject (a .sys file, an INF or a device id from setupapi.dev.log, or a Windows Update
    /// title) is about the driver <paramref name="key"/>. Graphics drivers also match their vendor's device ids and
    /// package names.
    /// </summary>
    public static bool InstallMatches(string? subject, string key)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (subject.Contains(key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CrashRules.GraphicsVendor(key) switch
        {
            "NVIDIA" => ContainsAny(subject, "VEN_10DE", "nv_dispi", "nvlt", "NVIDIA"),
            "AMD" => ContainsAny(subject, "VEN_1002", "Advanced Micro Devices", "AMD - Display", "AMD Radeon"),
            "Intel" => ContainsAny(subject, "iigd", "Intel - Display", "Intel(R) Graphics", "Intel(R) Arc", "Intel(R) UHD", "Intel(R) Iris"),
            _ => false,
        };
    }

    private static bool ContainsAny(string text, params string[] parts) => parts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static void SameStopCode(List<CrashEvent> serious, List<CrashPattern> patterns)
    {
        foreach (var group in serious.Where(c => c.Kind == CrashKind.BlueScreen && c.BugCheckCode is not null).GroupBy(c => c.BugCheckCode!.Value))
        {
            int n = group.Count();
            if (n < 2)
            {
                continue;
            }

            var first = group.Min(c => c.Time);
            patterns.Add(new CrashPattern(CrashPatternKind.SameStopCode,
                new CrashLine(n >= 3 ? T("{0} keeps coming back: {1} times since {2}.") : T("{0} happened twice, first on {2}."),
                    StopCodeCatalog.NameOf(group.Key), n.ToString(CultureInfo.InvariantCulture), Day(first)),
                n >= 3 ? 3 : 2, group.Select(c => c.Id).ToList())
            { Subject = StopCodeCatalog.NameOf(group.Key), Since = first });
        }
    }

    private static void SameDriver(List<CrashEvent> serious, List<CrashPattern> patterns)
    {
        foreach (var group in serious.Where(c => DriverKey(c) is not null).GroupBy(c => DriverKey(c)!))
        {
            int n = group.Count();
            if (n < 2)
            {
                continue;
            }

            int blue = group.Count(c => c.Kind == CrashKind.BlueScreen);
            int resets = group.Count(c => c.Kind == CrashKind.DriverReset);
            var file = group.Key + ".sys";
            patterns.Add(new CrashPattern(CrashPatternKind.SameDriver,
                new CrashLine(T("The same driver, {0}, is involved again and again: {1} blue screens and {2} driver resets."),
                    file, blue.ToString(CultureInfo.InvariantCulture), resets.ToString(CultureInfo.InvariantCulture)),
                n >= 3 ? 3 : 2, group.Select(c => c.Id).ToList())
            { Subject = file });
        }
    }

    private static void SameModuleAcrossApps(IReadOnlyList<CrashEvent> crashes, List<CrashPattern> patterns)
    {
        var apps = crashes.Where(c => c.Kind == CrashKind.AppCrash && !string.IsNullOrWhiteSpace(c.ModuleName) && !string.IsNullOrWhiteSpace(c.AppName)
            && !string.Equals(c.ModuleName, c.AppName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in apps.GroupBy(c => c.ModuleName!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            int programs = group.Select(c => c.AppName!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var info = FaultingModuleCatalog.Find(group.Key);
            if (programs < 2 || info?.Category is ModuleCategory.WindowsCore)
            {
                continue;
            }

            patterns.Add(new CrashPattern(CrashPatternKind.SameModuleAcrossApps,
                new CrashLine(T("{0} crashed {1} different programs, so the problem is probably {0} itself, not the programs."),
                    group.Key, programs.ToString(CultureInfo.InvariantCulture)),
                programs >= 3 ? 3 : 2, group.Select(c => c.Id).ToList())
            { Subject = group.Key });
        }
    }

    private static void AfterDriverInstall(List<CrashEvent> serious, CrashPatternInput input, List<CrashPattern> patterns)
    {
        var installs = input.SystemEvents.Where(e => e.Kind == CrashSystemEventKind.DriverInstalled && !string.IsNullOrWhiteSpace(e.Detail))
            .Select(e => (e.Time, Subject: e.Detail!))
            .Concat(input.Updates.Where(u => u.IsDriver).Select(u => (u.Time, Subject: u.Title)))
            .OrderBy(i => i.Time)
            .ToList();
        if (installs.Count == 0)
        {
            return;
        }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Crashes that involve one driver and started after that driver was installed.
        foreach (var group in serious.Where(c => DriverKey(c) is not null).GroupBy(c => DriverKey(c)!))
        {
            foreach (var (time, subject) in installs.Where(i => InstallMatches(i.Subject, group.Key)).Reverse())
            {
                var after = group.Where(c => c.Time > time && c.Time <= time.AddDays(CompareDays)).ToList();
                int before = group.Count(c => c.Time < time && c.Time >= time.AddDays(-CompareDays));
                if (after.Count < 2 || before > 0)
                {
                    continue;
                }

                bool every = after.Count == group.Count(c => c.Time > time);
                patterns.Add(new CrashPattern(CrashPatternKind.AfterDriverInstall,
                    new CrashLine(every
                            ? T("Every crash involving {0} came after a driver install on {1} ({2}): {3} since then, none in the {4} days before.")
                            : T("Crashes involving {0} started after a driver install on {1} ({2}): {3} since then, none in the {4} days before."),
                        group.Key + ".sys", Day(time), subject, after.Count.ToString(CultureInfo.InvariantCulture), CompareDays.ToString(CultureInfo.InvariantCulture)),
                    after.Count >= 3 ? 3 : 2, after.Select(c => c.Id).ToList())
                { Subject = subject, Since = time });
                reported.Add(group.Key);
                break;
            }
        }

        if (reported.Count > 0)
        {
            return;
        }

        // 2. Any driver install followed by a run of crashes where there were none before.
        foreach (var (time, subject) in installs.AsEnumerable().Reverse())
        {
            var after = serious.Where(c => c.Time > time && c.Time <= time.AddDays(14)).ToList();
            int before = serious.Count(c => c.Time < time && c.Time >= time.AddDays(-14));
            if (after.Count >= 2 && before == 0)
            {
                patterns.Add(new CrashPattern(CrashPatternKind.AfterDriverInstall,
                    new CrashLine(T("The crashes started after a driver was installed on {0} ({1}): {2} in the two weeks after, none in the two weeks before."),
                        Day(time), subject, after.Count.ToString(CultureInfo.InvariantCulture)),
                    1, after.Select(c => c.Id).ToList())
                { Subject = subject, Since = time });
                return;
            }
        }
    }

    private static void AfterWindowsUpdate(List<CrashEvent> serious, IReadOnlyList<UpdateInstallRecord> updates, List<CrashPattern> patterns)
    {
        foreach (var update in updates.Where(u => !u.IsDriver).OrderByDescending(u => u.Time))
        {
            var after = serious.Where(c => c.Time > update.Time && c.Time <= update.Time.AddDays(14)).ToList();
            int before = serious.Count(c => c.Time < update.Time && c.Time >= update.Time.AddDays(-14));
            if (after.Count >= 2 && before == 0)
            {
                patterns.Add(new CrashPattern(CrashPatternKind.AfterWindowsUpdate,
                    new CrashLine(T("The crashes started after a Windows update on {0} ({1}): {2} in the two weeks after, none in the two weeks before."),
                        Day(update.Time), update.Title, after.Count.ToString(CultureInfo.InvariantCulture)),
                    after.Count >= 3 ? 2 : 1, after.Select(c => c.Id).ToList())
                { Subject = update.Title, Since = update.Time });
                return;
            }
        }
    }

    private static void AfterPcCheckerChange(List<CrashEvent> serious, IReadOnlyList<ActionRecord> journal, List<CrashPattern> patterns)
    {
        foreach (var crash in serious)
        {
            var change = journal.Where(j => j.Timestamp <= crash.Time && crash.Time - j.Timestamp <= ChangeWindow && !Failed(j.Outcome))
                .OrderByDescending(j => IsOverclock(j)).ThenByDescending(j => j.Timestamp).FirstOrDefault();
            if (change is null)
            {
                continue;
            }

            bool overclock = IsOverclock(change);
            patterns.Add(new CrashPattern(CrashPatternKind.AfterPcCheckerChange,
                new CrashLine(overclock
                        ? T("PC Checker applied an overclock ({0}) {1} before this crash. Unstable overclocks are a common cause.")
                        : T("PC Checker changed something {1} before this crash: {2} – {0}."),
                    change.Target, CrashContextRules.Ago(crash.Time - change.Timestamp), change.Action),
                overclock ? 3 : 1, [crash.Id])
            { Subject = change.Module, Since = change.Timestamp });
        }
    }

    /// <summary>True for an Action Journal entry from the Overclock module (or an overclock action elsewhere).</summary>
    public static bool IsOverclock(ActionRecord record) =>
        string.Equals(record.Module, "Overclock", StringComparison.OrdinalIgnoreCase)
        && !record.Action.Contains("reset", StringComparison.OrdinalIgnoreCase)
        && !record.Action.Contains("stock", StringComparison.OrdinalIgnoreCase);

    private static bool Failed(string? outcome) =>
        outcome is not null && (outcome.Contains("fail", StringComparison.OrdinalIgnoreCase) || outcome.Contains("cancel", StringComparison.OrdinalIgnoreCase));

    private static void UnderLoadAndHeat(List<CrashEvent> serious, CrashPatternInput input, List<CrashPattern> patterns)
    {
        var known = serious.Where(c => input.Contexts.TryGetValue(c.Id, out var ctx) && ctx.HistoryRecorded).ToList();
        var apps = input.Crashes.Where(c => c.Kind is CrashKind.AppCrash or CrashKind.AppHang).ToList();
        var loaded = serious.Where(c =>
            (input.Contexts.TryGetValue(c.Id, out var ctx) && ctx.HeavyLoad)
            || apps.Any(a => a.Time <= c.Time && c.Time - a.Time <= TimeSpan.FromMinutes(10))).ToList();
        int total = Math.Max(known.Count, loaded.Count);
        if (loaded.Count >= 2 && loaded.Count * 2 >= total)
        {
            bool every = loaded.Count == total;
            patterns.Add(new CrashPattern(CrashPatternKind.UnderLoad,
                new CrashLine(every ? T("Every crash with recorded history happened under heavy load (a game or other demanding work): {0} of {1}.")
                        : T("Crashes often happen under heavy load (a game or other demanding work): {0} of {1}."),
                    loaded.Count.ToString(CultureInfo.InvariantCulture), total.ToString(CultureInfo.InvariantCulture)),
                every && loaded.Count >= 3 ? 3 : 2, loaded.Select(c => c.Id).ToList()));
        }

        var hot = serious.Where(c => input.Contexts.TryGetValue(c.Id, out var ctx) && ctx.Overheating).ToList();
        if (hot.Count > 0)
        {
            var part = input.Contexts[hot[0].Id].HotPart ?? T("CPU");
            patterns.Add(new CrashPattern(CrashPatternKind.Overheating,
                new CrashLine(hot.Count == 1 ? T("The {0} was overheating just before this crash.") : T("The {0} was overheating before {1} crashes."),
                    new LocalizedText(part), hot.Count.ToString(CultureInfo.InvariantCulture)),
                hot.Count >= 2 ? 3 : 2, hot.Select(c => c.Id).ToList())
            { Subject = part });
        }
    }

    private static void AtWake(List<CrashEvent> serious, IReadOnlyList<CrashSystemEvent> events, List<CrashPattern> patterns)
    {
        var wakes = events.Where(e => e.Kind == CrashSystemEventKind.Wake).Select(e => e.Time).ToList();
        var atWake = serious.Where(c =>
            wakes.Any(w => w <= c.Time && c.Time - w <= CrashContextRules.WakeWindow)
            || (c.Power is { } p && KernelPowerRules.Verdict(p) == KernelPowerVerdict.DuringSleep)
            || c.BugCheckCode is 0x9F or 0xA0 or 0x14F or 0x19C).ToList();
        if (atWake.Count == 0)
        {
            return;
        }

        bool every = atWake.Count == serious.Count && serious.Count >= 2;
        patterns.Add(new CrashPattern(CrashPatternKind.AtWake,
            new CrashLine(atWake.Count == 1
                    ? T("This crash happened while the PC was going to sleep or just after it woke up.")
                    : every ? T("Every crash ({0}) happened while the PC was going to sleep or just after it woke up: a driver or BIOS sleep problem.")
                        : T("{0} crashes happened while the PC was going to sleep or just after it woke up: often a driver or BIOS sleep problem."),
                atWake.Count.ToString(CultureInfo.InvariantCulture)),
            atWake.Count >= 3 || every ? 3 : atWake.Count == 2 ? 2 : 1, atWake.Select(c => c.Id).ToList()));
    }

    private static string Day(DateTimeOffset time) => time.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
}
