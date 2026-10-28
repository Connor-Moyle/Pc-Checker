using System.Globalization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>How sure a "probably caused by" line is.</summary>
public enum CauseConfidence
{
    Possible,
    Likely,
    VeryLikely,
}

/// <summary>One "probably caused by" line: the suspect, how sure, and why.</summary>
public sealed record CauseGuess(CrashLine Text, CauseConfidence Confidence, CrashCause? Cause)
{
    /// <summary>"Possible", "Likely" or "Very likely" (English key).</summary>
    public string ConfidenceText => CrashInsightBuilder.ConfidenceName(Confidence);
}

/// <summary>One program's crash history (for the "Most crashing apps" details).</summary>
public sealed record AppCrashStats(
    string AppName,
    int Crashes,
    int Hangs,
    DateTimeOffset First,
    DateTimeOffset Last,
    IReadOnlyList<string> Versions,
    IReadOnlyList<(string Module, int Count)> TopModules);

/// <summary>Everything the details pane, the copied report and Ask Claude say about one crash.</summary>
public sealed record CrashInsight
{
    public required CrashEvent Crash { get; init; }

    public required CrashAdviceText Advice { get; init; }

    /// <summary>"What the details say": decoded parameters, Kernel-Power fields, the WHEA record, the faulting module.</summary>
    public IReadOnlyList<CrashLine> Details { get; init; } = [];

    /// <summary>"Probably caused by", most likely first.</summary>
    public IReadOnlyList<CauseGuess> Causes { get; init; } = [];

    public IReadOnlyList<CrashPattern> Patterns { get; init; } = [];

    public CrashContext? Context { get; init; }

    public CrashDriverDetails? Driver { get; init; }

    public FaultingModuleInfo? Module { get; init; }

    public IReadOnlyList<CrashFix> Fixes { get; init; } = [];

    public AppCrashStats? App { get; init; }
}

/// <summary>Puts the catalogs, decoders, context, patterns and fix ranking together for one crash. Pure.</summary>
public static class CrashInsightBuilder
{
    public static string ConfidenceName(CauseConfidence confidence) => confidence switch
    {
        CauseConfidence.VeryLikely => T("Very likely"),
        CauseConfidence.Likely => T("Likely"),
        _ => T("Possible"),
    };

    public static CrashInsight Build(CrashEvent crash, bool overclockApplied, CrashContext? context = null, IReadOnlyList<CrashPattern>? allPatterns = null,
        CrashDriverDetails? driver = null, bool isGame = false, IReadOnlyList<CrashEvent>? allEvents = null)
    {
        ArgumentNullException.ThrowIfNull(crash);
        var patterns = allPatterns is null ? [] : CrashPatternRules.For(crash.Id, allPatterns);
        var module = crash.Kind is CrashKind.AppCrash ? FaultingModuleCatalog.Find(crash.ModuleName, crash.ModulePath, crash.ExceptionCode) : null;
        var fixes = CrashFixRules.Rank(new CrashFixInput
        {
            Crash = crash,
            OverclockApplied = overclockApplied,
            Context = context,
            Patterns = patterns,
            Driver = driver,
            IsGame = isGame,
        });
        return new CrashInsight
        {
            Crash = crash,
            Advice = CrashAdvice.For(crash, overclockApplied),
            Details = Details(crash, module),
            Causes = Causes(crash, context, patterns, driver, module, overclockApplied),
            Patterns = patterns,
            Context = context,
            Driver = driver,
            Module = module,
            Fixes = fixes,
            App = crash.AppName is { Length: > 0 } app && allEvents is not null ? CrashRules.AppStats(allEvents, app) : null,
        };
    }

