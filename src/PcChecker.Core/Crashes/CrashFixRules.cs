using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>A fix the Crashes page can offer as a button (or, for BIOS and power supply checks, as advice).</summary>
public enum CrashFixKind
{
    UpdateDriver,
    RollBackDriver,
    ResetOverclock,
    BiosStabilityTest,
    UpdateBios,
    MemoryDiagnostic,
    RepairWindowsFiles,
    CheckDisk,
    CheckTemperatures,
    CheckFans,
    CheckPowerSupply,
    ReinstallRuntimes,
    RepairDotNet,
    VerifyGameFiles,
    ScanGameFolder,
    DisableOverlay,
    UninstallRecentProgram,
    UninstallRecentUpdate,
    UpdateWindows,
    UpdateApp,
    TurnOffFastStartup,
}

/// <summary>
/// One ranked fix for a crash: what to do, why it is suggested for this crash, and how strongly. <see cref="Target"/>
/// is what the button acts on (a driver file, overlay name, runtime family or app name).
/// </summary>
public sealed record CrashFix(CrashFixKind Kind, CrashLine Why, int Score)
{
    public string? Target { get; init; }

    /// <summary>The button text (English key).</summary>
    public string Title => CrashFixRules.TitleOf(Kind);

    /// <summary>True when the fix changes the PC (the page asks first and the change is journaled).</summary>
    public bool ChangesPc => Kind is CrashFixKind.RollBackDriver or CrashFixKind.ResetOverclock or CrashFixKind.MemoryDiagnostic
        or CrashFixKind.RepairWindowsFiles or CrashFixKind.CheckDisk;

    /// <summary>True for advice the page can only explain (BIOS settings, cables), not do.</summary>
    public bool IsAdviceOnly => Kind is CrashFixKind.BiosStabilityTest or CrashFixKind.UpdateBios or CrashFixKind.CheckPowerSupply;
}

/// <summary>What the fix ranking looks at for one crash.</summary>
public sealed record CrashFixInput
{
    public required CrashEvent Crash { get; init; }

    /// <summary>PC Checker has an overclock applied now.</summary>
    public bool OverclockApplied { get; init; }

    public CrashContext? Context { get; init; }

    /// <summary>The patterns that cover this crash.</summary>
    public IReadOnlyList<CrashPattern> Patterns { get; init; } = [];

    public CrashDriverDetails? Driver { get; init; }

    /// <summary>True when the crashed program belongs to a game PC Checker found.</summary>
    public bool IsGame { get; init; }
}

/// <summary>
/// Ranks the fixes for one crash, most likely first, each with a one-line reason. The stop code's likely causes give
/// the base order; what is known about this crash moves fixes up: a third-party driver blamed, a newer driver, a driver
/// installed just before, an overclock applied, heat, disk errors, a Windows update, the faulting module of an app
/// crash. On a tie the safer fix comes first. Pure.
/// </summary>
public static class CrashFixRules
{
    /// <summary>Safer and simpler fixes first when scores tie.</summary>
    private static readonly CrashFixKind[] SafetyOrder =
    [
        CrashFixKind.CheckTemperatures, CrashFixKind.CheckFans, CrashFixKind.DisableOverlay, CrashFixKind.UpdateApp, CrashFixKind.VerifyGameFiles,
        CrashFixKind.UpdateDriver, CrashFixKind.ReinstallRuntimes, CrashFixKind.ScanGameFolder, CrashFixKind.RepairDotNet, CrashFixKind.ResetOverclock,
        CrashFixKind.RollBackDriver, CrashFixKind.UpdateWindows, CrashFixKind.RepairWindowsFiles, CrashFixKind.CheckDisk, CrashFixKind.MemoryDiagnostic,
        CrashFixKind.BiosStabilityTest, CrashFixKind.CheckPowerSupply, CrashFixKind.TurnOffFastStartup, CrashFixKind.UninstallRecentProgram,
        CrashFixKind.UninstallRecentUpdate, CrashFixKind.UpdateBios,
    ];

