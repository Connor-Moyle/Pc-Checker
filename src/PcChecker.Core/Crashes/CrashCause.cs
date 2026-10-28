using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>
/// The usual suspects behind a crash, in the words the page uses. A stop code lists them most likely first; the fix
/// ranking (<see cref="CrashFixRules"/>) and the crash summary weigh them with what else is known about the PC.
/// </summary>
public enum CrashCause
{
    /// <summary>A driver (a small program that runs a device) has a bug or is outdated.</summary>
    Driver,

    /// <summary>The RAM, or memory settings such as XMP or EXPO that run it faster than its default.</summary>
    Memory,

    /// <summary>A processor overclock or undervolt (PBO, Curve Optimizer, Intel XTU, a BIOS multiplier).</summary>
    CpuOverclock,

    /// <summary>The graphics card: its overclock, its power cables or the card itself.</summary>
    GraphicsCard,

    /// <summary>A part ran too hot.</summary>
    Overheating,

    /// <summary>The power supply cannot deliver steady power (too weak, failing or a loose cable).</summary>
    PowerSupply,

    /// <summary>A drive, its cable or the file system on it.</summary>
    Storage,

    /// <summary>Damaged Windows files or a bad update.</summary>
    WindowsFiles,

    /// <summary>Antivirus, anti-cheat, cheat tools or malware that hook deep into Windows.</summary>
    SecuritySoftware,

    /// <summary>The BIOS (UEFI firmware) or its settings.</summary>
    Firmware,
}

/// <summary>Plain names for <see cref="CrashCause"/> (English keys; the page translates them).</summary>
public static class CrashCauseText
{
    public static string Name(CrashCause cause) => cause switch
    {
        CrashCause.Driver => T("A driver"),
        CrashCause.Memory => T("RAM or memory settings (XMP/EXPO)"),
        CrashCause.CpuOverclock => T("CPU overclock or undervolt (PBO, Curve Optimizer)"),
        CrashCause.GraphicsCard => T("The graphics card or its overclock"),
        CrashCause.Overheating => T("Overheating"),
        CrashCause.PowerSupply => T("The power supply"),
        CrashCause.Storage => T("A drive or its cable"),
        CrashCause.WindowsFiles => T("Damaged Windows files or a bad update"),
        CrashCause.SecuritySoftware => T("Antivirus, anti-cheat or malware"),
        _ => T("The BIOS or its settings"),
    };
}
