using System.Buffers.Binary;
using System.Text;
using PcChecker.Core.Crashes;
using PcChecker.Core.Crashes.Dumps;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Kernel dump (PAGEDU64 / PAGEDUMP), small triage dump and user minidump (MDMP) parsing from crafted bytes.</summary>
public class CrashDumpTests
{
    private const ulong NvBase = 0xFFFFF8061C200000;
    private const ulong KernelBase = 0xFFFFF80612400000;
    private const ulong NetBase = 0xFFFFF80616A00000;
    private static readonly DateTimeOffset CrashUtc = new(2026, 10, 20, 21, 14, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ crafted files

    private static void U16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);

    private static void U32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);

    private static void U64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);

    private static void Ascii(byte[] b, int o, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, o);

    /// <summary>A 64-bit kernel dump header; a triage block with three drivers and a small stack when <paramref name="stride"/> is set.</summary>
    internal static byte[] KernelDump(uint code, ulong[] parameters, uint dumpType = 4, int stride = 0x88, ulong[]? stack = null, ulong exceptionAddress = 0)
    {
        var b = new byte[0x3000];
        Ascii(b, 0, "PAGE");
        Ascii(b, 4, "DU64");
        U32(b, 0x08, 0xF);
        U32(b, 0x0C, 26100);
        U32(b, 0x30, 0x8664);
        U32(b, 0x34, 16);
        U32(b, 0x38, code);
        for (int i = 0; i < parameters.Length; i++)
        {
            U64(b, 0x40 + (i * 8), parameters[i]);
        }

        U64(b, 0xF10, exceptionAddress);
        U32(b, 0xF98, dumpType);
        U64(b, 0xFA8, (ulong)CrashUtc.ToFileTime());

        if (dumpType == 4)
        {
            const int triage = 0x2000;
            U32(b, triage + 0x28, 0x2800);
            U32(b, triage + 0x2C, (uint)((stack?.Length ?? 0) * 8));
            U32(b, triage + 0x30, 0x2100);
            U32(b, triage + 0x34, 3);
            (string Name, ulong Base, uint Size)[] drivers =
            [
                ("ntoskrnl.exe", KernelBase, 0x1047000),
                ("nvlddmkm.sys", NvBase, 0x5A40000),
                ("e2f68.sys", NetBase, 0x9B000),
            ];
            for (int i = 0; i < drivers.Length; i++)
            {
                int entry = 0x2100 + (i * stride);
                int name = 0x2600 + (i * 0x40);
                U32(b, entry, (uint)name);
                U64(b, entry + 8 + 0x30, drivers[i].Base);
                U32(b, entry + 8 + 0x40, drivers[i].Size);
                U32(b, name, (uint)drivers[i].Name.Length);
                Encoding.Unicode.GetBytes(drivers[i].Name).CopyTo(b, name + 4);
            }

            for (int i = 0; i < (stack?.Length ?? 0); i++)
            {
                U64(b, 0x2800 + (i * 8), stack![i]);
            }
        }

        return b;
    }

    /// <summary>A user-mode minidump with SystemInfo, Exception and ModuleList streams.</summary>
    internal static byte[] UserMinidump()
    {
        var b = new byte[0x700];
        Ascii(b, 0, "MDMP");
        U32(b, 4, 0xA793);
        U32(b, 8, 3);
        U32(b, 0x0C, 0x20);
        U32(b, 0x14, (uint)CrashUtc.ToUnixTimeSeconds());
        // Directory: type, size, rva.
        U32(b, 0x20, 7); U32(b, 0x24, 0x38); U32(b, 0x28, 0x100);
        U32(b, 0x2C, 6); U32(b, 0x30, 0xA8); U32(b, 0x34, 0x200);
        U32(b, 0x38, 4); U32(b, 0x3C, 4 + (2 * 108)); U32(b, 0x40, 0x300);
        // SystemInfo: x64, Windows 10.0.22631.
        U16(b, 0x100, 9);
        U32(b, 0x108, 10);
        U32(b, 0x10C, 0);
        U32(b, 0x110, 22631);
        // Exception: access violation inside the second module.
        U32(b, 0x208, 0xC0000005);
        U64(b, 0x218, 0x7FFB12345678);
        // Module list.
        U32(b, 0x300, 2);
        WriteModule(b, 0x304, 0x7FF6A0000000, 0x5000000, 0x500, @"D:\Games\Game.exe");
        WriteModule(b, 0x304 + 108, 0x7FFB12000000, 0x1200000, 0x580, @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64\nvwgf2umx.dll");
        return b;
    }

    private static void WriteModule(byte[] b, int at, ulong baseAddress, uint size, int nameRva, string name)
    {
        U64(b, at, baseAddress);
        U32(b, at + 8, size);
        U32(b, at + 0x14, (uint)nameRva);
        var bytes = Encoding.Unicode.GetBytes(name);
        U32(b, nameRva, (uint)bytes.Length);
        bytes.CopyTo(b, nameRva + 4);
    }

    // ------------------------------------------------------------------ kernel dumps

    [Fact]
    public void A_kernel_dump_header_gives_code_parameters_build_and_crash_time()
    {
        var info = KernelDumpReader.Read(KernelDump(0x116, [0xFFFFB30A4C7E8010, 0xFFFFF8061C9D3A40, 0xFFFFFFFFC000009A, 4], dumpType: 2));

        Assert.NotNull(info);
        Assert.Equal(DumpFormat.Kernel64, info!.Format);
        Assert.Equal(0x116u, info.BugCheckCode);
        Assert.Equal([0xFFFFB30A4C7E8010, 0xFFFFF8061C9D3A40, 0xFFFFFFFFC000009A, 4UL], info.Parameters);
        Assert.Equal(26100, info.OsBuild);
        Assert.Equal("x64", info.Architecture);
        Assert.Equal("Kernel memory dump", info.KindText);
        Assert.Equal(CrashUtc, info.CrashTime!.Value.ToUniversalTime());
        Assert.Empty(info.Modules); // Only small dumps carry the driver list.
        Assert.False(info.Truncated);
    }

    [Theory]
    [InlineData(0x88)]
    [InlineData(0xA0)]
    [InlineData(0xB8)]
    public void A_small_dump_lists_its_drivers_whatever_the_entry_size(int stride)
    {
        var info = DumpFileReader.Parse(KernelDump(0xD1, [0x10, 2, 0, NetBase + 0x1234], stride: stride));

        Assert.NotNull(info);
        Assert.Equal("Small memory dump", info!.KindText);
        Assert.Equal(["ntoskrnl.exe", "nvlddmkm.sys", "e2f68.sys"], info.Modules.Select(m => m.Name));
        Assert.Equal(NvBase, info.Modules[1].Base);
        Assert.Equal(0x5A40000u, info.Modules[1].Size);
    }

    [Fact]
    public void A_small_dump_reads_the_raw_stack()
    {
        var info = KernelDumpReader.Read(KernelDump(0x7E, [0xC0000005, 0, 0, 0], stack: [0x1, KernelBase + 0x40, NvBase + 0x99]))!;
        Assert.Equal([0x1UL, KernelBase + 0x40, NvBase + 0x99], info.StackValues);
    }

    [Fact]
    public void Too_short_or_foreign_files_are_not_dumps()
    {
        var full = KernelDump(0x50, [1, 2, 3, 4]);
        Assert.Null(KernelDumpReader.Read(full.AsSpan(0, 0x50)));      // Parameters cut off.
        Assert.Null(KernelDumpReader.Read(full.AsSpan(0, 6)));
        Assert.Null(DumpFileReader.Parse([]));
        Assert.Null(DumpFileReader.Parse(Encoding.ASCII.GetBytes("MZ this is an exe, not a dump")));
        Assert.Null(KernelDumpReader.Read(Encoding.ASCII.GetBytes("PAGEXXXX" + new string('\0', 200))));
    }

    [Fact]
    public void A_truncated_dump_keeps_what_it_could_read()
    {
        var full = KernelDump(0x116, [0xA, NvBase + 0x10, 0, 0], stack: [NvBase + 1]);

        var headerOnly = KernelDumpReader.Read(full.AsSpan(0, 0xFA0))!;
        Assert.Equal(0x116u, headerOnly.BugCheckCode);
        Assert.True(headerOnly.Truncated);
        Assert.Null(headerOnly.CrashTime); // 0xFA8 lies beyond the cut.

        // Cut inside the driver list: fewer drivers, no exception.
        var partial = KernelDumpReader.Read(full.AsSpan(0, 0x2100 + 0x88 + 0x20))!;
        Assert.True(partial.Truncated);
        Assert.True(partial.Modules.Count < 3);
    }

    [Fact]
    public void Every_prefix_of_a_dump_parses_without_throwing()
    {
        var kernel = KernelDump(0x124, [0, 0xFFFFC1027A6E2028, 0xBE000000, 0x800400], stack: [NvBase + 5, 7]);
        for (int length = 0; length <= kernel.Length; length += 7)
        {
            _ = DumpFileReader.Parse(kernel.AsSpan(0, length));
        }

        var user = UserMinidump();
        for (int length = 0; length <= user.Length; length++)
        {
            _ = DumpFileReader.Parse(user.AsSpan(0, length));
        }
    }

    [Fact]
    public void Garbage_counts_and_offsets_are_bounded()
    {
        var b = KernelDump(0x1A, [0x41790, 0, 0, 0]);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2000 + 0x34), 0xFFFFFFFF); // driver count
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2000 + 0x28), 0xFFFFFF00); // stack offset
        var info = KernelDumpReader.Read(b)!;
        Assert.Empty(info.Modules);
        Assert.Empty(info.StackValues);

        var m = UserMinidump();
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), 0x7FFFFFFF); // stream count
        var user = MinidumpReader.Read(m)!;
        Assert.True(user.Truncated);
    }

    [Fact]
    public void A_32_bit_kernel_dump_header_is_read()
    {
        var b = new byte[0x1000];
        Encoding.ASCII.GetBytes("PAGEDUMP").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x0C), 19045);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x20), 0x14C);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x28), 0x0A);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2C), 0x8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x30), 2);

        var info = KernelDumpReader.Read(b)!;
        Assert.Equal(DumpFormat.Kernel32, info.Format);
        Assert.Equal(0x0Au, info.BugCheckCode);
        Assert.Equal([8UL, 2UL, 0UL, 0UL], info.Parameters);
        Assert.Equal(19045, info.OsBuild);
        Assert.Equal("x86", info.Architecture);
    }

    // ------------------------------------------------------------------ user minidumps

    [Fact]
    public void A_user_minidump_gives_build_exception_and_modules()
    {
        var info = DumpFileReader.Parse(UserMinidump())!;

        Assert.Equal(DumpFormat.UserMinidump, info.Format);
        Assert.Equal(22631, info.OsBuild);
        Assert.Equal("x64", info.Architecture);
        Assert.Equal(0xC0000005u, info.ExceptionCode);
        Assert.Equal(0x7FFB12345678UL, info.ExceptionAddress);
        Assert.Equal(["Game.exe", "nvwgf2umx.dll"], info.Modules.Select(m => m.Name));
        Assert.Equal("nvwgf2umx.dll", CrashBlameRules.FindModule(info.Modules, info.ExceptionAddress!.Value)?.Name);
        Assert.Equal(CrashUtc, info.CrashTime!.Value.ToUniversalTime());
        Assert.False(info.Truncated);
    }

    [Fact]
    public void A_truncated_user_minidump_reports_it()
    {
        var info = MinidumpReader.Read(UserMinidump().AsSpan(0, 0x400))!;
        Assert.True(info.Truncated);
        Assert.Equal(22631, info.OsBuild);
        Assert.Empty(info.Modules); // The module list stream runs past the end.
    }

    // ------------------------------------------------------------------ "probably caused by"

    private static readonly CrashModule[] Modules =
    [
        new("ntoskrnl.exe", KernelBase, 0x1047000),
        new("dxgkrnl.sys", 0xFFFFF80619A00000, 0x3C5000),
        new("nvlddmkm.sys", NvBase, 0x5A40000),
        new("e2f68.sys", NetBase, 0x9B000),
    ];

    [Fact]
    public void A_parameter_inside_a_third_party_driver_blames_it()
    {
        var blame = CrashBlameRules.Guess(0x116, [0xFFFFB30A4C7E8010, NvBase + 0x7D3A40, 0xFFFFFFFFC000009A, 4], null, Modules, [])!;
        Assert.Equal("nvlddmkm.sys", blame.Module);
        Assert.Equal(BlameConfidence.Medium, blame.Confidence);
        Assert.False(blame.IsWindowsModule);
        Assert.Contains("parameter 2", blame.Reason);
    }

    [Fact]
    public void A_driver_outside_Windows_wins_over_a_Windows_module()
    {
        var blame = CrashBlameRules.Guess(0xD1, [KernelBase + 0x10, 2, 0, NetBase + 0x44], null, Modules, [])!;
        Assert.Equal("e2f68.sys", blame.Module);

        var atCrash = CrashBlameRules.Guess(0x3B, [0xC0000005, 0, 0, 0], NetBase + 1, Modules, [])!;
        Assert.Equal("e2f68.sys", atCrash.Module);
        Assert.Contains("crash happened inside", atCrash.Reason);
    }

    [Fact]
    public void The_stack_scan_finds_the_first_driver_from_outside_Windows()
    {
        var blame = CrashBlameRules.Guess(0x7E, [0xC0000005, 0, 0, 0], null, Modules, [5, KernelBase + 8, 0xFFFFF80619A00010, NetBase + 0x20, NvBase + 8])!;
        Assert.Equal("e2f68.sys", blame.Module);
        Assert.Equal(BlameConfidence.Low, blame.Confidence);
    }

    [Fact]
    public void Only_Windows_pointed_at_says_so_and_graphics_codes_fall_back_to_the_graphics_driver()
    {
        var windows = CrashBlameRules.Guess(0x1A, [0x41790, KernelBase + 0x99, 0, 0], null, Modules, [])!;
        Assert.Equal("ntoskrnl.exe", windows.Module);
        Assert.True(windows.IsWindowsModule);

        var gpu = CrashBlameRules.Guess(0x119, [1, 2, 3, 4], null, Modules, [])!;
        Assert.Equal("nvlddmkm.sys", gpu.Module);
        Assert.Equal(BlameConfidence.Low, gpu.Confidence);

        Assert.Null(CrashBlameRules.Guess(0x1A, [1, 2, 3, 4], null, Modules, []));
        Assert.Null(CrashBlameRules.Guess(0x116, [NvBase + 1], null, [], []));
    }

    [Fact]
    public void Module_ranges_are_half_open_and_small_values_never_match()
    {
        Assert.Equal("nvlddmkm.sys", CrashBlameRules.FindModule(Modules, NvBase)?.Name);
        Assert.Null(CrashBlameRules.FindModule(Modules, NvBase + 0x5A40000));
        Assert.Null(CrashBlameRules.FindModule([new CrashModule("low.sys", 0, 0x20000)], 0x100));
        Assert.True(CrashBlameRules.IsWindowsModule(@"C:\Windows\System32\ntdll.dll"));
        Assert.False(CrashBlameRules.IsWindowsModule("nvlddmkm.sys"));
        Assert.True(CrashBlameRules.IsGraphicsDriver("amdkmdag.sys"));
    }

    [Fact]
    public void A_triage_dump_end_to_end_blames_the_driver_its_parameter_points_into()
    {
        var info = DumpFileReader.Parse(KernelDump(0x116, [0xFFFFB30A4C7E8010, NvBase + 0x7D3A40, 0xFFFFFFFFC000009A, 4], stride: 0xA0))!;
        var blame = CrashBlameRules.Guess(info.BugCheckCode, info.Parameters, info.ExceptionAddress, info.Modules, info.StackValues);
        Assert.Equal("nvlddmkm.sys", blame?.Module);
    }
}
