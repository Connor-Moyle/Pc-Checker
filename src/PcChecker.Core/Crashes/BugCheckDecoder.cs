using System.Globalization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>One MEMORY_MANAGEMENT (0x1A) sub-code: what it means and whether it usually points at hardware.</summary>
public sealed record MemoryManagementSubcode(uint Code, string Meaning, bool UsuallyHardware);

/// <summary>
/// "What the details say": turns the four stop-code parameters into plain sentences for the codes where Microsoft
/// documents what they mean (0x0A/0xD1 address, IRQL and operation; 0x50 operation and fault type; 0x1A sub-codes;
/// 0x124 error source and machine-check status; 0x9F, 0x133, 0x119 and 0x139 sub-types; 0x3B/0x1E/0x7E exception
/// codes; 0x116/0x117 the driver; 0x7A storage status; 0x101 the core; 0xEF process or thread). Addresses are mapped
/// to the driver whose memory range contains them when the dump has a driver list. Pure.
/// </summary>
public static class BugCheckDecoder
{
    /// <summary>MEMORY_MANAGEMENT parameter 1 values from Microsoft's bug check reference, in plain words.</summary>
    public static IReadOnlyList<MemoryManagementSubcode> MemoryManagementSubcodes { get; } =
    [
        new(0x31, T("A driver or program file in memory was damaged. This is probably a hardware error."), true),
        new(0x3F, T("Data read back from the page file did not match its checksum: the drive, its cable or the RAM changed it."), true),
        new(0x403, T("Two of Windows' memory tables disagree. This is probably a hardware (RAM) error, especially when the values differ by one bit."), true),
        new(0x404, T("Windows found its memory tables out of step while freeing a page."), false),
        new(0x411, T("A memory page table entry was damaged."), true),
        new(0x1010, T("A driver unlocked memory that was not locked."), false),
        new(0x1233, T("A driver mapped memory it had not locked first. This is a driver bug."), false),
        new(0x1234, T("A driver tried to lock memory that does not exist."), false),
        new(0x1235, T("A driver tried to protect memory with a bad mapping."), false),
        new(0x1236, T("A driver described memory with a page that is not locked."), false),
        new(0x1240, T("A driver described memory that is not loaded."), false),
        new(0x3300, T("Memory was wrongly marked as copy-on-write while being written."), false),
        new(0x3451, T("The saved stack of a background thread was damaged."), true),
        new(0x3453, T("Windows could not clean up a closed program's memory tables because something still used them."), false),
        new(0x3470, T("A cached kernel stack was damaged."), true),
        new(0x4477, T("A driver wrote to memory nobody had asked for."), false),
        new(0x5003, T("Windows' list of free memory was damaged. This is probably a hardware error."), true),
        new(0x5100, T("Windows' map of used memory was damaged."), true),
        new(0x5200, T("A free block of memory was changed after it was freed: a driver bug or faulty RAM."), false),
        new(0x5305, T("A driver tried to free memory with a bad address."), false),
        new(0x6001, T("The memory compression store's private memory was damaged."), true),
        new(0x8884, T("Two pages on the standby list had different priorities when they should match."), true),
        new(0x8885, T("Two pages on the standby list had different priorities when they should match."), true),
        new(0x8886, T("Two pages on the standby list had different priorities when they should match."), true),
        new(0x8887, T("Two pages on the standby list had different priorities when they should match."), true),
        new(0x8888, T("Windows' internal memory structures were damaged."), true),
        new(0x8889, T("Windows' internal memory structures were damaged."), true),
        new(0x888A, T("Windows' memory page tables were damaged."), true),
        new(0x9696, T("A memory page lost the link to the program it belongs to."), true),
        new(0x15000, T("A driver tried to release protection on memory in the wrong program."), false),
        new(0x15001, T("A driver failed to release protection on memory."), false),
        new(0x41201, T("Two of Windows' memory tables disagree while checking an address."), true),
        new(0x41202, T("A memory page table entry was damaged."), true),
        new(0x41283, T("The working set index in a page table entry was damaged."), true),
        new(0x41284, T("A page table entry or a program's working set list was damaged."), true),
        new(0x41286, T("A driver tried to free memory with a bad address."), false),
        new(0x41287, T("A page fault happened while Windows held a lock it must not fault under."), false),
        new(0x41785, T("A program's working set list was damaged."), true),
        new(0x41790, T("A page table page was damaged: Windows counted a different number of used entries than it expected. Very often faulty or unstable RAM."), true),
        new(0x41792, T("A damaged page table entry was found."), true),
        new(0x41793, T("A page table page was damaged."), true),
        new(0x61940, T("A page directory entry was unexpectedly removed."), true),
        new(0x61941, T("The memory paging tables were damaged when an address was used. Often faulty or unstable RAM, sometimes a driver."), true),
        new(0x61946, T("A driver described memory in a way that is not allowed (usually the driver is at fault)."), false),
        new(0x61948, T("A driver unlocked device memory that was not locked."), false),
        new(0x61949, T("A driver unlocked device memory that was not locked."), false),
        new(0x6194A, T("A driver released device memory that was not in use."), false),
        new(0x3030308, T("Memory being removed was still used by the boot loader."), false),
    ];

