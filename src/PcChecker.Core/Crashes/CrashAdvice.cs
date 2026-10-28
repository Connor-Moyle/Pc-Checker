using System.Globalization;

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
    public static readonly CrashLink Drivers = new("Check drivers", "drivers");
    public static readonly CrashLink Memory = new("Test memory", "memory");
    public static readonly CrashLink Storage = new("Check drives", "storage");
    public static readonly CrashLink Overclock = new("Reset overclock", "overclock");
    public static readonly CrashLink Health = new("See hardware errors", "health");
    public static readonly CrashLink Temperatures = new("Watch temperatures", "temperatures");
    public static readonly CrashLink Updates = new("Windows Update", "windows-update");
    public static readonly CrashLink Apps = new("Open Apps", "software");
    public static readonly CrashLink Runtimes = new("Check runtimes", "missing-dlls");

    private const string BestGuess = "This is a best guess from the memory dump, not proof.";

    public static CrashAdviceText For(CrashEvent e, bool overclockApplied)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind switch
        {
            CrashKind.BlueScreen => BlueScreen(e, overclockApplied),
            CrashKind.SuddenShutdown => SuddenShutdown(e, overclockApplied),
            CrashKind.DriverReset => DriverReset(e, overclockApplied),
            _ => App(e),
        };
    }

    /// <summary>One line for the timeline row.</summary>
    public static string Title(CrashEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind switch
        {
            CrashKind.BlueScreen => "Blue screen: " + (e.BugCheckCode is { } c ? StopCodeCatalog.NameOf(c) : "unknown stop code"),
            CrashKind.SuddenShutdown => e.PowerButtonHeld ? "Turned off with the power button" : "Sudden shutdown or restart",
            CrashKind.DriverReset => "Graphics driver reset" + (VendorOf(e) is { } v ? $" ({v})" : string.Empty),
            CrashKind.AppHang => $"{e.AppName} stopped responding",
            _ => $"{e.AppName} crashed",
        };
    }

    private static CrashAdviceText BlueScreen(CrashEvent e, bool overclockApplied)
    {
        var info = e.BugCheckCode is { } code ? StopCodeCatalog.Find(code) : null;
        string codeText = e.BugCheckCode is { } c ? BugCheckFormat.Code(c) : "unknown";
        string explanation = info?.Meaning
            ?? "Windows stopped because something went badly wrong. This stop code is not in PC Checker's list; Microsoft's reference explains it.";
        var steps = new List<string>(info?.FirstSteps ?? StopCodeCatalog.GeneralSteps);
        var pages = new List<CrashLink>();
        bool hardware = info?.Area is StopCodeArea.Hardware || e.BugCheckCode is 0x124 or 0x101 or 0x9C;
        if (hardware && overclockApplied)
        {
            pages.Add(Overclock);
            steps.Insert(0, "PC Checker has an overclock applied. Reset it to stock on the Overclock page first.");
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

        string? cause = e.Blame is { } blame ? $"{blame.Module} (best guess). {blame.Reason} {BestGuess}" : null;
        if (cause is null && info?.Area == StopCodeArea.Hardware)
        {
            cause = "The hardware itself reported the error, so no driver is to blame. An unstable overclock or undervolt, heat, memory settings or a failing part are the usual causes.";
        }

        if (cause is null && e.DumpPath is null)
        {
            cause = "No memory dump was found, so PC Checker cannot guess which driver was involved. Turn on small memory dumps in System Properties → Advanced → Startup and Recovery.";
        }

        string name = e.BugCheckCode is { } k ? StopCodeCatalog.NameOf(k) : "Unknown stop code";
        return new CrashAdviceText(
            Title(e),
            e.Blame is { IsWindowsModule: false } b ? $"{codeText}, probably {b.Module}" : codeText,
            $"{name} ({codeText}). {explanation}",
            cause,
            steps.Distinct().ToList(),
            pages.Distinct().ToList(),
            e.BugCheckCode is { } d ? StopCodeCatalog.DocsUrlFor(d) : StopCodeCatalog.ReferenceIndexUrl);
    }

    private static CrashAdviceText SuddenShutdown(CrashEvent e, bool overclockApplied)
    {
        var pages = new List<CrashLink>();
        var steps = new List<string>();
        string explanation;
        string? cause;
        if (e.PowerButtonHeld)
        {
            explanation = "The PC was turned off by holding the power button, so Windows could not shut down properly.";
            cause = "If the PC had frozen, the freeze is the real problem: look for blue screens or driver resets just before this.";
            steps.Add("Next time it freezes, wait a minute: Windows may recover or write a crash report.");
        }
        else
        {
            explanation = "The PC turned off or restarted without shutting down properly and without a blue screen. It lost power, the power supply switched off, or it froze hard.";
            cause = "Common causes: a power cut or loose cable, an overloaded or failing power supply, overheating, or an unstable overclock.";
            steps.Add("Check that the power cable and the power strip are fine, and that nothing else trips the power.");
            steps.Add("Watch temperatures under load; a PC that overheats can switch itself off.");
            steps.Add("If it happens under heavy load (games), the power supply may be too weak or failing.");
        }

        if (overclockApplied)
        {
            pages.Add(Overclock);
            steps.Insert(0, "PC Checker has an overclock applied. Reset it to stock on the Overclock page and see whether the shutdowns stop.");
        }
        else
        {
            steps.Add("If anything is overclocked or undervolted (including XMP/EXPO memory profiles), go back to default settings.");
        }

        pages.Add(Temperatures);
        pages.Add(Health);
        return new CrashAdviceText(Title(e), e.PowerButtonHeld ? "Power button held down" : "No blue screen was recorded", explanation, cause, steps, pages, null);
    }

    private static CrashAdviceText DriverReset(CrashEvent e, bool overclockApplied)
    {
        var info = (e.BugCheckCode is { } code ? StopCodeCatalog.Find(code) : null) is { Area: StopCodeArea.Graphics } known ? known : StopCodeCatalog.Find(0x117)!;
        var steps = new List<string>(info.FirstSteps);
        var pages = new List<CrashLink> { Drivers, Temperatures };
        if (overclockApplied)
        {
            pages.Insert(0, Overclock);
            steps.Insert(0, "PC Checker has a graphics overclock applied. Reset it to stock on the Overclock page first.");
        }

        string driver = e.Driver is { Length: > 0 } d ? d : "the graphics driver";
        return new CrashAdviceText(
            Title(e),
            e.Driver is { Length: > 0 } ? $"{e.Driver} was restarted" : "Windows restarted the graphics driver",
            "The graphics card stopped responding for a few seconds and Windows restarted its driver. The screen may have gone black or a game may have closed. A few a month can be normal; many point at a driver, overclock, heat or power problem.",
            $"{driver} stopped responding.",
            steps,
            pages,
            info.DocsUrl);
    }

    private static CrashAdviceText App(CrashEvent e)
    {
        var steps = new List<string>();
        var pages = new List<CrashLink>();
        string explanation;
        string? cause = null;
        string summary;
        if (e.Kind == CrashKind.AppHang)
        {
            explanation = "The program stopped responding and was closed, by you or by Windows.";
            summary = string.IsNullOrWhiteSpace(e.AppVersion) ? "Stopped responding" : "Version " + e.AppVersion;
            steps.Add("Update the program.");
            steps.Add("If it hangs while saving or loading, check that the drive is not nearly full and is healthy.");
            pages.Add(Apps);
            pages.Add(Storage);
        }
        else
        {
            var ex = e.ExceptionCode is { } code ? ExceptionCodeCatalog.Find(code) : null;
            string exText = e.ExceptionCode is { } c2 ? ExceptionCodeCatalog.Describe(c2) : "an unknown error";
            explanation = ex is null ? $"The program closed because of {exText}." : $"The program closed because of {exText}. {ex.Meaning}";
            summary = (e.ModuleName is { Length: > 0 } mod ? $"In {mod}, " : string.Empty) + exText;
            if (ex is not null)
            {
                steps.Add(ex.FirstStep);
            }

            if (e.ModuleName is { Length: > 0 } module)
            {
                cause = CrashBlameRules.IsWindowsModule(module)
                    ? $"The error happened in {module}, a part of Windows. The cause is usually the program itself or an add-on it loaded."
                    : string.Equals(module, e.AppName, StringComparison.OrdinalIgnoreCase)
                        ? "The error happened in the program's own code."
                        : $"The error happened in {module}, a file the program loaded (an add-on, overlay, driver or runtime).";
            }

            steps.Add("Update or repair the program; reinstall it if crashes continue.");
            steps.Add("Turn off overlays and mods (recording, FPS counters, RGB tools) and try again.");
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

        return new CrashAdviceText(Title(e), summary, explanation, cause, steps.Distinct().ToList(), pages.Distinct().ToList(), null);
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
