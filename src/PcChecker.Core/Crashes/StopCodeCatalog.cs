using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>Which part of the PC a stop code usually points at; decides which PC Checker pages the advice links to.</summary>
public enum StopCodeArea
{
    /// <summary>A driver (a small program that runs a device) did something wrong.</summary>
    Driver,

    /// <summary>The graphics card or its driver.</summary>
    Graphics,

    /// <summary>The RAM, or a driver that damaged memory.</summary>
    Memory,

    /// <summary>A drive, its cable or the file system.</summary>
    Storage,

    /// <summary>The processor, motherboard or an unstable overclock.</summary>
    Hardware,

    /// <summary>Sleep, power states or the BIOS power tables.</summary>
    Power,

    /// <summary>A core part of Windows itself.</summary>
    System,
}

/// <summary>One stop code: its name, what it means in plain words and the first things to try.</summary>
public sealed record StopCodeInfo(uint Code, string Name, StopCodeArea Area, string Meaning, IReadOnlyList<string> FirstSteps, string? DocsSlug)
{
    /// <summary>Microsoft's bug check reference page for this code (or the index when there is no page of its own).</summary>
    public string DocsUrl => StopCodeCatalog.DocsUrl(DocsSlug);
}

/// <summary>
/// Plain-language explanations and first steps for the most common stop codes, with links to Microsoft's bug check
/// reference. Unknown codes still get the reference index and the general advice.
/// </summary>
public static class StopCodeCatalog
{
    /// <summary>Microsoft's bug check code reference (the index of every stop code).</summary>
    public const string ReferenceIndexUrl = "https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-code-reference2";

    private const string DocsBase = "https://learn.microsoft.com/windows-hardware/drivers/debugger/";

    private static readonly string UpdateDriver = T("Update the driver named under Probably caused by, from the device maker's website or the Drivers page.");
    private static readonly string RecentChange = T("Think about what changed just before the crashes started (a new driver, program, update or device) and undo it.");
    private static readonly string GpuDriver = T("Install the latest graphics driver from NVIDIA, AMD or Intel. If the crashes started after an update, go back one version.");
    private static readonly string MemoryTest = T("Test the memory: run the quick memory test on the Memory page, or Windows Memory Diagnostic for a full pass.");
    private static readonly string ResetOverclock = T("If anything is overclocked (CPU, graphics card, or memory with XMP/EXPO), go back to default settings and see whether the crashes stop.");
    private static readonly string CheckDrive = T("Check the drive's health on the Storage page and run a disk check.");
    private static readonly string WindowsUpdate = T("Install all waiting Windows updates.");
    private static readonly string Temperatures = T("Watch temperatures under load on the Temperatures page; clean dust out of the case if they run hot.");
    private static readonly string Sfc = T("Repair Windows files: run \"sfc /scannow\" and then \"DISM /Online /Cleanup-Image /RestoreHealth\" in an administrator terminal.");
    private static readonly string Bios = T("Update the BIOS (UEFI) from the motherboard or PC maker's website.");