    private static readonly Dictionary<uint, MemoryManagementSubcode> SubcodeByValue = MemoryManagementSubcodes.ToDictionary(s => s.Code);

    /// <summary>The MEMORY_MANAGEMENT sub-code, or null when it is not in the table.</summary>
    public static MemoryManagementSubcode? FindMemorySubcode(ulong value) =>
        value <= uint.MaxValue && SubcodeByValue.TryGetValue((uint)value, out var s) ? s : null;

    /// <summary>The plain lines for a stop code and its parameters (empty when there is nothing documented to say).</summary>
    public static IReadOnlyList<CrashLine> Decode(uint code, IReadOnlyList<ulong> parameters, IReadOnlyList<CrashModule>? modules = null, WheaErrorInfo? whea = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        modules ??= [];
        var lines = new List<CrashLine>();
        ulong P(int n) => n >= 1 && n <= parameters.Count ? parameters[n - 1] : 0;
        bool Has(int n) => n >= 1 && n <= parameters.Count;
        uint baseCode = (code & 0xF0000000) == 0x10000000 ? code & 0x0FFFFFFF : code;

        switch (baseCode)
        {
            case 0x0A:
            case 0xD1:
                if (!Has(4))
                {
                    break;
                }

                lines.Add(new CrashLine(T("The driver tried to {0} memory address {1} at a moment it was not allowed to (IRQL {2})."),
                    new LocalizedText(Operation(P(3), arm: false, irqlStyle: true)), Hex(P(1)), new LocalizedText(Irql(P(2)))));
                if (P(1) < 0x10000)
                {
                    lines.Add(CrashLine.Of(T("The address is almost zero: the driver used an empty (null) pointer, a classic driver bug.")));
                }

                AddWhere(lines, P(4), modules, T("The instruction that did it is in {0}."));
                break;

            case 0x50:
                if (!Has(4))
                {
                    break;
                }

                lines.Add(new CrashLine(T("Windows tried to {0} memory address {1}, which was not there."), new LocalizedText(Operation(P(2), arm: false, irqlStyle: false)), Hex(P(1))));
                AddWhere(lines, P(3), modules, T("The instruction that did it is in {0}."));
                if (PageFaultType(P(4)) is { } type)
                {
                    lines.Add(new CrashLine(T("Kind of fault: {0}."), new LocalizedText(type)));
                }

                break;

            case 0x1A:
                if (!Has(1))
                {
                    break;
                }

                if (FindMemorySubcode(P(1)) is { } sub)
                {
                    lines.Add(new CrashLine(T("Sub-code {0}: {1}"), Hex32(P(1)), new LocalizedText(sub.Meaning)));
                    lines.Add(CrashLine.Of(sub.UsuallyHardware
                        ? T("Microsoft's notes point at hardware for this sub-code: test the RAM and undo memory overclocks (XMP/EXPO) first.")
                        : T("This sub-code usually points at a driver rather than the RAM.")));
                    if (P(1) == 0x403 && IsSingleBitDifference(P(3), P(4)))
                    {
                        lines.Add(CrashLine.Of(T("The two values differ by exactly one bit, the typical sign of a memory (RAM) error.")));
                    }
                }
                else
                {
                    lines.Add(new CrashLine(T("Sub-code {0} is not one Microsoft documents in detail."), Hex32(P(1))));
                }

                break;

            case 0x124:
            {
                var source = WheaRecord.SourceOf(P(1));
                var info = whea ?? new WheaErrorInfo { Source = source, Fatal = true };
                if (info.Source == WheaErrorSource.Unknown && Has(1))
                {
                    info = info with { Source = source };
                }

                if (info.MciStatus is null && source == WheaErrorSource.MachineCheck && Has(4))
                {
                    info = info with { MciStatus = (P(3) << 32) | (P(4) & 0xFFFFFFFF) };
                }

                lines.AddRange(WheaRecord.Describe(info));
                if (whea is null)
                {
                    lines.Add(CrashLine.Of(T("The full error record is in kernel memory; the WHEA-Logger event written at the next start usually names the processor core.")));
                }

                break;
            }

            case 0x9F:
                if (!Has(1))
                {
                    break;
                }

                lines.Add(new CrashLine(T("Type {0}: {1}"), Hex32(P(1)), new LocalizedText(PowerFailureType(P(1)))));
                break;

            case 0x133:
                if (!Has(1))
                {
                    break;
                }

                lines.Add(P(1) == 0
                    ? new CrashLine(T("A single driver routine ran too long ({0} of the {1} ticks allowed). The driver on the stack is usually the one."), Dec(P(2)), Dec(P(3)))
                    : CrashLine.Of(T("The processors spent too long in driver code in total (not one single routine). Storage, network and graphics drivers are the usual suspects.")));
                break;

            case 0x101:
                if (Has(4))
                {
                    lines.Add(new CrashLine(T("Logical processor {0} stopped answering for {1} clock ticks. When it is always the same core, that core's overclock or Curve Optimizer value is too aggressive."),
                        Dec(P(4)), Dec(P(1))));
                }

                break;

            case 0x116:
            case 0x117:
            case 0x141:
                AddWhere(lines, P(2), modules, T("The graphics driver that stopped responding is {0}."));
                if (Has(3) && P(3) != 0 && P(3) <= uint.MaxValue)
                {
                    lines.Add(new CrashLine(T("Last error: {0}."), DescribeStatus((uint)P(3))));
                }

                break;

            case 0x119:
                if (Has(1))
                {
                    lines.Add(new CrashLine(T("Type {0}: {1}"), Hex32(P(1)), new LocalizedText(SchedulerType(P(1)))));
                }

                break;

            case 0x3B:
            case 0x1E:
            case 0x7E:
            case 0x8E:
                if (!Has(2))
                {
                    break;
                }

                if (P(1) <= uint.MaxValue && P(1) != 0)
                {
                    lines.Add(new CrashLine(T("The error was: {0}."), DescribeStatus((uint)P(1))));
                }

                AddWhere(lines, P(2), modules, T("It happened in {0}."));
                if (P(1) == 0x80000003)
                {
                    lines.Add(CrashLine.Of(T("A breakpoint in kernel code usually means a driver built for testing, or a debugger setting left on.")));
                }

                break;

            case 0x139:
                if (Has(1))
                {
                    lines.Add(new CrashLine(T("Type {0}: {1}"), Dec(P(1)), new LocalizedText(SecurityCheckType(P(1)))));
                }

                break;

            case 0x7A:
            case 0x77:
            {
                ulong status = baseCode == 0x7A ? P(2) : P(1);
                if (status is >= 0xC0000000 and <= 0xFFFFFFFF)
                {
                    lines.Add(new CrashLine(T("The drive answered: {0}."), DescribeStatus((uint)status)));
                }

                break;
            }

            case 0xEF:
                if (Has(2))
                {
                    lines.Add(CrashLine.Of(P(2) == 1
                        ? T("A thread of a critical Windows process ended. The process name is only in the memory dump; WinDbg's !analyze shows it.")
                        : T("A critical Windows process ended (for example csrss, wininit, smss or services). Its name is only in the memory dump; WinDbg's !analyze shows it.")));
                }

                break;

            case 0xC2:
            case 0x19:
                if (Has(1))
                {
                    lines.Add(new CrashLine(T("Pool problem type {0}. The driver that asked for or freed this memory is the usual cause."), Hex32(P(1))));
                }

                break;

            case 0x7F:
                if (Has(1))
                {
                    lines.Add(new CrashLine(T("Processor trap {0}: {1}"), Hex32(P(1)), new LocalizedText(TrapName(P(1)))));
                }

                break;
        }

        return lines;
    }

