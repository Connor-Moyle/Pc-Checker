namespace PcChecker.Core.Crashes.Dumps;

/// <summary>Reads a dump file's header from disk (shared read, size-capped) and hands the bytes to the right parser.</summary>
public static class DumpFileReader
{
    /// <summary>Small dumps are read whole up to this size; bigger files (MEMORY.DMP) only to their header.</summary>
    public const int MaxWholeFileBytes = 16 * 1024 * 1024;

    /// <summary>Enough for the 64-bit kernel dump header and the start of a triage block.</summary>
    public const int HeaderBytes = KernelDumpReader.Header64Size + 0x1000;

    /// <summary>Parses bytes that start a dump file; null when the format is not recognised.</summary>
    public static CrashDumpInfo? Parse(ReadOnlySpan<byte> data) =>
        KernelDumpReader.IsKernelDump(data) ? KernelDumpReader.Read(data)
        : MinidumpReader.IsMinidump(data) ? MinidumpReader.Read(data)
        : null;

    /// <summary>Reads and parses <paramref name="path"/>; null when it cannot be opened or is not a dump.</summary>
    public static CrashDumpInfo? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            long length = stream.Length;
            int toRead = (int)Math.Min(length, length <= MaxWholeFileBytes ? MaxWholeFileBytes : HeaderBytes);
            var buffer = new byte[toRead];
            int read = 0;
            while (read < toRead)
            {
                int n = stream.Read(buffer, read, toRead - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return Parse(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
