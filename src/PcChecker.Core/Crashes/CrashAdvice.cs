using System.Globalization;
using PcChecker.Core.Localization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>A button to another PC Checker page (by its navigation key).</summary>
public sealed record CrashLink(string Text, string PageKey);

/// <summary>What the details pane says about one crash.</summary>
public sealed record CrashAdviceText(
    string Title,
    string Summary,
    string Explanation,
    string? LikelyCause,
    IReadOnlyList<string> Steps,
    IReadOnlyList<CrashLink> Pages,
    string? DocsUrl);

/// <summary>
/// Plain-language title, explanation, likely cause, first steps and related pages for a crash, built from the stop
/// code and exception code catalogs, the "probably caused by" guess and whether an overclock is applied.
/// </summary>
public static class CrashAdvice
{
    // Link texts are English keys; the page translates them when it shows the buttons.
    public static readonly CrashLink Drivers = new(T("Check drivers"), "drivers");
    public static readonly CrashLink Memory = new(T("Test memory"), "memory");
    public static readonly CrashLink Storage = new(T("Check drives"), "storage");
    public static readonly CrashLink Overclock = new(T("Reset overclock"), "overclock");
    public static readonly CrashLink Health = new(T("See hardware errors"), "health");
    public static readonly CrashLink Temperatures = new(T("Watch temperatures"), "temperatures");
    public static readonly CrashLink Updates = new(T("Windows Update"), "windows-update");
    public static readonly CrashLink Apps = new(T("Open Apps"), "software");
    public static readonly CrashLink Runtimes = new(T("Check runtimes"), "missing-dlls");

    /// <summary>
    /// The advice for <paramref name="e"/>, in the language of <paramref name="tr"/> (English when null). The catalog
    /// texts are looked up by their English text, so a missing translation stays English.
    /// </summary>
    public static CrashAdviceText For(CrashEvent e, bool overclockApplied, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        tr ??= Translator.English;
        return e.Kind switch
        {
            CrashKind.BlueScreen => BlueScreen(e, overclockApplied, tr),
            CrashKind.SuddenShutdown => SuddenShutdown(e, overclockApplied, tr),
            CrashKind.DriverReset => DriverReset(e, overclockApplied, tr),
            _ => App(e, tr),
        };
    }

    /// <summary>One line for the timeline row.</summary>
    public static string Title(CrashEvent e, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        tr ??= Translator.English;
        return e.Kind switch
        {
            CrashKind.BlueScreen => e.BugCheckCode is { } c ? tr.F("Blue screen: {0}", StopCodeCatalog.NameOf(c, tr)) : tr.T("Blue screen: unknown stop code"),
            CrashKind.SuddenShutdown => e.PowerButtonHeld ? tr.T("Turned off with the power button") : tr.T("Sudden shutdown or restart"),
            CrashKind.DriverReset => VendorOf(e) is { } v ? tr.F("Graphics driver reset ({0})", v) : tr.T("Graphics driver reset"),
            CrashKind.AppHang => tr.F("{0} stopped responding", e.AppName),
            _ => tr.F("{0} crashed", e.AppName),
        };
    }

    private static CrashAdviceText BlueScreen(CrashEvent e, bool overclockApplied, Translator tr)
    {
        var info = e.BugCheckCode is { } code ? StopCodeCatalog.Find(code) : null;
        string codeText = e.BugCheckCode is { } c ? BugCheckFormat.Code(c) : tr.T("unknown");
        string explanation = info is not null
            ? tr.T(info.Meaning)
            : tr.T("Windows stopped because something went badly wrong. This stop code is not in PC Checker's list; Microsoft's reference explains it.");
        var steps = new List<string>((info?.FirstSteps ?? StopCodeCatalog.GeneralSteps).Select(tr.T));
        var pages = new List<CrashLink>();
        bool hardware = info?.Area is StopCodeArea.Hardware || e.BugCheckCode is 0x124 or 0x101 or 0x9C;
        if (hardware && overclockApplied)
        {
            pages.Add(Overclock);
            steps.Insert(0, tr.T("PC Checker has an overclock applied. Reset it to stock on the Overclock page first."));
        }

        switch (info?.Area)
        {
            case StopCodeArea.Graphics:
            case StopCodeArea.Driver:
            case StopCodeArea.Power:
                pages.Add(Drivers);
                break;
            case StopCodeArea.Memory:
                pages.Add(Memory);
                break;
            case StopCodeArea.Storage:
                pages.Add(Storage);
                break;
            case StopCodeArea.Hardware:
                pages.Add(Health);
                pages.Add(Temperatures);
                break;
            case StopCodeArea.System:
                pages.Add(Updates);
                break;
            default:
                pages.Add(Drivers);
                break;
        }

        if (e.Blame is { IsWindowsModule: false })
        {
            pages.Add(Drivers);
        }

        string? cause = e.Blame is { } blame
            ? tr.F("{0} (best guess). {1} This is a best guess from the memory dump, not proof.", blame.Module, BlameReason(blame, tr))
            : null;
        if (cause is null && info?.Area == StopCodeArea.Hardware)
        {
            cause = tr.T("The hardware itself reported the error, so no driver is to blame. An unstable overclock or undervolt, heat, memory settings or a failing part are the usual causes.");
        }

        if (cause is null && e.DumpPath is null)
        {
            cause = tr.T("No memory dump was found, so PC Checker cannot guess which driver was involved. Turn on small memory dumps in System Properties → Advanced → Startup and Recovery.");
        }

        string name = e.BugCheckCode is { } k ? StopCodeCatalog.NameOf(k, tr) : tr.T("Unknown stop code");
        return new CrashAdviceText(
            Title(e, tr),
            e.Blame is { IsWindowsModule: false } b ? tr.F("{0}, probably {1}", codeText, b.Module) : codeText,
            $"{name} ({codeText}). {explanation}",
            cause,
            steps.Distinct().ToList(),
            pages.Distinct().ToList(),
            e.BugCheckCode is { } d ? StopCodeCatalog.DocsUrlFor(d) : StopCodeCatalog.ReferenceIndexUrl);
    }

