using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>One exception code an app crash can report, in plain words.</summary>
public sealed record ExceptionCodeInfo(uint Code, string Name, string Meaning, string FirstStep);

/// <summary>Plain-language meanings of the exception codes Windows records when a program crashes (Application Error 1000).</summary>
public static class ExceptionCodeCatalog
{
    public static IReadOnlyList<ExceptionCodeInfo> All { get; } =
    [
        new(0xC0000005, T("Access violation"),
            T("The program tried to use memory it did not own. Usually a bug in the program or in an add-on, overlay or mod it loaded."),
            T("Update the program, then turn off overlays and mods. If many programs crash this way, test the memory.")),
        new(0xC0000409, T("Stack buffer overrun"),
            T("The program detected that its own memory was damaged and closed itself for safety. Often a program bug, sometimes a bad add-on or unstable hardware."),
            T("Update or repair the program. If several programs do this, reset any overclock and test the memory.")),
        new(0xC0000374, T("Heap corruption"),
            T("The program's memory bookkeeping was damaged. Usually a program or add-on bug, sometimes faulty RAM."),
            T("Update or reinstall the program and remove add-ons; test the memory if it keeps happening.")),
        new(0x80000003, T("Breakpoint"),
            T("The program stopped itself on purpose because something unexpected happened (an assertion)."),
            T("Update the program; if it keeps happening, report it to its maker with the details.")),
        new(0xE0434352, T(".NET exception"),
            T("A program built with .NET hit an error it did not handle."),
            T("Update the program and install the latest .NET runtime from the Missing DLLs page.")),
        new(0xC000001D, T("Illegal instruction"),
            T("The program used a processor instruction the CPU could not run. Old CPUs without newer instructions, or an unstable overclock, cause this."),
            T("Check that the CPU meets the program's requirements and reset any overclock.")),
        new(0xC0000094, T("Integer divide by zero"),
            T("The program divided a number by zero. This is a bug in the program."),
            T("Update the program or report the crash to its maker.")),
        new(0xC00000FD, T("Stack overflow"),
            T("The program used up its stack memory, usually because of a bug that loops forever."),
            T("Update the program or report the crash to its maker.")),
        new(0xC0000006, T("In-page error"),
            T("The program's code could not be read from the drive, for example from a disconnected network or USB drive or a failing disk."),
            T("Check the drive the program is installed on on the Storage page.")),
        new(0xC0000135, T("DLL not found"),
            T("A file the program needs to start is missing."),
            T("Repair or reinstall the program, and check the Missing DLLs page for runtime packages.")),
        new(0xC0000142, T("DLL initialization failed"),
            T("A file the program loads at start-up failed to start, often a damaged runtime or a blocked add-on."),
            T("Repair the program and its runtimes (Missing DLLs page) and restart the PC.")),
        new(0xC0000417, T("Invalid parameter"),
            T("The program passed bad data to a Windows or C runtime function and was closed. A program bug."),
            T("Update the program or report the crash to its maker.")),
        new(0xC0000602, T("Fail fast exception"),
            T("The program found a serious internal problem and closed itself straight away."),
            T("Update or repair the program; for Windows apps, reset them in Settings → Apps.")),
        new(0xCFFFFFFF, T("Application hang"),
            T("The program stopped responding and was closed."),
            T("Wait longer before closing it next time, update the program and check that the drive is not full.")),
    ];

    private static readonly Dictionary<uint, ExceptionCodeInfo> ByCode = All.ToDictionary(e => e.Code);

    public static ExceptionCodeInfo? Find(uint code) => ByCode.TryGetValue(code, out var info) ? info : null;

    /// <summary>"Access violation (0xc0000005)" or just the code when unknown.</summary>
    public static string Describe(uint code, Localization.Translator? tr = null) =>
        Find(code) is { } info ? $"{(tr ?? Localization.Translator.English).T(info.Name)} ({BugCheckFormat.Exception(code)})" : BugCheckFormat.Exception(code);
}