    /// <summary>"What the details say" for one crash.</summary>
    public static IReadOnlyList<CrashLine> Details(CrashEvent crash, FaultingModuleInfo? module = null)
    {
        ArgumentNullException.ThrowIfNull(crash);
        var lines = new List<CrashLine>();
        if (crash.BugCheckCode is { } code && crash.Kind is CrashKind.BlueScreen or CrashKind.DriverReset)
        {
            lines.AddRange(BugCheckDecoder.Decode(code, crash.Parameters, crash.Modules, crash.Whea));
        }

        if (crash.Power is { } power && crash.Kind == CrashKind.SuddenShutdown)
        {
            lines.AddRange(KernelPowerRules.Describe(power));
        }
        else if (crash.Kind == CrashKind.SuddenShutdown && crash.EventId == 6008)
        {
            lines.Add(CrashLine.Of(T("Windows noticed at the next start that the previous shutdown was unexpected; no more detail was recorded.")));
        }

        if (crash.Kind == CrashKind.DriverReset && crash.Driver is { Length: > 0 } driver)
        {
            lines.Add(new CrashLine(T("The graphics driver that was restarted: {0}."), driver));
        }

        if (crash.Kind is CrashKind.AppCrash)
        {
            if (crash.ExceptionCode is { } ex)
            {
                lines.Add(new CrashLine(T("Error: {0}."), ExceptionCodeCatalog.Describe(ex)));
                if (ExceptionCodeCatalog.Find(ex) is { } info)
                {
                    lines.Add(new CrashLine("{0}", new LocalizedText(info.Meaning)));
                }
            }

            if (crash.ModuleName is { Length: > 0 } name)
            {
                lines.Add(module is not null
                    ? new CrashLine(T("Faulting file: {0}, which belongs to {1}."), name + (crash.ModuleVersion is { Length: > 0 } v ? " " + v : string.Empty), new LocalizedText(module.Owner))
                    : string.Equals(name, crash.AppName, StringComparison.OrdinalIgnoreCase)
                        ? CrashLine.Of(T("The error happened in the program's own code."))
                        : new CrashLine(T("Faulting file: {0}."), name + (crash.ModuleVersion is { Length: > 0 } v2 ? " " + v2 : string.Empty)));
                if (crash.ModulePath is { Length: > 0 } path && module?.Category == ModuleCategory.Mod)
                {
                    lines.Add(new CrashLine(T("It was loaded from {0}, not from Windows."), path));
                }
            }

            if (crash.FaultOffset is { Length: > 0 } offset)
            {
                lines.Add(new CrashLine(T("Offset in that file: {0} (the same offset in several crashes means the same bug)."), offset));
            }
        }

        if (crash.Kind == CrashKind.AppHang && crash.AppVersion is { Length: > 0 } hv)
        {
            lines.Add(new CrashLine(T("Program version {0} stopped responding and was closed."), hv));
        }

        return lines;
    }

    /// <summary>"Probably caused by", most likely first.</summary>
    public static IReadOnlyList<CauseGuess> Causes(CrashEvent crash, CrashContext? context, IReadOnlyList<CrashPattern> patterns, CrashDriverDetails? driver,
        FaultingModuleInfo? module, bool overclockApplied)
    {
        ArgumentNullException.ThrowIfNull(crash);
        ArgumentNullException.ThrowIfNull(patterns);
        var list = new List<CauseGuess>();
        bool overclockBefore = (context?.ChangesBefore.Any(CrashPatternRules.IsOverclock) ?? false)
            || patterns.Any(p => p.Kind == CrashPatternKind.AfterPcCheckerChange && p.Strength >= 3);
        var causes = crash.Kind switch
        {
            CrashKind.BlueScreen => StopCodeCatalog.CausesOf(crash.BugCheckCode),
            CrashKind.DriverReset => StopCodeCatalog.CausesOf(crash.BugCheckCode is { } rc && StopCodeCatalog.Find(rc)?.Area == StopCodeArea.Graphics ? rc : 0x117),
            CrashKind.SuddenShutdown => SuddenCauses(crash),
            _ => [],
        };

        if (overclockBefore)
        {
            list.Add(new CauseGuess(CrashLine.Of(T("The overclock PC Checker applied shortly before the crash.")), CauseConfidence.VeryLikely, CrashCause.CpuOverclock));
        }

        if (context?.Overheating == true)
        {
            list.Add(new CauseGuess(new CrashLine(T("Overheating: the {0} reached {1} °C."), context.HotPart ?? "CPU",
                Math.Round(context.HotTemperature ?? 0).ToString("0", CultureInfo.CurrentCulture)), CauseConfidence.VeryLikely, CrashCause.Overheating));
        }

        if (patterns.FirstOrDefault(p => p.Kind == CrashPatternKind.AfterDriverInstall) is { } install)
        {
            list.Add(new CauseGuess(new CrashLine(T("The driver installed on {0} ({1})."), install.Since?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "?", install.Subject ?? "?"),
                install.Strength >= 2 ? CauseConfidence.VeryLikely : CauseConfidence.Likely, CrashCause.Driver));
        }

        if (crash.Blame is { } blame && (crash.Kind == CrashKind.BlueScreen))
        {
            var confidence = blame.IsWindowsModule ? CauseConfidence.Possible : blame.Confidence == BlameConfidence.Medium ? CauseConfidence.Likely : CauseConfidence.Possible;
            var who = driver is { DeviceName: { Length: > 0 } device } ? $"{blame.Module} ({device})" : blame.Module;
            list.Add(new CauseGuess(new CrashLine(T("{0}: {1}"), who, ReasonLine(blame.Reason)), confidence, CrashCause.Driver));
        }
        else if (crash.Kind == CrashKind.DriverReset && crash.Driver is { Length: > 0 } reset)
        {
            list.Add(new CauseGuess(new CrashLine(T("{0}: the graphics driver that stopped responding."), reset), CauseConfidence.Likely, CrashCause.Driver));
        }

        if (crash.Whea is { } whea)
        {
            var part = whea.Components.FirstOrDefault() ?? T("Processor");
            list.Add(new CauseGuess(new CrashLine(T("The hardware ({0}) reported the error itself, so no driver is to blame."), new LocalizedText(part)),
                CauseConfidence.Likely, part == "Memory" ? CrashCause.Memory : CrashCause.CpuOverclock));
        }

        if (module is not null)
        {
            list.Add(new CauseGuess(new CrashLine(T("{0}, in {1}."), new LocalizedText(module.Owner), crash.ModuleName),
                module.Category is ModuleCategory.WindowsCore ? CauseConfidence.Possible : CauseConfidence.Likely, CategoryCause(module.Category)));
        }
        else if (crash.Kind == CrashKind.AppCrash && crash.ModuleName is { Length: > 0 } m)
        {
            bool own = string.Equals(m, crash.AppName, StringComparison.OrdinalIgnoreCase);
            list.Add(new CauseGuess(own ? CrashLine.Of(T("A bug in the program itself.")) : new CrashLine(T("{0}, a file the program loaded."), m), CauseConfidence.Possible, null));
        }

        for (int i = 0; i < causes.Count && i < 3; i++)
        {
            var cause = causes[i];
            if (list.Any(g => g.Cause == cause))
            {
                continue;
            }

            var conf = i == 0 && list.Count == 0 ? CauseConfidence.Likely : CauseConfidence.Possible;
            if (cause is CrashCause.CpuOverclock or CrashCause.GraphicsCard && overclockApplied)
            {
                conf = CauseConfidence.Likely;
            }

            list.Add(new CauseGuess(new CrashLine(T("{0} (common for this kind of crash)."), new LocalizedText(CrashCauseText.Name(cause))), conf, cause));
        }

        return list.OrderByDescending(g => g.Confidence).ToList();
    }