    private static CrashAdviceText SuddenShutdown(CrashEvent e, bool overclockApplied, Translator tr)
    {
        var pages = new List<CrashLink>();
        var steps = new List<string>();
        string explanation;
        string? cause;
        if (e.PowerButtonHeld)
        {
            explanation = tr.T("The PC was turned off by holding the power button, so Windows could not shut down properly.");
            cause = tr.T("If the PC had frozen, the freeze is the real problem: look for blue screens or driver resets just before this.");
            steps.Add(tr.T("Next time it freezes, wait a minute: Windows may recover or write a crash report."));
        }
        else
        {
            explanation = tr.T("The PC turned off or restarted without shutting down properly and without a blue screen. It lost power, the power supply switched off, or it froze hard.");
            cause = tr.T("Common causes: a power cut or loose cable, an overloaded or failing power supply, overheating, or an unstable overclock.");
            steps.Add(tr.T("Check that the power cable and the power strip are fine, and that nothing else trips the power."));
            steps.Add(tr.T("Watch temperatures under load; a PC that overheats can switch itself off."));
            steps.Add(tr.T("If it happens under heavy load (games), the power supply may be too weak or failing."));
        }

        if (overclockApplied)
        {
            pages.Add(Overclock);
            steps.Insert(0, tr.T("PC Checker has an overclock applied. Reset it to stock on the Overclock page and see whether the shutdowns stop."));
        }
        else
        {
            steps.Add(tr.T("If anything is overclocked or undervolted (including XMP/EXPO memory profiles), go back to default settings."));
        }

        pages.Add(Temperatures);
        pages.Add(Health);
        return new CrashAdviceText(Title(e, tr), e.PowerButtonHeld ? tr.T("Power button held down") : tr.T("No blue screen was recorded"), explanation, cause, steps, pages, null);
    }

    private static CrashAdviceText DriverReset(CrashEvent e, bool overclockApplied, Translator tr)
    {
        var info = (e.BugCheckCode is { } code ? StopCodeCatalog.Find(code) : null) is { Area: StopCodeArea.Graphics } known ? known : StopCodeCatalog.Find(0x117)!;
        var steps = new List<string>(info.FirstSteps.Select(tr.T));
        var pages = new List<CrashLink> { Drivers, Temperatures };
        if (overclockApplied)
        {
            pages.Insert(0, Overclock);
            steps.Insert(0, tr.T("PC Checker has a graphics overclock applied. Reset it to stock on the Overclock page first."));
        }

        bool named = e.Driver is { Length: > 0 };
        return new CrashAdviceText(
            Title(e, tr),
            named ? tr.F("{0} was restarted", e.Driver) : tr.T("Windows restarted the graphics driver"),
            tr.T("The graphics card stopped responding for a few seconds and Windows restarted its driver. The screen may have gone black or a game may have closed. A few a month can be normal; many point at a driver, overclock, heat or power problem."),
            named ? tr.F("{0} stopped responding.", e.Driver) : tr.T("The graphics driver stopped responding."),
            steps,
            pages,
            info.DocsUrl);
    }

