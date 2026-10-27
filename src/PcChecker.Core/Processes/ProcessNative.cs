using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PcChecker.Core.Processes;

/// <summary>
/// The Win32 calls behind the process list. Every method is guarded: on failure it returns null, false or an
/// outcome value instead of throwing. Processes are opened with limited query rights only (which also works for most
/// protected processes); terminate and set-information rights are requested only for the action itself.
/// </summary>
internal static partial class ProcessNative
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;
    private const uint WaitObject0 = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly uint MemoryCountersExSize = (uint)Marshal.OffsetOf<ProcessMemoryCountersEx2>(nameof(ProcessMemoryCountersEx2.PrivateWorkingSetSize));

    /// <summary>One line of the Toolhelp process list.</summary>
    internal readonly record struct ProcessEntry(int Pid, int ParentPid, string Name);

    /// <summary>Cumulative counters of one process; <see cref="Ok"/> is false when the process could not be opened.</summary>
    internal readonly record struct Reading(bool Ok, long CreationFileTime, long CpuTicks, long ReadBytes, long WriteBytes, long PrivateBytes, uint PriorityClass)
    {
        /// <summary>True when Windows throttles the process with EcoQoS (efficiency mode).</summary>
        public bool Efficiency { get; init; }
    }

    internal enum EndOutcome
    {
        Ended,
        Critical,
        AlreadyExited,
        AccessDenied,
        StillRunning,
        Failed,
    }

    /// <summary>Every process with its parent id and file name (Toolhelp snapshot). Empty when the snapshot fails.</summary>
    public static IReadOnlyList<ProcessEntry> EnumerateProcesses()
    {
        var list = new List<ProcessEntry>(256);
        IntPtr snapshot = IntPtr.Zero;
        try
        {
            snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            {
                return list;
            }

            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return list;
            }

            do
            {
                list.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        catch
        {
            // Return what was read so far.
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != InvalidHandleValue)
            {
                CloseHandle(snapshot);
            }
        }

        return list;
    }

    /// <summary>CPU time, I/O byte counts, private working set and priority class of one process.</summary>
    public static Reading Read(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return default;
            }

            long created = 0, cpu = 0;
            if (GetProcessTimes(handle, out created, out _, out long kernel, out long user))
            {
                cpu = kernel + user;
            }

            long read = 0, write = 0;
            if (GetProcessIoCounters(handle, out var io))
            {
                read = (long)Math.Min(io.ReadTransferCount, long.MaxValue);
                write = (long)Math.Min(io.WriteTransferCount, long.MaxValue);
            }

            long privateBytes = 0;
            var memory = new ProcessMemoryCountersEx2 { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
            bool gotMemory = K32GetProcessMemoryInfo(handle, ref memory, memory.cb);
            if (!gotMemory)
            {
                // Windows builds from before PROCESS_MEMORY_COUNTERS_EX2 reject its size; ask for the EX layout instead
                // (the same structure without the last two fields, which then stay zero).
                memory = new ProcessMemoryCountersEx2 { cb = MemoryCountersExSize };
                gotMemory = K32GetProcessMemoryInfo(handle, ref memory, memory.cb);
            }

            if (gotMemory)
            {
                // PrivateWorkingSetSize is what Task Manager shows as "Memory"; older builds leave it at zero.
                ulong value = (ulong)memory.PrivateWorkingSetSize;
                privateBytes = (long)Math.Min(value != 0 ? value : (ulong)memory.WorkingSetSize, long.MaxValue);
            }

            uint priority = GetPriorityClass(handle);
            return new Reading(true, created, cpu, read, write, privateBytes, priority) { Efficiency = ReadEfficiency(handle) };
        }
        catch
        {
            return default;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>When the process started, or null when Windows does not tell.</summary>
    public static DateTimeOffset? TryGetStartTime(int pid)
    {
        var reading = Read(pid);
        return reading.Ok && reading.CreationFileTime > 0 ? FromFileTime(reading.CreationFileTime) : null;
    }

    /// <summary>Converts a FILETIME to local time, or null for zero or out-of-range values.</summary>
    public static DateTimeOffset? FromFileTime(long fileTime)
    {
        try
        {
            return fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Full path of the program file and the SID of the account it runs as; either may be null.</summary>
    public static (string? Path, string? Sid) ReadIdentity(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return (null, null);
            }

            string? path = null;
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                path = buffer.ToString(0, (int)size);
            }

            return (path, TryGetTokenSid(handle));
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>"DOMAIN\name" → "name" for display; null when the SID cannot be translated.</summary>
    public static string? TryTranslateSid(string sid)
    {
        try
        {
            var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            int slash = account.IndexOf('\\');
            return slash >= 0 ? account[(slash + 1)..] : account;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Idle, kernel and user time of the whole machine (100 ns units).</summary>
    public static bool TryGetSystemTimes(out long idle, out long kernel, out long user)
    {
        idle = kernel = user = 0;
        try
        {
            return GetSystemTimes(out idle, out kernel, out user);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ends the process if it is still the same run (same start time) and waits up to <paramref name="wait"/> for it
    /// to exit. A different start time means the id was reused: nothing is ended and AlreadyExited is returned.
    /// </summary>
    public static (EndOutcome Outcome, int Error) Terminate(int pid, long expectedCreationFileTime, TimeSpan wait)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return error == ErrorInvalidParameter ? (EndOutcome.AlreadyExited, 0)
                    : error == ErrorAccessDenied ? (EndOutcome.AccessDenied, error)
                    : (EndOutcome.Failed, error);
            }

            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || created != expectedCreationFileTime)
            {
                return (EndOutcome.AlreadyExited, 0);
            }

            // Windows marks some processes critical (for example a svchost hosting DcomLaunch or RpcSs): ending one
            // stops the PC. Refuse those even when their name is not on the fixed list.
            if (IsCritical(handle) == true)
            {
                return (EndOutcome.Critical, 0);
            }

            if (!TerminateProcess(handle, 1))
            {
                int error = Marshal.GetLastWin32Error();
                return error == ErrorAccessDenied ? (EndOutcome.AccessDenied, error) : (EndOutcome.Failed, error);
            }

            uint waited = WaitForSingleObject(handle, (uint)Math.Clamp(wait.TotalMilliseconds, 0, 60_000));
            return waited == WaitObject0 ? (EndOutcome.Ended, 0) : (EndOutcome.StillRunning, 0);
        }
        catch (Exception ex)
        {
            return (EndOutcome.Failed, ex.HResult);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// Sets the priority class if the process is still the same run. Returns the previous class and the class Windows
    /// actually applied (0 when unknown).
    /// </summary>
    public static (bool Ok, int Error, uint Previous, uint Applied) SetPriority(int pid, long expectedCreationFileTime, uint priorityClass)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return (false, Marshal.GetLastWin32Error(), 0, 0);
            }

            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || created != expectedCreationFileTime)
            {
                return (false, ErrorInvalidParameter, 0, 0);
            }

            uint previous = GetPriorityClass(handle);
            if (!SetPriorityClass(handle, priorityClass))
            {
                return (false, Marshal.GetLastWin32Error(), previous, 0);
            }

            // Without the "increase scheduling priority" privilege Windows quietly picks High instead of Realtime.
            return (true, 0, previous, GetPriorityClass(handle));
        }
        catch (Exception ex)
        {
            return (false, ex.HResult, 0, 0);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// True when Windows marks the process critical (ending it stops the PC), null when that cannot be read. Uses
    /// IsProcessCritical (Windows 8.1 and later), which needs only limited query rights.
    /// </summary>
    public static bool? IsCriticalProcess(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            return handle == IntPtr.Zero ? null : IsCritical(handle);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    private static bool? IsCritical(IntPtr handle)
    {
        try
        {
            return IsProcessCritical(handle, out bool critical) ? critical : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetTokenSid(IntPtr process)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token))
            {
                return null;
            }

            GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out int length);
            if (length <= 0 || length > 64 * 1024)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenUserClass, buffer, length, out _))
            {
                return null;
            }

            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid).Value;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);
}
