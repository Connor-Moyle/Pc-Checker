using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcChecker.Core.Processes;

/// <summary>
/// The Task Manager details: thread, handle and session counts and the suspended state for every process in one
/// system call, plus architecture, elevation, command line, efficiency mode, suspend and resume, affinity and memory
/// dumps for one process. Guarded like the rest of <see cref="ProcessNative"/>.
/// </summary>
internal static partial class ProcessNative
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessDupHandle = 0x0040;
    private const uint ProcessSuspendResume = 0x0800;
    private const int SystemProcessInformationClass = 5;
    private const int ProcessCommandLineInformationClass = 60;
    private const int ProcessPowerThrottlingClass = 4;
    private const int NtProcessPowerThrottlingStateClass = 77;
    private const int ProcessMachineTypeInfoClass = 9;
    private const int TokenElevationClass = 20;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferTooSmall = 0xC0000023;

    // Set once the efficiency mode query has failed and been logged.
    private static int _efficiencyQueryWarned;

    // Layout of SYSTEM_PROCESS_INFORMATION and SYSTEM_THREAD_INFORMATION on 64-bit Windows (x64 and ARM64).
    private const int ProcessEntrySize = 256;
    private const int ThreadEntrySize = 80;
    private const int OffsetThreadCount = 4;
    private const int OffsetProcessId = 80;
    private const int OffsetHandleCount = 96;
    private const int OffsetSessionId = 100;
    private const int OffsetThreadState = 68;
    private const int OffsetWaitReason = 72;

    // MiniDumpWithFullMemory | WithHandleData | WithUnloadedModules | WithFullMemoryInfo | WithThreadInfo, as Task Manager.
    private const uint FullDumpType = 0x2 | 0x4 | 0x20 | 0x800 | 0x1000;

    /// <summary>Per-process figures from one NtQuerySystemInformation call.</summary>
    internal readonly record struct SystemDetails(int Threads, int Handles, int Session, bool Suspended);

    /// <summary>Details that do not change while a process runs; read once per run.</summary>
    internal readonly record struct StaticDetails(ProcessArchitecture Architecture, bool? Elevated, string? CommandLine);

    /// <summary>
    /// Thread and handle counts, session and suspended state of every process. Empty when the call fails or on 32-bit
    /// Windows (PC Checker ships for x64 and ARM64 only; the layout below is the 64-bit one).
    /// </summary>
    public static IReadOnlyDictionary<int, SystemDetails> ReadSystemDetails()
    {
        var result = new Dictionary<int, SystemDetails>(512);
        if (IntPtr.Size != 8)
        {
            return result;
        }

        int size = 1 << 20;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                uint status = (uint)NtQuerySystemInformation(SystemProcessInformationClass, buffer, size, out int needed);
                if (status == 0)
                {
                    Parse(buffer, size, result);
                    return result;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall)
                {
                    return result;
                }

                size = Math.Max(size * 2, needed + (64 * 1024));
            }
        }
        catch
        {
            // Return what was parsed so far.
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    private static void Parse(IntPtr buffer, int size, Dictionary<int, SystemDetails> result)
    {
        long offset = 0;
        while (offset + ProcessEntrySize <= size)
        {
            IntPtr entry = buffer + (int)offset;
            uint next = (uint)Marshal.ReadInt32(entry);
            int threads = Marshal.ReadInt32(entry, OffsetThreadCount);
            int pid = (int)Marshal.ReadInt64(entry, OffsetProcessId);
            int handles = Marshal.ReadInt32(entry, OffsetHandleCount);
            int session = Marshal.ReadInt32(entry, OffsetSessionId);
            var states = new List<(int State, int WaitReason)>(Math.Clamp(threads, 0, 4096));
            for (int i = 0; i < threads && i < 4096; i++)
            {
                long threadOffset = offset + ProcessEntrySize + ((long)i * ThreadEntrySize);
                if (threadOffset + ThreadEntrySize > size)
                {
                    break;
                }

                IntPtr thread = buffer + (int)threadOffset;
                states.Add((Marshal.ReadInt32(thread, OffsetThreadState), Marshal.ReadInt32(thread, OffsetWaitReason)));
            }

            result[pid] = new SystemDetails(threads, handles, session, pid > CriticalProcessRules.SystemPid && ProcessStatusRules.IsSuspended(states));
            if (next == 0)
            {
                break;
            }

            offset += next;
        }
    }

    /// <summary>Architecture, elevation and command line of one process; each part is null or unknown when it cannot be read.</summary>
    public static StaticDetails ReadStaticDetails(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            return handle == IntPtr.Zero ? default : new StaticDetails(ReadArchitecture(handle), ReadElevation(handle), ReadCommandLine(handle));
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

    private static ProcessArchitecture ReadArchitecture(IntPtr handle)
    {
        try
        {
            // Windows 11 tells the real machine type, also for x64 programs emulated on ARM64.
            var info = default(ProcessMachineInformation);
            if (GetProcessInformation(handle, ProcessMachineTypeInfoClass, ref info, Marshal.SizeOf<ProcessMachineInformation>()))
            {
                var architecture = ProcessArchitectureRules.FromMachine(info.ProcessMachine);
                if (architecture != ProcessArchitecture.Unknown)
                {
                    return architecture;
                }
            }
        }
        catch
        {
            // Older Windows: fall back to IsWow64Process2.
        }

        try
        {
            return IsWow64Process2(handle, out ushort process, out ushort native)
                ? ProcessArchitectureRules.FromWow64(process, native)
                : ProcessArchitecture.Unknown;
        }
        catch
        {
            return ProcessArchitecture.Unknown;
        }
    }

    private static bool? ReadElevation(IntPtr process)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token))
            {
                return null;
            }

            return GetTokenInformation(token, TokenElevationClass, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    private static string? ReadCommandLine(IntPtr handle)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformationClass, IntPtr.Zero, 0, out int needed);
            if (needed <= 0 || needed > 1 << 20)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(needed);
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformationClass, buffer, needed, out _) != 0)
            {
                return null;
            }

            // UNICODE_STRING: Length (bytes), MaximumLength, padding, then the pointer to the text inside the same buffer.
            int length = (ushort)Marshal.ReadInt16(buffer);
            IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length > 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text, length / 2) : null;
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
        }
    }

    /// <summary>
    /// Whether efficiency mode is on. Older Windows 10 and 11 builds do not answer GetProcessInformation for this
    /// class; the same state is then read with NtQueryInformationProcess (ProcessPowerThrottlingState), which Task
    /// Manager uses. When both fail the first time, a warning is written once, so an "Efficiency mode: off"
    /// everywhere can be explained from the log.
    /// </summary>
    private static bool ReadEfficiency(IntPtr handle)
    {
        try
        {
            int size = Marshal.SizeOf<PowerThrottlingState>();
            var state = new PowerThrottlingState { Version = EfficiencyModeRules.Version };
            if (GetProcessInformation(handle, ProcessPowerThrottlingClass, ref state, size))
            {
                return EfficiencyModeRules.IsOn(state.ControlMask, state.StateMask);
            }

            int error = Marshal.GetLastWin32Error();
            state = new PowerThrottlingState { Version = EfficiencyModeRules.Version };
            int status = NtQueryInformationProcess(handle, NtProcessPowerThrottlingStateClass, ref state, size, out _);
            if (status == 0)
            {
                return EfficiencyModeRules.IsOn(state.ControlMask, state.StateMask);
            }

            if (Interlocked.Exchange(ref _efficiencyQueryWarned, 1) == 0)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Efficiency mode could not be read (GetProcessInformation error {error}, NtQueryInformationProcess status 0x{status:X8}); it is shown as off.");
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Suspends or resumes the process if it is still the same run. Returns a Win32 or NTSTATUS error code (0 on success).</summary>
    public static int SuspendOrResume(int pid, long expectedCreationFileTime, bool suspend) =>
        WithProcess(pid, expectedCreationFileTime, ProcessSuspendResume, handle => suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle));

    /// <summary>Turns EcoQoS throttling and the matching priority on or off. Returns an error code (0 on success).</summary>
    public static int SetEfficiency(int pid, long expectedCreationFileTime, bool on) =>
        WithProcess(pid, expectedCreationFileTime, ProcessSetInformation, handle =>
        {
            var (control, stateMask, priority) = EfficiencyModeRules.Request(on);
            var state = new PowerThrottlingState { Version = EfficiencyModeRules.Version, ControlMask = control, StateMask = stateMask };
            if (!SetProcessInformation(handle, ProcessPowerThrottlingClass, ref state, Marshal.SizeOf<PowerThrottlingState>()))
            {
                return Marshal.GetLastWin32Error();
            }

            return SetPriorityClass(handle, PriorityRules.ToPriorityClass(priority)) ? 0 : Marshal.GetLastWin32Error();
        });

    /// <summary>The affinity mask of the process, or null.</summary>
    public static ulong? GetAffinity(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            return handle != IntPtr.Zero && GetProcessAffinityMask(handle, out UIntPtr mask, out _) ? (ulong)mask : null;
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

    /// <summary>Sets the affinity mask if the process is still the same run. Returns an error code (0 on success).</summary>
    public static int SetAffinity(int pid, long expectedCreationFileTime, ulong mask) =>
        WithProcess(pid, expectedCreationFileTime, ProcessSetInformation, handle =>
            SetProcessAffinityMask(handle, (UIntPtr)mask) ? 0 : Marshal.GetLastWin32Error());

    /// <summary>Writes a full memory dump of the process to <paramref name="path"/>. Returns an error code (0 on success).</summary>
    public static int WriteDump(int pid, long expectedCreationFileTime, string path) =>
        WithProcess(pid, expectedCreationFileTime, ProcessQueryInformation | ProcessVmRead | ProcessDupHandle, handle =>
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return MiniDumpWriteDump(handle, (uint)pid, file.SafeFileHandle, FullDumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                ? 0
                : Marshal.GetHRForLastWin32Error();
        });

    /// <summary>Waits up to <paramref name="timeout"/> for the process to exit. True when it has exited (or is gone).</summary>
    public static bool WaitForExit(int pid, long expectedCreationFileTime, TimeSpan timeout)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorInvalidParameter;
            }

            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || created != expectedCreationFileTime)
            {
                return true;
            }

            return WaitForSingleObject(handle, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, 120_000)) == WaitObject0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>Opens the process with <paramref name="access"/>, checks it is the same run and runs <paramref name="action"/>.</summary>
    private static int WithProcess(int pid, long expectedCreationFileTime, uint access, Func<IntPtr, int> action)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(access | ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || created != expectedCreationFileTime)
            {
                return ErrorInvalidParameter;
            }

            return action(handle);
        }
        catch (Exception ex)
        {
            return ex.HResult == 0 ? -1 : ex.HResult;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMachineInformation
    {
        public ushort ProcessMachine;
        public ushort Reserved;
        public uint MachineAttributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref PowerThrottlingState information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int informationClass, ref ProcessMachineInformation information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returnLength);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
