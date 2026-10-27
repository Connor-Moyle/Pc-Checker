using System.Runtime.InteropServices;

namespace PcChecker.Core.LiveUsage;

/// <summary>
/// A small wrapper over the Windows Performance Data Helper (pdh.dll). Counters are added with their English paths
/// (<c>PdhAddEnglishCounter</c>), so the same paths work on every display language. Rate counters need two
/// <see cref="Collect"/> calls before they return values. Not thread-safe; the owner serializes calls.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;

    // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then PDH_FMT_COUNTERVALUE (status + 8-byte aligned union) = 24 bytes.
    private const int ItemSize = 24;
    private const int ItemStatusOffset = 8;
    private const int ItemValueOffset = 16;

    // PDH_RAW_COUNTER_ITEM_W: a name pointer, then PDH_RAW_COUNTER (status, FILETIME, two 64-bit values, count) = 48 bytes.
    private const int RawItemSize = 48;

    private IntPtr _query;

    private PdhQuery(IntPtr query) => _query = query;

    /// <summary>Opens a live query, or returns null when PDH is not available.</summary>
    public static PdhQuery? TryOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return PdhOpenQueryW(null, IntPtr.Zero, out var query) == 0 && query != IntPtr.Zero ? new PdhQuery(query) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Adds a counter by its English path; returns <see cref="IntPtr.Zero"/> when the counter does not exist here.</summary>
    public IntPtr Add(string englishPath)
    {
        if (_query == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            return PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Adds the first of <paramref name="englishPaths"/> that exists.</summary>
    public IntPtr AddFirst(params string[] englishPaths)
    {
        foreach (string path in englishPaths)
        {
            var counter = Add(path);
            if (counter != IntPtr.Zero)
            {
                return counter;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Takes a reading of every counter in the query.</summary>
    public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

    /// <summary>The value of a single-instance counter, or null when it has no valid data yet.</summary>
    public double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
        {
            return null;
        }

        if (PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value) != 0)
        {
            return null;
        }

        return value.CStatus is PdhCstatusValidData or PdhCstatusNewData && double.IsFinite(value.DoubleValue) ? value.DoubleValue : null;
    }

    /// <summary>
    /// The instance names of a wildcard counter after one <see cref="Collect"/>, read from the raw values so rate
    /// counters list their instances before they have a second reading.
    /// </summary>
    public List<string> ReadInstanceNames(IntPtr counter)
    {
        var names = new List<string>();
        if (counter == IntPtr.Zero)
        {
            return names;
        }

        uint size = 0;
        if (PdhGetRawCounterArrayW(counter, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0)
        {
            return names;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetRawCounterArrayW(counter, ref size, out uint count, buffer) != 0)
            {
                return names;
            }

            for (int i = 0; i < count && (i + 1) * RawItemSize <= size; i++)
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * RawItemSize)) is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return names;
    }

    /// <summary>
    /// Every instance of a wildcard counter ("\PhysicalDisk(*)\% Idle Time") with a valid value (or NaN for the others
    /// when <paramref name="includeInvalid"/> is true).
    /// </summary>
    public List<KeyValuePair<string, double>> ReadArray(IntPtr counter, bool includeInvalid = false)
    {
        var result = new List<KeyValuePair<string, double>>();
        if (counter == IntPtr.Zero)
        {
            return result;
        }

        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0)
        {
            return result;
        }

        // Instances can appear between the two calls; try a couple of times with the size PDH asks for.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint count, buffer);
                if (status == PdhMoreData)
                {
                    continue;
                }

                if (status != 0)
                {
                    return result;
                }

                for (int i = 0; i < count; i++)
                {
                    int offset = i * ItemSize;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, offset));
                    uint itemStatus = unchecked((uint)Marshal.ReadInt32(buffer, offset + ItemStatusOffset));
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, offset + ItemValueOffset));
                    if (name is null)
                    {
                        continue;
                    }

                    bool valid = itemStatus is PdhCstatusValidData or PdhCstatusNewData && double.IsFinite(value);
                    if (valid || includeInvalid)
                    {
                        result.Add(new KeyValuePair<string, double>(name, valid ? value : double.NaN));
                    }
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetRawCounterArrayW(IntPtr counter, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
