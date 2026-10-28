using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>One exception code an app crash can report, in plain words.</summary>
public sealed record ExceptionCodeInfo(uint Code, string Name, string Meaning, string FirstStep);

/// <summary>
/// Plain-language meanings of the exception and status codes Windows records when a program crashes (Application
/// Error 1000) and that stop codes such as 0x3B, 0x1E or 0x7A carry as a parameter.
/// </summary>
public static class ExceptionCodeCatalog
{
    private static readonly string UpdateOrReport = T("Update the program or report the crash to its maker.");
    private static readonly string CheckDriveStep = T("Check the drive the program is installed on on the Storage page.");

    public static IReadOnlyList<ExceptionCodeInfo> All { get; } =
    [
        new(0xC0000005, T("Access violation"),
            T("The program tried to use memory it did not own. Usually a bug in the program or in an add-on, overlay or mod it loaded."),
            T("Update the program, then turn off overlays and mods. If many programs crash this way, test the memory.")),
        new(0xC0000409, T("Stack buffer overrun"),
            T("The program detected that its own memory was damaged and closed itself for safety. Often a program bug, sometimes a bad add-on or unstable hardware."),
            T("Update or repair the program. If several programs do this, reset any overclock and test the memory.")),
        new(0xC0000374, T("Heap corruption"),
            T("The program's memory bookkeeping was damaged. Usually a program, mod or overlay bug, sometimes faulty RAM."),
            T("Update or reinstall the program and remove add-ons; test the memory if it keeps happening.")),
        new(0x80000003, T("Breakpoint"),
            T("The program stopped itself on purpose because something unexpected happened (an assertion)."),
            T("Update the program; if it keeps happening, report it to its maker with the details.")),
        new(0xE0434352, T(".NET exception"),
            T("A program built with .NET hit an error it did not handle."),
            T("Update the program and repair .NET: install the latest .NET runtime, or run Microsoft's .NET Framework Repair Tool.")),
        new(0xE06D7363, T("C++ exception"),
            T("A program written in C++ hit an error it did not handle. Often a missing or damaged file, or a program bug."),
            T("Repair or reinstall the program and its Visual C++ runtimes (Missing DLLs page).")),
        new(0xC000001D, T("Illegal instruction"),
            T("The program used a processor instruction the CPU could not run. Old CPUs without newer instructions (such as AVX2), or an unstable overclock, cause this."),
            T("Check that the CPU meets the program's requirements and reset any overclock.")),
        new(0xC0000096, T("Privileged instruction"),
            T("The program used a processor instruction only Windows itself may use. A program bug or damaged program file."),
            T("Repair or reinstall the program.")),
        new(0xC0000094, T("Integer divide by zero"),
            T("The program divided a number by zero. This is a bug in the program."),
            UpdateOrReport),
        new(0xC000008E, T("Floating-point divide by zero"),
            T("The program divided a number by zero. This is a bug in the program."),
            UpdateOrReport),
        new(0xC0000090, T("Invalid floating-point operation"),
            T("The program did a calculation the processor could not do. Usually a program bug, sometimes an unstable overclock."),
            T("Update the program; reset any overclock if several programs do this.")),
        new(0xC000008C, T("Array bounds exceeded"),
            T("The program read past the end of a list. This is a bug in the program."),
            UpdateOrReport),
        new(0x80000002, T("Data misalignment"),
            T("The program read data from an address the processor could not use. A program bug."),
            UpdateOrReport),
        new(0xC00000FD, T("Stack overflow"),
            T("The program used up its stack memory, usually because of a bug that loops forever."),
            UpdateOrReport),
        new(0xC0000006, T("In-page error"),
            T("The program's code could not be read from the drive, for example from a disconnected network or USB drive or a failing disk."),
            CheckDriveStep),
        new(0xC0000008, T("Invalid handle"),
            T("The program used something it had already closed. A program bug, sometimes caused by an add-on."),
            UpdateOrReport),
        new(0xC0000017, T("Out of memory"),
            T("The program could not get the memory it asked for."),
            T("Close other programs, check that the page file is on (Memory page) and that the Windows drive has free space.")),
        new(0xC000012D, T("Out of virtual memory"),
            T("Windows ran out of memory plus page file, so the program could not continue."),
            T("Let Windows manage the page file size (Memory page) and free up space on the Windows drive.")),
        new(0xC0000135, T("DLL not found"),
            T("A file the program needs to start is missing."),
            T("Repair or reinstall the program, and check the Missing DLLs page for runtime packages.")),
        new(0xC0000139, T("Entry point not found"),
            T("A file the program loads is the wrong version, often an old or mixed-up runtime DLL."),
            T("Reinstall the program's runtimes (Missing DLLs page) and remove stray DLL files copied into the game folder.")),
        new(0xC000007B, T("Bad image format"),
            T("A 32-bit and a 64-bit file were mixed up, or a program file is damaged. Usually a runtime DLL copied by hand."),
            T("Reinstall the Visual C++ and DirectX runtimes (Missing DLLs page) and repair the program.")),
        new(0xC0000142, T("DLL initialization failed"),
            T("A file the program loads at start-up failed to start, often a damaged runtime or a blocked add-on."),
            T("Repair the program and its runtimes (Missing DLLs page) and restart the PC.")),
        new(0xC06D007E, T("Delay-loaded module not found"),
            T("A file the program loads later on is missing."),
            T("Repair or reinstall the program and its runtimes (Missing DLLs page).")),
        new(0xC0000417, T("Invalid parameter"),
            T("The program passed bad data to a Windows or C runtime function and was closed. A program bug."),
            UpdateOrReport),
        new(0xC0000420, T("Assertion failure"),
            T("The program checked something that should always be true, found it false and stopped."),
            UpdateOrReport),
        new(0xC0000602, T("Fail fast exception"),
            T("The program found a serious internal problem and closed itself straight away."),
            T("Update or repair the program; for Windows apps, reset them in Settings → Apps.")),
        new(0xC0000194, T("Possible deadlock"),
            T("Two parts of the program waited for each other forever."),
            UpdateOrReport),
        new(0xC000041D, T("Unhandled error in a callback"),
            T("Code that Windows called back into the program failed, often in an add-on, overlay or input hook."),
            T("Turn off overlays and input or macro tools, then update the program.")),
        new(0x40000015, T("Fatal application exit"),
            T("The program ended itself because of an error it could not recover from."),
            UpdateOrReport),
        new(0xC000009C, T("Device data error"),
            T("A drive returned bad data: there may be bad sectors."),
            CheckDriveStep),
        new(0xC000009D, T("Device not connected"),
            T("A drive disappeared while it was being read: a loose cable, a sleeping USB drive or a failing disk."),
            T("Check the drive's cable and health on the Storage page.")),
        new(0xC0000185, T("Input/output device error"),
            T("A drive did not answer correctly."),
            CheckDriveStep),
        new(0xC000009A, T("Insufficient resources"),
            T("Windows ran out of an internal resource it needed for the request."),
            T("Restart the PC, update drivers and check memory use on the Memory page.")),
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
