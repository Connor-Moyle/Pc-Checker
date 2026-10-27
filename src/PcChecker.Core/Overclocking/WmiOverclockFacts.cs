using System.Management;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Minimal WMI fallbacks for when the hardware inventory is unavailable: GPU, CPU and memory facts needed to
/// detect vendors and the XMP state. Every query is guarded and returns empty results on failure.
/// </summary>
public static class WmiOverclockFacts
{
    public static IReadOnlyList<GpuInfo> QueryGpus()
    {
        var list = new List<GpuInfo>();
        if (!PlatformGuard.IsWindows)
        {
            return list;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, AdapterRAM, DriverVersion, PNPDeviceID, VideoProcessor FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                string name = o["Name"]?.ToString() ?? "Graphics adapter";
                string? vendor = o["AdapterCompatibility"]?.ToString();
                long? ram = o["AdapterRAM"] is { } r ? Convert.ToInt64(r) : null;
                string? pnp = o["PNPDeviceID"]?.ToString();
                var gpu = new GpuInfo(name, vendor, pnp, null, ram, o["DriverVersion"]?.ToString(), null, o["VideoProcessor"]?.ToString(), null, null, null, null, pnp, vendor, false,
                    new Dictionary<string, string>());
                list.Add(gpu with { IsIntegrated = GpuVendorDetector.LooksIntegrated(gpu) });
            }
        }
        catch (Exception)
        {
            // WMI unavailable; the caller reports "unknown".
        }

        return list;
    }

    /// <summary>True when Virtualization-Based Security is running (Win32_DeviceGuard status 2), null when unknown.</summary>
    public static bool? VbsRunning()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    return o["VirtualizationBasedSecurityStatus"] is { } v && Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) == 2;
                }
            }
        }
        catch (Exception)
        {
            // Unknown.
        }

        return null;
    }

    public static (string? Name, string? Manufacturer) QueryCpu()
    {
        if (!PlatformGuard.IsWindows)
        {
            return (null, null);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer FROM Win32_Processor");
            foreach (var o in searcher.Get())
            {
                return (o["Name"]?.ToString()?.Trim(), o["Manufacturer"]?.ToString());
            }
        }
        catch (Exception)
        {
        }

        return (null, null);
    }

    public static IReadOnlyList<(int? ConfiguredMhz, int? RatedMhz, string? MemoryType, string? PartNumber)> QueryMemory()
    {
        var list = new List<(int?, int?, string?, string?)>();
        if (!PlatformGuard.IsWindows)
        {
            return list;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory");
            foreach (var o in searcher.Get())
            {
                int? speed = o["Speed"] is { } s ? Convert.ToInt32(s) : null;
                int? configured = o["ConfiguredClockSpeed"] is { } c ? Convert.ToInt32(c) : null;
                string? type = o["SMBIOSMemoryType"] is { } t ? Convert.ToInt32(t) switch { 34 => "DDR5", 26 => "DDR4", 24 => "DDR3", _ => null } : null;
                list.Add((configured, speed, type, (o["PartNumber"] as string)?.Trim()));
            }
        }
        catch (Exception)
        {
        }

        return list;
    }
}
