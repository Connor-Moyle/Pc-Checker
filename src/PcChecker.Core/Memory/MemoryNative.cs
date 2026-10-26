using System.Runtime.InteropServices;
using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>
/// Win32 calls behind the memory overview and the standby-list purge. Every call is guarded: on failure the method
/// returns false or a failed <see cref="OperationResult"/> instead of throwing.
/// </summary>
public static class MemoryNative
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";
    private const int ErrorNotAllAssigned = 1300;

    /// <summary>Total and available physical memory from <c>GlobalMemoryStatusEx</c>.</summary>
    public static bool TryGetMemoryStatus(out long totalBytes, out long availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return false;
            }

            totalBytes = (long)status.ullTotalPhys;
            availableBytes = (long)status.ullAvailPhys;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Commit charge, commit limit and system cache size from <c>GetPerformanceInfo</c>.</summary>
    public static bool TryGetPerformanceInfo(out long commitTotalBytes, out long commitLimitBytes, out long systemCacheBytes)
    {
        commitTotalBytes = 0;
        commitLimitBytes = 0;
        systemCacheBytes = 0;
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            var info = new PerformanceInformation { cb = (uint)Marshal.SizeOf<PerformanceInformation>() };
            if (!GetPerformanceInfo(ref info, info.cb))
            {
                return false;
            }

            long pageSize = (long)info.PageSize;
            commitTotalBytes = (long)info.CommitTotal * pageSize;
            commitLimitBytes = (long)info.CommitLimit * pageSize;
            systemCacheBytes = (long)info.SystemCache * pageSize;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Empties the standby list (cached file pages that are not in use) by enabling
    /// <c>SeProfileSingleProcessPrivilege</c> and calling <c>NtSetSystemInformation(SystemMemoryListInformation)</c>.
    /// Requires an elevated process.
    /// </summary>
    public static OperationResult PurgeStandbyList()
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Clearing the standby list is only possible on Windows.");
        }

        try
        {
            if (!EnablePrivilege(SeProfileSingleProcessPrivilege, out string? reason))
            {
                return OperationResult.Fail("Windows refused the privilege needed to clear the standby list. Run PC Checker as administrator. " + reason);
            }

            int command = MemoryPurgeStandbyList;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            if (status != 0)
            {
                return OperationResult.Fail($"Windows rejected the request (NTSTATUS 0x{status:X8}). This usually means the app is not running as administrator.");
            }

            return OperationResult.Ok("Standby list cleared.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Could not clear the standby list: " + ex.Message, ex);
        }
    }

    private static bool EnablePrivilege(string privilege, out string? reason)
    {
        reason = null;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
            {
                reason = $"OpenProcessToken failed (error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            if (!LookupPrivilegeValue(null, privilege, out Luid luid))
            {
                reason = $"LookupPrivilegeValue failed (error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                reason = $"AdjustTokenPrivileges failed (error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAllAssigned)
            {
                reason = "The privilege is not held by this process.";
                return false;
            }

            return true;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation pPerformanceInformation, uint cb);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int systemInformationClass, ref int systemInformation, int systemInformationLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
