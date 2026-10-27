using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>
/// Reads where PawnIO stands right now: the uninstall entry (both registry views), the driver service key and, as the
/// definitive check, whether its device opens. Read fresh on every call (LibreHardwareMonitor's own check is cached for
/// the life of the process, so it stays "not installed" after an install). Never throws.
/// </summary>
public static class PawnIoProbe
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    public static PawnIoStatus Probe()
    {
        if (!PlatformGuard.IsWindows)
        {
            return PawnIoStatus.NotSupported;
        }

        string? version = ReadUninstallVersion();
        bool? service = ServiceRegistered();
        bool? device = DeviceOpens();
        return new PawnIoStatus(PawnIoRules.Classify(true, version, service, device), version, service, device);
    }

    private static string? ReadUninstallVersion()
    {
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(PawnIoRules.UninstallKey);
                if (key?.GetValue("DisplayVersion") is string v && !string.IsNullOrWhiteSpace(v))
                {
                    return v.Trim();
                }

                if (key is not null)
                {
                    return "unknown";
                }
            }
        }
        catch
        {
            // Unreadable: say nothing about it.
        }

        return null;
    }

    private static bool? ServiceRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + PawnIoRules.ServiceName);
            return key is not null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? DeviceOpens()
    {
        try
        {
            using var handle = CreateFileW(PawnIoRules.DevicePath, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            return handle.IsInvalid ? PawnIoRules.DeviceOpenResult(Marshal.GetLastPInvokeError()) : true;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