    public static IReadOnlyList<StopCodeInfo> All { get; } =
    [
        new(0x01, "APC_INDEX_MISMATCH", StopCodeArea.Driver,
            T("A driver left the system in a state it should have cleaned up. This is almost always a driver bug."),
            [UpdateDriver, RecentChange, WindowsUpdate], "bug-check-0x1--apc-index-mismatch"),
        new(0x0A, "IRQL_NOT_LESS_OR_EQUAL", StopCodeArea.Driver,
            T("A driver touched memory it was not allowed to at that moment. Usually a faulty or outdated driver, sometimes bad RAM."),
            [UpdateDriver, RecentChange, MemoryTest], "bug-check-0xa--irql-not-less-or-equal"),
        new(0x19, "BAD_POOL_HEADER", StopCodeArea.Driver,
            T("Windows found its memory bookkeeping damaged. A driver usually wrote where it should not."),
            [UpdateDriver, RecentChange, MemoryTest], "bug-check-0x19--bad-pool-header"),
        new(0x1A, "MEMORY_MANAGEMENT", StopCodeArea.Memory,
            T("Windows found a serious problem in how memory is used. Faulty or unstable RAM is a common cause, followed by drivers."),
            [MemoryTest, ResetOverclock, UpdateDriver], "bug-check-0x1a--memory-management"),
        new(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", StopCodeArea.Driver,
            T("A driver or part of Windows hit an error it could not handle."),
            [UpdateDriver, RecentChange, MemoryTest], "bug-check-0x1e--kmode-exception-not-handled"),
        new(0x24, "NTFS_FILE_SYSTEM", StopCodeArea.Storage,
            T("The file system on a drive ran into a problem. The drive may have errors, or a disk tool or antivirus driver misbehaved."),
            [CheckDrive, T("Check that the drive's cables are firmly connected."), RecentChange], "bug-check-0x24--ntfs-file-system"),
        new(0x3B, "SYSTEM_SERVICE_EXCEPTION", StopCodeArea.Driver,
            T("Something went wrong while Windows was doing work for a program. Often a graphics, antivirus or anti-cheat driver."),
            [UpdateDriver, GpuDriver, WindowsUpdate], "bug-check-0x3b--system-service-exception"),
        new(0x3D, "INTERRUPT_EXCEPTION_NOT_HANDLED", StopCodeArea.Driver,
            T("A driver failed while answering a device's signal."),
            [UpdateDriver, RecentChange], "bug-check-0x3d--interrupt-exception-not-handled"),
        new(0x3F, "NO_MORE_SYSTEM_PTES", StopCodeArea.Driver,
            T("Windows ran out of an internal memory resource, usually because a driver did not give it back."),
            [UpdateDriver, RecentChange], "bug-check-0x3f--no-more-system-ptes"),
        new(0x4E, "PFN_LIST_CORRUPT", StopCodeArea.Memory,
            T("Windows' list of memory pages was damaged, by a driver or by faulty RAM."),
            [MemoryTest, UpdateDriver, ResetOverclock], "bug-check-0x4e--pfn-list-corrupt"),
        new(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", StopCodeArea.Memory,
            T("Windows tried to read memory that should always be there and was not. Faulty RAM, a bad driver or antivirus are common causes."),
            [MemoryTest, UpdateDriver, ResetOverclock], "bug-check-0x50--page-fault-in-nonpaged-area"),
        new(0x7A, "KERNEL_DATA_INPAGE_ERROR", StopCodeArea.Storage,
            T("Windows could not read part of itself back from the drive. Check the drive, its cable and the page file."),
            [CheckDrive, T("Reseat or replace the drive's data cable (SATA) or reseat the SSD."), MemoryTest], "bug-check-0x7a--kernel-data-inpage-error"),
        new(0x7B, "INACCESSIBLE_BOOT_DEVICE", StopCodeArea.Storage,
            T("Windows lost access to the drive it starts from, often after a storage driver or BIOS setting (AHCI/RAID) changed."),
            [T("Undo recent BIOS changes to the storage mode (AHCI, RAID, VMD)."), CheckDrive, T("Use Startup Repair from the Windows recovery screen.")],
            "bug-check-0x7b--inaccessible-boot-device"),
        new(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", StopCodeArea.Driver,
            T("A driver running in the background hit an error it could not handle."),
            [UpdateDriver, RecentChange, WindowsUpdate], "bug-check-0x7e--system-thread-exception-not-handled"),
        new(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", StopCodeArea.Hardware,
            T("The processor reported an error Windows could not recover from. Unstable overclocks, overheating and faulty RAM are common causes."),
            [ResetOverclock, Temperatures, MemoryTest], "bug-check-0x7f--unexpected-kernel-mode-trap"),
        new(0x8E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", StopCodeArea.Driver,
            T("A driver or part of Windows hit an error it could not handle. Faulty RAM can also cause this."),
            [UpdateDriver, MemoryTest], "bug-check-0x8e--kernel-mode-exception-not-handled"),
        new(0x9C, "MACHINE_CHECK_EXCEPTION", StopCodeArea.Hardware,
            T("The processor reported a hardware error."),
            [ResetOverclock, Temperatures, Bios], "bug-check-0x9c--machine-check-exception"),
        new(0x9F, "DRIVER_POWER_STATE_FAILURE", StopCodeArea.Power,
            T("A driver did not respond in time while the PC was going to sleep, waking up or shutting down."),
            [UpdateDriver, T("Update chipset, network and graphics drivers; these are the usual suspects."), T("If it only happens on sleep, try turning off Fast startup.")],
            "bug-check-0x9f--driver-power-state-failure"),
        new(0xA0, "INTERNAL_POWER_ERROR", StopCodeArea.Power,
            T("Windows' power management hit a fatal error, often while going to sleep or hibernating."),
            [UpdateDriver, Bios, WindowsUpdate], "bug-check-0xa0--internal-power-error"),
        new(0xA5, "ACPI_BIOS_ERROR", StopCodeArea.Power,
            T("The BIOS gave Windows power settings it cannot use."),
            [Bios, T("Reset the BIOS to its default settings.")], "bug-check-0xa5--acpi-bios-error"),
        new(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", StopCodeArea.Driver,
            T("A driver tried to change memory that is read-only."),
            [UpdateDriver, RecentChange], "bug-check-0xbe--attempted-write-to-readonly-memory"),
        new(0xC2, "BAD_POOL_CALLER", StopCodeArea.Driver,
            T("A driver asked for memory in a way that is not allowed."),
            [UpdateDriver, RecentChange], "bug-check-0xc2--bad-pool-caller"),
        new(0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION", StopCodeArea.Driver,
            T("Driver Verifier (a Windows testing tool) caught a driver breaking the rules. Turn Driver Verifier off when you are done testing."),
            [UpdateDriver, T("Turn Driver Verifier off with \"verifier /reset\" in an administrator terminal, then restart.")],
            "bug-check-0xc4--driver-verifier-detected-violation"),
        new(0xC5, "DRIVER_CORRUPTED_EXPOOL", StopCodeArea.Driver,
            T("A driver damaged Windows' memory bookkeeping."),
            [UpdateDriver, RecentChange], "bug-check-0xc5--driver-corrupted-expool"),
        new(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", StopCodeArea.Driver,
            T("A driver used memory it should not have. Network, Wi-Fi, graphics and antivirus drivers are common causes."),
            [UpdateDriver, RecentChange, MemoryTest], "bug-check-0xd1--driver-irql-not-less-or-equal"),
        new(0xD5, "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", StopCodeArea.Driver,
            T("A driver used memory after giving it back."),
            [UpdateDriver, RecentChange], "bug-check-0xd5--driver-page-fault-in-freed-special-pool"),
        new(0xE2, "MANUALLY_INITIATED_CRASH", StopCodeArea.System,
            T("Someone started this crash on purpose (a keyboard shortcut or a debugging tool). Nothing is broken."),
            [T("Nothing to fix, unless you did not cause it; then check for keyboard or debugging tools set up to do this.")],
            "bug-check-0xe2--manually-initiated-crash"),
        new(0xEF, "CRITICAL_PROCESS_DIED", StopCodeArea.System,
            T("A part of Windows that must always run stopped. Damaged system files, a failing drive or security software can cause this."),
            [Sfc, CheckDrive, WindowsUpdate], "bug-check-0xef--critical-process-died"),
        new(0xF4, "CRITICAL_OBJECT_TERMINATION", StopCodeArea.Storage,
            T("A process Windows needs stopped unexpectedly, often because the drive could not be read."),
            [CheckDrive, Sfc], "bug-check-0xf4--critical-object-termination"),
        new(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", StopCodeArea.Driver,
            T("A driver tried to run code from memory that is only meant for data."),
            [UpdateDriver, RecentChange, MemoryTest], "bug-check-0xfc---attempted-execute-of-noexecute-memory"),
        new(0x101, "CLOCK_WATCHDOG_TIMEOUT", StopCodeArea.Hardware,
            T("One processor core stopped responding. Unstable overclocks and undervolts (PBO, Curve Optimizer) are the most common cause."),
            [ResetOverclock, Bios, Temperatures], "bug-check-0x101---clock-watchdog-timeout"),
        new(0x109, "CRITICAL_STRUCTURE_CORRUPTION", StopCodeArea.Driver,
            T("Windows found a protected part of itself changed, by a driver, faulty RAM or cheat or hacking tools."),
            [UpdateDriver, MemoryTest, RecentChange], "bug-check-0x109---critical-structure-corruption"),
        new(0x10E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", StopCodeArea.Graphics,
            T("The graphics driver lost track of the graphics card's memory."),
            [GpuDriver, ResetOverclock], "bug-check-0x10e---video-memory-management-internal"),
        new(0x113, "VIDEO_DXGKRNL_FATAL_ERROR", StopCodeArea.Graphics,
            T("The part of Windows that talks to the graphics driver hit a fatal error."),
            [GpuDriver, WindowsUpdate], "bug-check-0x113---video-dxgkrnl-fatal-error"),
        new(0x116, "VIDEO_TDR_FAILURE", StopCodeArea.Graphics,
            T("The graphics card stopped responding and Windows could not restart its driver. Driver problems, an unstable graphics overclock, heat or power delivery are the usual causes."),
            [GpuDriver, ResetOverclock, Temperatures, T("Make sure the graphics card's power cables are firmly connected.")], "bug-check-0x116---video-tdr-failure"),
        new(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", StopCodeArea.Graphics,
            T("The graphics card stopped responding for a moment and Windows restarted its driver. The screen goes black briefly."),
            [GpuDriver, ResetOverclock, Temperatures], "bug-check-0x117---video-tdr-timeout-detected"),
        new(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", StopCodeArea.Graphics,
            T("Windows' graphics scheduler found a serious problem, usually in the graphics driver."),
            [GpuDriver, T("If Hardware-accelerated GPU scheduling is on, try turning it off (Settings → Display → Graphics)."), WindowsUpdate],
            "bug-check-0x119---video-scheduler-internal-error"),
        new(0x124, "WHEA_UNCORRECTABLE_ERROR", StopCodeArea.Hardware,
            T("The hardware reported an error it could not fix. Unstable overclocks or undervolts, memory settings (XMP/EXPO), heat or a failing part are the usual causes."),
            [ResetOverclock, Temperatures, Bios, MemoryTest], "bug-check-0x124---whea-uncorrectable-error"),
        new(0x12B, "FAULTY_HARDWARE_CORRUPTED_PAGE", StopCodeArea.Memory,
            T("Windows found memory that changed by itself. This points at faulty RAM."),
            [MemoryTest, ResetOverclock], "bug-check-0x12b---faulty-hardware-corrupted-page"),
        new(0x133, "DPC_WATCHDOG_VIOLATION", StopCodeArea.Driver,
            T("A driver kept the processor busy for too long. Storage (SSD firmware, SATA/NVMe drivers) and network drivers are common causes."),
            [UpdateDriver, T("Update your SSD's firmware with the maker's tool."), T("Update chipset and storage drivers.")], "bug-check-0x133-dpc-watchdog-violation"),
        new(0x139, "KERNEL_SECURITY_CHECK_FAILURE", StopCodeArea.Driver,
            T("Windows found damaged data in a part of itself, usually caused by a driver, sometimes by faulty RAM."),
            [UpdateDriver, MemoryTest, Sfc], "bug-check-0x139--kernel-security-check-failure"),
        new(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", StopCodeArea.Driver,
            T("A driver damaged memory that Windows manages."),
            [UpdateDriver, MemoryTest], "bug-check-0x13a--kernel-mode-heap-corruption"),
        new(0x141, "VIDEO_ENGINE_TIMEOUT_DETECTED", StopCodeArea.Graphics,
            T("Part of the graphics card stopped responding and Windows restarted it. The screen may have gone black for a moment."),
            [GpuDriver, ResetOverclock, Temperatures], null),
        new(0x144, "BUGCODE_USB3_DRIVER", StopCodeArea.Driver,
            T("The USB 3 driver hit a fatal error, often because of a USB device or hub."),
            [T("Unplug USB devices you do not need and see whether it stops."), T("Update the chipset and USB controller drivers."), Bios],
            "bug-check-0x144--bugcode-usb3-driver"),
        new(0x154, "UNEXPECTED_STORE_EXCEPTION", StopCodeArea.Storage,
            T("Windows' memory compression store hit an error, often because of a drive problem or antivirus."),
            [CheckDrive, UpdateDriver], "bug-check-0x154--unexpected-store-exception"),
        new(0x1C7, "STORE_DATA_STRUCTURE_CORRUPTION", StopCodeArea.Storage,
            T("Windows' memory compression store was damaged, by a driver, the drive or RAM."),
            [CheckDrive, MemoryTest], "bug-check-0x1c7--store-data-structure-corruption"),
        new(0xC000021A, "WINLOGON_FATAL_ERROR", StopCodeArea.System,
            T("A part of Windows that handles sign-in stopped. Damaged system files or a bad update are common causes."),
            [Sfc, T("Uninstall the most recent update from the Windows Update page."), T("Use System Restore to go back to a restore point.")],
            "bug-check-0xc000021a--winlogin-fatal-error"),
    ];