    private static CrashAdviceText App(CrashEvent e, Translator tr)
    {
        var steps = new List<string>();
        var pages = new List<CrashLink>();
        string explanation;
        string? cause = null;
        string summary;
        if (e.Kind == CrashKind.AppHang)
        {
            explanation = tr.T("The program stopped responding and was closed, by you or by Windows.");
            summary = string.IsNullOrWhiteSpace(e.AppVersion) ? tr.T("Stopped responding") : tr.F("Version {0}", e.AppVersion);
            steps.Add(tr.T("Update the program."));
            steps.Add(tr.T("If it hangs while saving or loading, check that the drive is not nearly full and is healthy."));
            pages.Add(Apps);
            pages.Add(Storage);
        }
        else
        {
            var ex = e.ExceptionCode is { } code ? ExceptionCodeCatalog.Find(code) : null;
            string exText = e.ExceptionCode is { } c2 ? ExceptionCodeCatalog.Describe(c2, tr) : tr.T("an unknown error");
            explanation = tr.F("The program closed because of {0}.", exText) + (ex is null ? string.Empty : " " + tr.T(ex.Meaning));
            summary = e.ModuleName is { Length: > 0 } mod ? tr.F("In {0}, {1}", mod, exText) : exText;
            if (ex is not null)
            {
                steps.Add(tr.T(ex.FirstStep));
            }

            if (e.ModuleName is { Length: > 0 } module)
            {
                cause = CrashBlameRules.IsWindowsModule(module)
                    ? tr.F("The error happened in {0}, a part of Windows. The cause is usually the program itself or an add-on it loaded.", module)
                    : string.Equals(module, e.AppName, StringComparison.OrdinalIgnoreCase)
                        ? tr.T("The error happened in the program's own code.")
                        : tr.F("The error happened in {0}, a file the program loaded (an add-on, overlay, driver or runtime).", module);
            }

            steps.Add(tr.T("Update or repair the program; reinstall it if crashes continue."));
            steps.Add(tr.T("Turn off overlays and mods (recording, FPS counters, RGB tools) and try again."));
            pages.Add(Apps);
            if (e.ExceptionCode is 0xC0000135 or 0xC0000142 or 0xE0434352)
            {
                pages.Add(Runtimes);
            }
            else if (e.ExceptionCode is 0xC0000006)
            {
                pages.Add(Storage);
            }
            else if (e.ExceptionCode is 0xC0000005 or 0xC0000374 or 0xC0000409 or 0xC000001D)
            {
                pages.Add(Memory);
            }
        }

        return new CrashAdviceText(Title(e, tr), summary, explanation, cause, steps.Distinct().ToList(), pages.Distinct().ToList(), null);
    }

    /// <summary>
    /// The blame reason in the page's language. <see cref="CrashBlameRules"/> writes English reasons; the parameter
    /// number is the only part that varies.
    /// </summary>
    private static string BlameReason(CrashBlame blame, Translator tr)
    {
        const string ParameterPrefix = "Stop code parameter ";
        const string ParameterSuffix = " points inside this file.";
        const string WindowsSuffix = " It is part of Windows, so the real cause is often a driver that called it.";
        string reason = blame.Reason;
        string tail = string.Empty;
        if (reason.EndsWith(WindowsSuffix, StringComparison.Ordinal))
        {
            reason = reason[..^WindowsSuffix.Length];
            tail = " " + tr.T(WindowsSuffix.TrimStart());
        }

        if (reason.StartsWith(ParameterPrefix, StringComparison.Ordinal) && reason.EndsWith(ParameterSuffix, StringComparison.Ordinal)
            && int.TryParse(reason.AsSpan(ParameterPrefix.Length, reason.Length - ParameterPrefix.Length - ParameterSuffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            return tr.F("Stop code parameter {0} points inside this file.", n) + tail;
        }

        return tr.T(reason) + tail;
    }

    private static string? VendorOf(CrashEvent e) => CrashRules.GraphicsVendor(e.Driver);

    /// <summary>Plain text for "Copy details": everything the page knows about the crash.</summary>
    public static string DetailsText(CrashEvent e, CrashAdviceText advice)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(advice);
        var sb = new System.Text.StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.Append(label).Append(": ").AppendLine(value);
            }
        }

        sb.AppendLine(advice.Title);
        Line("When", e.Time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line("Source", e.Source);
        if (e.BugCheckCode is { } code)
        {
            Line("Stop code", $"{StopCodeCatalog.NameOf(code)} {BugCheckFormat.WithParameters(code, e.Parameters)}");
        }

        Line("Probably caused by", e.Blame is { } b ? $"{b.Module} (best guess: {b.Reason})" : null);
        Line("Graphics driver", e.Driver);
        Line("Program", e.AppName is null ? null : $"{e.AppName} {e.AppVersion}".Trim());
        Line("Program path", e.AppPath);
        Line("Faulting module", e.ModuleName is null ? null : $"{e.ModuleName} {e.ModuleVersion}".Trim());
        Line("Exception", e.ExceptionCode is { } ex ? ExceptionCodeCatalog.Describe(ex) : null);
        Line("Offset", e.FaultOffset);
        Line("Windows build", e.OsBuild?.ToString(CultureInfo.InvariantCulture));
        Line("Dump file", e.DumpPath);
        Line("Report id", e.ReportId);
        sb.AppendLine();
        sb.AppendLine(advice.Explanation);
        if (advice.LikelyCause is { } cause)
        {
            sb.AppendLine(cause);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The question handed to Ask Claude.</summary>
    public static string AskPrompt(CrashEvent e, CrashAdviceText advice) =>
        "My PC had this crash. Explain in plain English what it most likely means for my PC and what I should try first, "
        + "safest steps first.\n\n" + DetailsText(e, advice);
}