    /// <summary>The driver an address falls in, as "nvlddmkm.sys (+0x1d3a40)", or null.</summary>
    public static string? WhereIs(ulong address, IReadOnlyList<CrashModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return CrashBlameRules.FindModule(modules, address) is { } m
            ? string.Create(CultureInfo.InvariantCulture, $"{m.Name} (+0x{address - m.Base:x})")
            : null;
    }

    private static void AddWhere(List<CrashLine> lines, ulong address, IReadOnlyList<CrashModule> modules, string format)
    {
        if (WhereIs(address, modules) is { } where)
        {
            lines.Add(new CrashLine(format, where));
        }
    }

    /// <summary>"Access violation (0xc0000005)" or the code.</summary>
    private static string DescribeStatus(uint status) => ExceptionCodeCatalog.Describe(status);

    /// <summary>The kind of access in 0x0A/0xD1 parameter 3 (bit 0 write, bit 3 execute) or 0x50 parameter 2 (0, 1/2, 10).</summary>
    public static string Operation(ulong value, bool arm, bool irqlStyle)
    {
        if (irqlStyle)
        {
            return (value & 0x8) != 0 ? T("run code at") : (value & 0x1) != 0 ? T("write to") : T("read from");
        }

        return value switch
        {
            0 => T("read from"),
            1 or 2 => T("write to"),
            8 or 10 => T("run code at"),
            _ => T("use"),
        };
    }

