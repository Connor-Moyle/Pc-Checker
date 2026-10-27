using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcChecker.Core.Services;

/// <summary>
/// Thin P/Invoke wrappers over the Service Control Manager (advapi32) for the actions the Services page performs:
/// start, stop, pause, continue, change the start type and the delayed-auto-start flag, and read dependencies.
/// Nothing here parses text output. Every call returns a plain result instead of throwing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceControlNative
{
    private const int SC_MANAGER_CONNECT = 0x0001;
    private const int SERVICE_QUERY_CONFIG = 0x0001;
    private const int SERVICE_CHANGE_CONFIG = 0x0002;
    private const int SERVICE_QUERY_STATUS = 0x0004;
    private const int SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    private const int SERVICE_START = 0x0010;
    private const int SERVICE_STOP = 0x0020;
    private const int SERVICE_PAUSE_CONTINUE = 0x0040;

    private const int SERVICE_NO_CHANGE = unchecked((int)0xFFFFFFFF);
    private const int SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    private const int SERVICE_CONTROL_STOP = 0x0001;
    private const int SERVICE_CONTROL_PAUSE = 0x0002;
    private const int SERVICE_CONTROL_CONTINUE = 0x0003;

    private const int SERVICE_ACTIVE = 0x1;
    private const int SERVICE_INACTIVE = 0x2;
    private const int SERVICE_STATE_ALL = SERVICE_ACTIVE | SERVICE_INACTIVE;

    private const int SERVICE_STOPPED = 0x1;

    internal enum ControlAction
    {
        Start,
        Stop,
        Pause,
        Continue,
    }

    /// <summary>Starts, stops, pauses or continues a service. Returns null on success, or a message on failure.</summary>
    internal static string? Control(string serviceName, ControlAction action)
    {
        int access = action switch
        {
            ControlAction.Start => SERVICE_START | SERVICE_QUERY_STATUS,
            ControlAction.Stop => SERVICE_STOP | SERVICE_QUERY_STATUS,
            _ => SERVICE_PAUSE_CONTINUE | SERVICE_QUERY_STATUS,
        };

        return WithService(serviceName, access, handle =>
        {
            bool ok;
            if (action == ControlAction.Start)
            {
                ok = StartService(handle, 0, null);
            }
            else
            {
                int control = action switch
                {
                    ControlAction.Stop => SERVICE_CONTROL_STOP,
                    ControlAction.Pause => SERVICE_CONTROL_PAUSE,
                    _ => SERVICE_CONTROL_CONTINUE,
                };
                var status = default(SERVICE_STATUS);
                ok = ControlService(handle, control, ref status);
            }

            return ok ? null : ServiceErrorText.Describe(Marshal.GetLastWin32Error());
        });
    }

    /// <summary>Sets the start type, and the delayed-auto-start flag when the type is Automatic.</summary>
    internal static string? SetStartType(string serviceName, int win32StartType, bool delayed)
    {
        return WithService(serviceName, SERVICE_CHANGE_CONFIG, handle =>
        {
            if (!ChangeServiceConfig(handle, SERVICE_NO_CHANGE, win32StartType, SERVICE_NO_CHANGE,
                    null, null, IntPtr.Zero, null, null, null, null))
            {
                return "The start type could not be changed. " + ServiceErrorText.Describe(Marshal.GetLastWin32Error());
            }

            // The delayed flag only applies to Automatic; clear it for every other type.
            var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_DELAYED_AUTO_START_INFO>());
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!ChangeServiceConfig2(handle, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, buffer))
                {
                    return "The start type changed but the delayed-start flag could not be set. " + ServiceErrorText.Describe(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return null;
        });
    }

    /// <summary>
    /// Every direct and indirect dependent of <paramref name="serviceName"/>, with whether it is running, in the order
    /// Windows returns them (reverse start order, so a safe order to stop them in).
    /// </summary>
    internal static IReadOnlyList<ServiceRestartPlan.Dependent> EnumerateDependents(string serviceName)
    {
        var result = new List<ServiceRestartPlan.Dependent>();
        WithService(serviceName, SERVICE_ENUMERATE_DEPENDENTS, handle =>
        {
            int bytesNeeded = 0, count = 0;
            EnumDependentServices(handle, SERVICE_STATE_ALL, IntPtr.Zero, 0, ref bytesNeeded, ref count);
            if (bytesNeeded <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(bytesNeeded);
            try
            {
                if (EnumDependentServices(handle, SERVICE_STATE_ALL, buffer, bytesNeeded, ref bytesNeeded, ref count))
                {
                    int size = Marshal.SizeOf<ENUM_SERVICE_STATUS>();
                    for (int i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS>(buffer + (i * size));
                        if (!string.IsNullOrEmpty(entry.lpServiceName))
                        {
                            result.Add(new ServiceRestartPlan.Dependent(entry.lpServiceName,
                                entry.ServiceStatus.dwCurrentState != SERVICE_STOPPED));
                        }
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        });

        return result;
    }

    /// <summary>
    /// Waits until the service reaches <paramref name="target"/> (polling its status, as the Services console does) or
    /// the timeout passes. Returns null when it got there, else a plain message.
    /// </summary>
    internal static async Task<string?> WaitForStateAsync(string serviceName, ServiceRunState target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = QueryState(serviceName);
            if (state == target)
            {
                return null;
            }

            // A service that settled in a different state (for example it stopped again right after starting) will not
            // get there, so give up after a short grace period instead of waiting for the whole timeout.
            bool pending = state is ServiceRunState.StartPending or ServiceRunState.StopPending
                or ServiceRunState.PausePending or ServiceRunState.ContinuePending;
            var elapsed = DateTime.UtcNow - started;
            if (elapsed > timeout || (!pending && elapsed > TimeSpan.FromSeconds(3)))
            {
                return $"The service did not finish in time; it is now {ServiceStartupMapping.StateLabel(state).TrimEnd('…').ToLowerInvariant()}.";
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The current state of a service, or Unknown when it cannot be read.</summary>
    internal static ServiceRunState QueryState(string serviceName)
    {
        var state = ServiceRunState.Unknown;
        WithService(serviceName, SERVICE_QUERY_STATUS, handle =>
        {
            var status = default(SERVICE_STATUS);
            if (QueryServiceStatus(handle, ref status))
            {
                state = status.dwCurrentState switch
                {
                    SERVICE_STOPPED => ServiceRunState.Stopped,
                    2 => ServiceRunState.StartPending,
                    3 => ServiceRunState.StopPending,
                    4 => ServiceRunState.Running,
                    5 => ServiceRunState.ContinuePending,
                    6 => ServiceRunState.PausePending,
                    7 => ServiceRunState.Paused,
                    _ => ServiceRunState.Unknown,
                };
            }

            return null;
        });

        return state;
    }

    private static string? WithService(string serviceName, int access, Func<IntPtr, string?> work)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return "PC Checker could not reach the Service Control Manager. " + ServiceErrorText.Describe(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(scm, serviceName, access);
            if (service == IntPtr.Zero)
            {
                return $"The service '{serviceName}' could not be opened. " + ServiceErrorText.Describe(Marshal.GetLastWin32Error());
            }

            try
            {
                return work(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpServiceName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpDisplayName;
        public SERVICE_STATUS ServiceStatus;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, int numArgs, string[]? args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, int control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, int serviceType, int startType, int errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, IntPtr info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServices(IntPtr service, int serviceState, IntPtr services, int bufferSize,
        ref int bytesNeeded, ref int servicesReturned);
}
