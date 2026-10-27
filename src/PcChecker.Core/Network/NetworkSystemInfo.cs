using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using PcChecker.Core.Common;

namespace PcChecker.Core.Network;

/// <summary>Small, guarded facts about this PC that the network services need. Never throws.</summary>
public static class NetworkSystemInfo
{
    /// <summary>"PcChecker/0.3.0" for HTTP requests.</summary>
    public static string UserAgent { get; } = "PcChecker/" + ProductVersion();

    /// <summary>True when Windows reports at least one Wi-Fi (802.11) interface, connected or not.</summary>
    public static bool HasWirelessAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Interface GUIDs ("{…}") of the Wi-Fi adapters.</summary>
    public static IReadOnlyList<string> WirelessInterfaceIds()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(n => n.Id)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>True when the PC has a system battery (a laptop or tablet). False when unknown.</summary>
    public static bool HasBattery()
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            // BatteryFlag 128 = "no system battery", 255 = unknown status.
            return NativeMethods.GetSystemPowerStatus(out var status) && status.BatteryFlag != 128 && status.BatteryFlag != 255;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows 11 starts at build 22000.</summary>
    public static bool IsWindows11OrNewer() => PlatformGuard.IsWindows && Environment.OSVersion.Version.Build >= 22000;

    private static string ProductVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(NetworkSystemInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }
}