    /// <summary>An IRQL value in plain words.</summary>
    public static string Irql(ulong value) => value switch
    {
        0 => T("0, normal"),
        1 => T("1, APC level"),
        2 => T("2, dispatch level: the driver may not touch memory that can be paged out"),
        _ => T("above 2, while handling a device interrupt"),
    };

    private static string? PageFaultType(ulong value) => value switch
    {
        0x0 => T("the address is on a page Windows had already freed"),
        0x2 => T("the address has no valid page table entry"),
        0x3 => T("a session address was used from the wrong process"),
        0x4 => T("the address is not a valid (canonical) address at all"),
        0xF => T("kernel code touched a program's memory where that is not allowed"),
        _ => null,
    };

    private static string PowerFailureType(ulong value) => value switch
    {
        0x1 => T("a device being removed still had an unfinished power request."),
        0x2 => T("a driver finished a power request without telling Windows it was done."),
        0x3 => T("a driver held a power request for too long while the PC was going to sleep, waking up or shutting down."),
        0x4 => T("the power change timed out waiting for Plug and Play (a device was being added or removed)."),
        0x5 => T("a device did not finish a power change within the time allowed."),
        0x6 => T("a device did not complete its power change successfully."),
        0x500 => T("a driver finished a power request without telling Windows it was done."),
        _ => T("a power request was not handled correctly."),
    };

    private static string SchedulerType(ulong value) => value switch
    {
        0x1 => T("the graphics driver reported an invalid fence (a sync marker)."),
        0x2 => T("the graphics driver failed to submit work to the card."),
        0x3 => T("the graphics driver failed to patch a command buffer."),
        0x5 => T("the graphics driver failed a system or paging command."),
        0x9 => T("the graphics driver failed to cancel a command."),
        0xC => T("the graphics card reported a fatal hardware fault."),
        0x10 => T("the graphics card raised an interrupt right after it was reset."),
        _ => T("the graphics scheduler found the driver breaking its rules."),
    };

    private static string SecurityCheckType(ulong value) => value switch
    {
        0 or 2 => T("a driver overran a buffer on its stack."),
        1 => T("a damaged C++ object was used."),
        3 => T("a linked list in kernel memory was damaged (often a driver, sometimes faulty RAM)."),
        5 => T("a function was given a bad value it treats as fatal."),
        6 => T("a driver's security cookie was not set up (a driver made for another Windows version)."),
        7 => T("a fatal exit was requested."),
        8 => T("an array was read outside its bounds."),
        10 => T("an indirect call jumped somewhere it was not allowed (Control Flow Guard)."),
        11 => T("a write went somewhere it was not allowed."),
        14 => T("an object's reference count was wrong (used after it was freed)."),
        28 => T("a buffer was read outside its bounds."),
        29 => T("a tree structure in kernel memory was damaged."),
        _ => T("a security check inside Windows failed."),
    };

    private static string TrapName(ulong value) => value switch
    {
        0x0 => T("divide error."),
        0x4 => T("overflow."),
        0x5 => T("bound check failed."),
        0x6 => T("invalid instruction (often an unstable overclock)."),
        0x8 => T("double fault: an error while handling another error (often hardware, or a stack overflow in a driver)."),
        0xD => T("general protection fault."),
        0xE => T("page fault."),
        0x12 => T("machine check (hardware)."),
        _ => T("other processor trap."),
    };

    private static bool IsSingleBitDifference(ulong a, ulong b)
    {
        ulong x = a ^ b;
        return x != 0 && (x & (x - 1)) == 0;
    }

    private static string Hex(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);

    private static string Hex32(ulong value) => "0x" + value.ToString(value <= uint.MaxValue ? "X" : "X16", CultureInfo.InvariantCulture);

    private static string Dec(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
