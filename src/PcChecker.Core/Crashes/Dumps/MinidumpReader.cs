namespace PcChecker.Core.Crashes.Dumps;

/// <summary>
/// Reads a user-mode minidump ("MDMP", MINIDUMP_HEADER) as written by Windows Error Reporting for a crashing program:
/// the stream directory, then the SystemInfo (Windows build), Exception (code and address) and ModuleList (every loaded
/// file with its address range) streams. Every location from the file is bounds-checked.
/// </summary>
public static class MinidumpReader
{
    private const uint ModuleListStream = 4;
    private const uint ExceptionStream = 6;
    private const uint SystemInfoStream = 7;
    private const int DirectoryEntrySize = 12;
    private const int ModuleEntrySize = 108;
    private const int MaxStreams = 4096;
    private const int MaxModules = 8192;

    public static bool IsMinidump(ReadOnlySpan<byte> data) => new DumpBytes(data).Matches(0, "MDMP");

    /// <summary>Parses the minidump; null when the signature or the header is missing.</summary>
    public static CrashDumpInfo? Read(ReadOnlySpan<byte> data)
    {
        var b = new DumpBytes(data);
        if (!b.Matches(0, "MDMP") || b.U32(8) is not { } streams || b.U32(0x0C) is not { } directory)
        {
            return null;
        }

        bool truncated = false;
        int? build = null;
        string? arch = null;
        uint? exceptionCode = null;
        ulong? exceptionAddress = null;
        var modules = new List<CrashModule>();

        if (streams > MaxStreams || !b.Has(directory, (long)streams * DirectoryEntrySize))
        {
            truncated = true;
            streams = (uint)Math.Clamp((b.Length - (long)directory) / DirectoryEntrySize, 0, MaxStreams);
        }

        for (long i = 0; i < streams; i++)
        {
            long entry = directory + (i * DirectoryEntrySize);
            uint type = b.U32(entry) ?? 0;
            uint size = b.U32(entry + 4) ?? 0;
            uint rva = b.U32(entry + 8) ?? 0;
            if (!b.Has(rva, size))
            {
                truncated |= type is SystemInfoStream or ExceptionStream or ModuleListStream;
                continue;
            }

            switch (type)
            {
                case SystemInfoStream when size >= 0x18:
                    build = b.U32(rva + 0x10) is { } n and > 0 and < 100_000 ? (int)n : null;
                    arch = b.U16(rva) switch
                    {
                        9 => "x64",
                        12 => "ARM64",
                        0 => "x86",
                        _ => null,
                    };
                    break;
                case ExceptionStream when size >= 0x20:
                    exceptionCode = b.U32(rva + 8);
                    exceptionAddress = b.U64(rva + 0x18);
                    break;
                case ModuleListStream when size >= 4:
                    truncated |= ReadModules(b, rva, size, modules);
                    break;
            }
        }

        uint? stamp = b.U32(0x14);
        return new CrashDumpInfo
        {
            Format = DumpFormat.UserMinidump,
            KindText = "Program memory dump",
            ExceptionCode = exceptionCode,
            ExceptionAddress = exceptionAddress,
            OsBuild = build,
            Architecture = arch,
            CrashTime = stamp is { } s and > 946_684_800 ? DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime() : null,
            Modules = modules,
            Truncated = truncated,
        };
    }

    /// <summary>Adds the modules of a MINIDUMP_MODULE_LIST; returns true when the list ran past the stream or the file.</summary>
    private static bool ReadModules(DumpBytes b, uint rva, uint size, List<CrashModule> modules)
    {
        uint count = b.U32(rva) ?? 0;
        long available = (size - 4L) / ModuleEntrySize;
        bool truncated = count > available;
        long n = Math.Min(Math.Min(count, available), MaxModules);
        for (long i = 0; i < n; i++)
        {
            long m = rva + 4 + (i * ModuleEntrySize);
            if (b.U64(m) is not { } baseAddress || b.U32(m + 8) is not { } imageSize || b.U32(m + 0x14) is not { } nameRva)
            {
                return true;
            }

            // MINIDUMP_STRING: ULONG Length in bytes, then UTF-16.
            string? name = b.U32(nameRva) is { } bytes and > 0 and < 2048 ? b.Utf16(nameRva + 4L, bytes / 2) : null;
            if (name is null)
            {
                truncated = true;
                continue;
            }

            modules.Add(new CrashModule(name.Replace('/', '\\').Split('\\')[^1], baseAddress, imageSize));
        }

        return truncated;
    }
}