    private static IReadOnlyList<CrashCause> SuddenCauses(CrashEvent crash)
    {
        var verdict = crash.Power is { } p ? KernelPowerRules.Verdict(p) : crash.PowerButtonHeld ? KernelPowerVerdict.PowerButton : KernelPowerVerdict.PowerLoss;
        return verdict switch
        {
            KernelPowerVerdict.DuringSleep => [CrashCause.Driver, CrashCause.Firmware],
            KernelPowerVerdict.PowerButton => [CrashCause.Driver, CrashCause.Overheating, CrashCause.Memory],
            _ => [CrashCause.PowerSupply, CrashCause.Overheating, CrashCause.CpuOverclock],
        };
    }

    private static CrashCause? CategoryCause(ModuleCategory category) => category switch
    {
        ModuleCategory.GraphicsDriver or ModuleCategory.WindowsGraphics or ModuleCategory.Audio or ModuleCategory.Input => CrashCause.Driver,
        ModuleCategory.AntiCheat or ModuleCategory.Security => CrashCause.SecuritySoftware,
        ModuleCategory.VisualCppRuntime or ModuleCategory.DirectXRuntime or ModuleCategory.DotNet => CrashCause.WindowsFiles,
        _ => null,
    };

    /// <summary>The English blame reason as a translatable line (the parameter number is the only value in it).</summary>
    public static CrashLine ReasonLine(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        const string Note = " It is part of Windows, so the real cause is often a driver that called it.";
        const string Prefix = "Stop code parameter ";
        const string Suffix = " points inside this file.";
        bool windows = reason.EndsWith(Note, StringComparison.Ordinal);
        var core = windows ? reason[..^Note.Length] : reason;
        CrashLine line = core.StartsWith(Prefix, StringComparison.Ordinal) && core.EndsWith(Suffix, StringComparison.Ordinal)
            && int.TryParse(core.AsSpan(Prefix.Length, core.Length - Prefix.Length - Suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                ? new CrashLine(T("Stop code parameter {0} points inside this file."), n.ToString(CultureInfo.InvariantCulture))
                : new CrashLine("{0}", new LocalizedText(core));
        return windows ? new CrashLine("{0} {1}", line, new LocalizedText(Note.TrimStart())) : line;
    }
}