    public static string TitleOf(CrashFixKind kind) => kind switch
    {
        CrashFixKind.UpdateDriver => T("Update the driver"),
        CrashFixKind.RollBackDriver => T("Roll back the driver"),
        CrashFixKind.ResetOverclock => T("Reset the overclock"),
        CrashFixKind.BiosStabilityTest => T("Test BIOS defaults (XMP/EXPO, PBO)"),
        CrashFixKind.UpdateBios => T("Update the BIOS"),
        CrashFixKind.MemoryDiagnostic => T("Test the memory"),
        CrashFixKind.RepairWindowsFiles => T("Repair Windows files"),
        CrashFixKind.CheckDisk => T("Check the disk"),
        CrashFixKind.CheckTemperatures => T("Check temperatures"),
        CrashFixKind.CheckFans => T("Check the fans"),
        CrashFixKind.CheckPowerSupply => T("Check the power supply"),
        CrashFixKind.ReinstallRuntimes => T("Reinstall runtimes"),
        CrashFixKind.RepairDotNet => T("Repair .NET"),
        CrashFixKind.VerifyGameFiles => T("Verify game files"),
        CrashFixKind.ScanGameFolder => T("Scan the game for missing DLLs"),
        CrashFixKind.DisableOverlay => T("Turn off the overlay"),
        CrashFixKind.UninstallRecentProgram => T("Remove a recent program"),
        CrashFixKind.UninstallRecentUpdate => T("Uninstall the recent update"),
        CrashFixKind.UpdateWindows => T("Update Windows"),
        CrashFixKind.UpdateApp => T("Update the program"),
        _ => T("Turn off Fast startup"),
    };

    public static IReadOnlyList<CrashFix> Rank(CrashFixInput input, int max = 6)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scores = new Dictionary<CrashFixKind, CrashFix>();
        void Add(CrashFixKind kind, int score, CrashLine why, string? target = null)
        {
            if (!scores.TryGetValue(kind, out var existing) || existing.Score < score)
            {
                scores[kind] = new CrashFix(kind, why, score) { Target = target ?? existing?.Target };
            }
        }

        var crash = input.Crash;
        switch (crash.Kind)
        {
            case CrashKind.BlueScreen:
            case CrashKind.DriverReset:
                FromCauses(input, crash.Kind == CrashKind.DriverReset && crash.BugCheckCode is null ? StopCodeCatalog.CausesOf(0x117) : StopCodeCatalog.CausesOf(crash.BugCheckCode), Add);
                break;
            case CrashKind.SuddenShutdown:
                SuddenShutdown(input, Add);
                break;
            default:
                App(input, Add);
                break;
        }