    private static readonly Dictionary<uint, StopCodeInfo> ByCode = All.ToDictionary(s => s.Code);

    /// <summary>General first steps for a stop code that is not in the catalog.</summary>
    public static IReadOnlyList<string> GeneralSteps { get; } = [RecentChange, UpdateDriver, WindowsUpdate, MemoryTest];

    /// <summary>
    /// The entry for <paramref name="code"/>, or null. The "_M" variants (0x1000007E = 0x7E with a flag bit) map to the
    /// base code.
    /// </summary>
    public static StopCodeInfo? Find(uint code) =>
        ByCode.TryGetValue(code, out var info) ? info
        : (code & 0xF0000000) == 0x10000000 && ByCode.TryGetValue(code & 0x0FFFFFFF, out var baseInfo) ? baseInfo
        : null;

    /// <summary>"VIDEO_TDR_FAILURE" or "Stop code 0x000001E0" when unknown.</summary>
    public static string NameOf(uint code, Localization.Translator? tr = null) =>
        Find(code)?.Name ?? (tr ?? Localization.Translator.English).F("Stop code {0}", BugCheckFormat.Code(code));

    /// <summary>The docs page for a slug, or the reference index.</summary>
    public static string DocsUrl(string? slug) => string.IsNullOrWhiteSpace(slug) ? ReferenceIndexUrl : DocsBase + slug;

    /// <summary>Microsoft's page for <paramref name="code"/>, or the reference index.</summary>
    public static string DocsUrlFor(uint code) => DocsUrl(Find(code)?.DocsSlug);
}