        Boosts(input, Add);
        return scores.Values
            .OrderByDescending(f => f.Score)
            .ThenBy(f => Array.IndexOf(SafetyOrder, f.Kind))
            .Take(Math.Max(1, max))
            .ToList();
    }

    private static void FromCauses(CrashFixInput input, IReadOnlyList<CrashCause> causes, Action<CrashFixKind, int, CrashLine, string?> add)
    {
        string? blamed = BlamedDriver(input.Crash);
        for (int i = 0; i < causes.Count; i++)
        {
            int w = Math.Max(20, 100 - (i * 20));
            var why = new CrashLine(T("{0} is a likely cause of this stop code."), new LocalizedText(CrashCauseText.Name(causes[i])));
            switch (causes[i])
            {
                case CrashCause.Driver:
                    add(CrashFixKind.UpdateDriver, w, blamed is null ? why : new CrashLine(T("{0} is the driver this crash points at."), blamed), blamed);
                    add(CrashFixKind.RollBackDriver, w - 40, why, blamed);
                    break;
                case CrashCause.Memory:
                    add(CrashFixKind.MemoryDiagnostic, w, why, null);
                    add(CrashFixKind.BiosStabilityTest, w - 10, new CrashLine(T("Memory settings such as XMP/EXPO are a common cause of this stop code.")), null);
                    break;
                case CrashCause.CpuOverclock:
                    if (input.OverclockApplied)
                    {
                        add(CrashFixKind.ResetOverclock, w + 10, new CrashLine(T("PC Checker has an overclock applied, and overclocks are a likely cause of this stop code.")), null);
                    }

                    add(CrashFixKind.BiosStabilityTest, w, new CrashLine(T("CPU overclocks and undervolts (PBO, Curve Optimizer) are a likely cause of this stop code.")), null);
                    break;
                case CrashCause.GraphicsCard:
                    add(CrashFixKind.UpdateDriver, w - 10, why, blamed);
                    if (input.OverclockApplied)
                    {
                        add(CrashFixKind.ResetOverclock, w, new CrashLine(T("PC Checker has an overclock applied, and a graphics overclock is a likely cause.")), null);
                    }

                    add(CrashFixKind.CheckPowerSupply, w - 40, new CrashLine(T("A graphics card that crashes under load can be short of power (cables, power supply).")), null);
                    break;
                case CrashCause.Overheating:
                    add(CrashFixKind.CheckTemperatures, w - 10, why, null);
                    add(CrashFixKind.CheckFans, w - 40, why, null);
                    break;
                case CrashCause.PowerSupply:
                    add(CrashFixKind.CheckPowerSupply, w - 10, why, null);
                    break;
                case CrashCause.Storage:
                    add(CrashFixKind.CheckDisk, w, why, null);
                    break;
                case CrashCause.WindowsFiles:
                    add(CrashFixKind.RepairWindowsFiles, w, why, null);
                    add(CrashFixKind.UpdateWindows, w - 30, why, null);
                    break;
                case CrashCause.SecuritySoftware:
                    add(CrashFixKind.UninstallRecentProgram, w - 20, new CrashLine(T("Antivirus, anti-cheat and cheat tools load drivers that can cause this stop code.")), null);
                    break;
                case CrashCause.Firmware:
                    add(CrashFixKind.UpdateBios, w - 20, why, null);
                    break;
            }
        }
    }

    private static void SuddenShutdown(CrashFixInput input, Action<CrashFixKind, int, CrashLine, string?> add)
    {
        var verdict = input.Crash.Power is { } p ? KernelPowerRules.Verdict(p) : input.Crash.PowerButtonHeld ? KernelPowerVerdict.PowerButton : KernelPowerVerdict.PowerLoss;
        switch (verdict)
        {
            case KernelPowerVerdict.DuringSleep:
                add(CrashFixKind.UpdateDriver, 80, new CrashLine(T("It happened during sleep, which is usually a driver (graphics, network, chipset) problem.")), null);
                add(CrashFixKind.TurnOffFastStartup, 60, new CrashLine(T("Fast startup and sleep problems often go together.")), null);
                add(CrashFixKind.UpdateBios, 40, new CrashLine(T("The BIOS handles sleep and wake; an update often fixes sleep crashes.")), null);
                break;
            case KernelPowerVerdict.PowerButton:
                add(CrashFixKind.CheckTemperatures, 50, new CrashLine(T("A PC that freezes until the power button is held can be overheating.")), null);
                add(CrashFixKind.UpdateDriver, 45, new CrashLine(T("Freezes are often caused by a graphics or storage driver.")), null);
                add(CrashFixKind.MemoryDiagnostic, 35, new CrashLine(T("Hard freezes can come from unstable RAM.")), null);
                break;
            default:
                add(CrashFixKind.CheckPowerSupply, 80, new CrashLine(T("The PC switched off with no blue screen: power delivery is the most common cause.")), null);
                add(CrashFixKind.CheckTemperatures, 70, new CrashLine(T("A PC that overheats can switch itself off.")), null);
                add(CrashFixKind.BiosStabilityTest, 60, new CrashLine(T("Unstable overclocks and undervolts can make the PC reset without a blue screen.")), null);
                if (input.OverclockApplied)
                {
                    add(CrashFixKind.ResetOverclock, 85, new CrashLine(T("PC Checker has an overclock applied; unstable overclocks can make the PC reset.")), null);
                }

                add(CrashFixKind.CheckFans, 40, new CrashLine(T("A stopped fan can make the PC overheat and switch off.")), null);
                break;
        }
    }

    private static void App(CrashFixInput input, Action<CrashFixKind, int, CrashLine, string?> add)
    {
        var crash = input.Crash;
        if (crash.Kind == CrashKind.AppHang)
        {
            add(CrashFixKind.UpdateApp, 70, new CrashLine(T("Program hangs are usually fixed by an update of the program.")), crash.AppName);
            add(CrashFixKind.CheckDisk, 40, new CrashLine(T("A slow or failing drive can make programs stop responding.")), null);
            if (input.IsGame)
            {
                add(CrashFixKind.VerifyGameFiles, 50, new CrashLine(T("Damaged game files can make a game hang while loading.")), crash.AppName);
            }

            return;
        }

        if (FaultingModuleCatalog.Find(crash.ModuleName, crash.ModulePath, crash.ExceptionCode) is { } module)
        {
            var why = new CrashLine(T("The crash happened in {0}: {1}."), crash.ModuleName, new LocalizedText(module.Owner));
            string? target = module.Fix switch
            {
                CrashFixKind.ReinstallRuntimes => module.RuntimeFamily,
                CrashFixKind.DisableOverlay => module.Owner,
                CrashFixKind.UninstallRecentProgram => module.Owner,
                _ => crash.ModuleName,
            };
            add(module.Fix, 100, why, target);
            if (module.Category == ModuleCategory.GraphicsDriver)
            {
                add(CrashFixKind.RollBackDriver, 50, new CrashLine(T("If the crashes started after a graphics driver update, go back one version.")), crash.ModuleName);
            }

            if (module.Category is ModuleCategory.Mod or ModuleCategory.AntiCheat or ModuleCategory.GameFile && !input.IsGame)
            {
                add(CrashFixKind.UpdateApp, 60, why, crash.AppName);
            }
        }

        switch (crash.ExceptionCode)
        {
            case 0xC0000135 or 0xC0000139 or 0xC000007B or 0xC06D007E or 0xC0000142:
                add(input.IsGame ? CrashFixKind.ScanGameFolder : CrashFixKind.ReinstallRuntimes, 95,
                    new CrashLine(T("The error says a file the program needs is missing or the wrong version.")), input.IsGame ? crash.AppName : "vc14");
                add(CrashFixKind.ReinstallRuntimes, 80, new CrashLine(T("Missing or mixed-up runtime files are the usual cause of this error.")), "vc14");
                break;
            case 0xE0434352:
                add(CrashFixKind.RepairDotNet, 90, new CrashLine(T("This is a .NET exception: repairing .NET and updating the program are the usual fixes.")), null);
                break;
            case 0xC0000374:
                add(CrashFixKind.DisableOverlay, 70, new CrashLine(T("Heap corruption is often caused by overlays, mods or injected tools.")), null);
                add(CrashFixKind.MemoryDiagnostic, 30, new CrashLine(T("If many programs crash with heap corruption, the RAM may be faulty.")), null);
                break;
            case 0xC000001D or 0xC0000090:
                if (input.OverclockApplied)
                {
                    add(CrashFixKind.ResetOverclock, 80, new CrashLine(T("Illegal-instruction and floating-point crashes can come from an unstable overclock.")), null);
                }

                add(CrashFixKind.BiosStabilityTest, 40, new CrashLine(T("Illegal-instruction and floating-point crashes can come from an unstable overclock or undervolt.")), null);
                break;
            case 0xC0000006 or 0xC000009C or 0xC000009D or 0xC0000185:
                add(CrashFixKind.CheckDisk, 90, new CrashLine(T("The error says the program's files could not be read from the drive.")), null);
                break;
            case 0xC0000005 or 0xC0000409:
                add(CrashFixKind.UpdateApp, 60, new CrashLine(T("Memory errors inside a program are most often fixed by an update.")), crash.AppName);
                add(CrashFixKind.DisableOverlay, 45, new CrashLine(T("Overlays and mods are a common cause of this kind of crash.")), null);
                break;
        }

        if (input.IsGame)
        {
            add(CrashFixKind.VerifyGameFiles, 55, new CrashLine(T("Verifying the game's files repairs damaged or missing files.")), crash.AppName);
        }

        add(CrashFixKind.UpdateApp, 50, new CrashLine(T("An update of the program often fixes its crashes.")), crash.AppName);
    }

    private static void Boosts(CrashFixInput input, Action<CrashFixKind, int, CrashLine, string?> add)
    {
        var crash = input.Crash;
        string? blamed = BlamedDriver(crash);
        if (crash.Kind is CrashKind.BlueScreen or CrashKind.DriverReset && blamed is not null && crash.Blame is not { IsWindowsModule: true })
        {
            add(CrashFixKind.UpdateDriver, 110, new CrashLine(T("The crash points at {0}, a third-party driver."), blamed), blamed);
        }

        if (input.Driver is { NewerVersion: { Length: > 0 } newer } driver)
        {
            add(CrashFixKind.UpdateDriver, 125, new CrashLine(T("A newer version of {0} is available ({1})."), driver.FileName, newer), driver.FileName);
        }

        foreach (var pattern in input.Patterns)
        {
            switch (pattern.Kind)
            {
                case CrashPatternKind.AfterDriverInstall:
                    add(CrashFixKind.RollBackDriver, 115 + (pattern.Strength * 5), new CrashLine(T("The crashes started after a driver install ({0}); rolling it back undoes that change."), pattern.Subject ?? "?"), blamed ?? pattern.Subject);
                    break;
                case CrashPatternKind.AfterWindowsUpdate:
                    add(CrashFixKind.UninstallRecentUpdate, 70 + (pattern.Strength * 10), new CrashLine(T("The crashes started after a Windows update ({0})."), pattern.Subject ?? "?"), pattern.Subject);
                    break;
                case CrashPatternKind.AfterPcCheckerChange when pattern.Strength >= 3:
                    add(CrashFixKind.ResetOverclock, 140, new CrashLine(T("PC Checker applied an overclock shortly before this crash.")), null);
                    break;
                case CrashPatternKind.Overheating:
                    add(CrashFixKind.CheckTemperatures, 130, new CrashLine(T("The {0} was overheating just before the crash."), pattern.Subject ?? "CPU"), null);
                    add(CrashFixKind.CheckFans, 90, new CrashLine(T("Overheating often means a fan is slow, stopped or clogged with dust.")), null);
                    break;
                case CrashPatternKind.AtWake:
                    add(CrashFixKind.TurnOffFastStartup, 55, new CrashLine(T("Crashes around sleep and wake are often helped by turning off Fast startup.")), null);
                    break;
                case CrashPatternKind.SameModuleAcrossApps:
                    add(CrashFixKind.UpdateDriver, 85, new CrashLine(T("{0} crashed several programs."), pattern.Subject ?? "?"), pattern.Subject);
                    break;
            }
        }

        if (input.Context is { } context)
        {
            if (context.Overheating)
            {
                add(CrashFixKind.CheckTemperatures, 130, new CrashLine(T("The {0} was overheating just before the crash."), new LocalizedText(context.HotPart ?? T("CPU"))), null);
                add(CrashFixKind.CheckFans, 90, new CrashLine(T("Overheating often means a fan is slow, stopped or clogged with dust.")), null);
            }

            if (context.ChangesBefore.Any(CrashPatternRules.IsOverclock))
            {
                add(CrashFixKind.ResetOverclock, 140, new CrashLine(T("PC Checker applied an overclock shortly before this crash.")), null);
            }

            if (context.DiskErrorsDayBefore > 0)
            {
                add(CrashFixKind.CheckDisk, 105, new CrashLine(T("Drive errors were logged in the day before the crash.")), null);
            }

            if (context.WheaCorrectedHourBefore > 0)
            {
                add(CrashFixKind.BiosStabilityTest, 90, new CrashLine(T("The hardware corrected errors just before the crash, a typical sign of an unstable overclock or memory setting.")), null);
            }
        }

        if (crash.Whea is { } whea && whea.Components.Contains("Memory"))
        {
            add(CrashFixKind.MemoryDiagnostic, 105, new CrashLine(T("The hardware error came from the memory.")), null);
        }
    }

    /// <summary>The third-party driver a blue screen or reset points at, as a file name, or null.</summary>
    private static string? BlamedDriver(CrashEvent crash) =>
        crash.Kind == CrashKind.DriverReset && crash.Driver is { Length: > 0 } d ? (d.Contains('.') ? d : d + ".sys")
        : crash.Blame is { IsWindowsModule: false } b ? b.Module
        : null;
}
