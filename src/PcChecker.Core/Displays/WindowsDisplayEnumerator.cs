using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static PcChecker.Core.Displays.DisplayNativeMethods;

namespace PcChecker.Core.Displays;

/// <summary>What DisplayConfig knows about one active monitor path (keyed by the GDI name <c>\\.\DISPLAYn</c>).</summary>
internal sealed record DisplayTarget(string GdiName, Luid Adapter, uint TargetId, uint OutputTechnology, string FriendlyName,
    string ManufacturerCode, string DevicePath, bool HdrSupported, bool HdrEnabled);

/// <summary>What WMI (root\WMI) knows about one monitor, keyed by its normalized instance id.</summary>
internal sealed record WmiMonitor(string InstanceName, string? FriendlyName, string? Manufacturer, string? Serial, int WidthCm, int HeightCm, int? Brightness);

/// <summary>Brightness/contrast read over DDC/CI, scaled to 0..100.</summary>
internal sealed record DdcReading(bool Supported, int? Brightness, int? Contrast);

/// <summary>
/// Gathers everything the Displays page shows from GDI, DisplayConfig, WMI and DDC/CI. Every source is optional:
/// a failing one adds a warning and the rest still fills in.
/// </summary>
internal static class WindowsDisplayEnumerator
{
    private static readonly TimeSpan DdcTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Attached GDI display devices (adapters that currently show part of the desktop).</summary>
    public static IReadOnlyList<(DisplayDevice Adapter, DisplayDevice? Monitor, DevMode Current)> EnumerateGdi()
    {
        var list = new List<(DisplayDevice, DisplayDevice?, DevMode)>();
        for (uint i = 0; i < 32; i++)
        {
            var adapter = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, i, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0 || (adapter.StateFlags & DisplayDeviceMirroringDriver) != 0)
            {
                continue;
            }

            var monitor = DisplayDevice.Create();
            DisplayDevice? monitorOrNull = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EddGetDeviceInterfaceName) ? monitor : null;

            var current = DevMode.Create();
            if (!EnumDisplaySettingsEx(adapter.DeviceName, EnumCurrentSettings, ref current, 0))
            {
                continue;
            }

            list.Add((adapter, monitorOrNull, current));
        }

        return list;
    }

    /// <summary>All modes the driver lists for a GDI device, normalized (32-bit colour only).</summary>
    public static IReadOnlyList<DisplayMode> EnumerateModes(string deviceName)
    {
        var modes = new List<DisplayMode>();
        for (int i = 0; i < 4096; i++)
        {
            var dm = DevMode.Create();
            if (!EnumDisplaySettingsEx(deviceName, i, ref dm, 0))
            {
                break;
            }

            if (dm.dmBitsPerPel >= 32)
            {
                modes.Add(new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, (int)dm.dmBitsPerPel));
            }
        }

        return DisplayModeMath.Normalize(modes);
    }

    /// <summary>HMONITOR handles keyed by GDI device name.</summary>
    public static Dictionary<string, IntPtr> EnumerateMonitorHandles()
    {
        var map = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        bool Callback(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data)
        {
            var info = MonitorInfoEx.Create();
            if (GetMonitorInfo(hMonitor, ref info) && !string.IsNullOrEmpty(info.szDevice))
            {
                map[info.szDevice] = hMonitor;
            }

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return map;
    }

    /// <summary>Effective DPI of a monitor as a Windows scale percent (100, 125, 150 …), or 100 when unknown.</summary>
    public static double ScalePercent(IntPtr hMonitor)
    {
        try
        {
            if (hMonitor != IntPtr.Zero && GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0)
            {
                return DisplayModeMath.ScaleFromDpi(dpiX);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // shcore is missing on very old systems; 100 % is the safe answer.
        }

        return 100;
    }

    /// <summary>Active DisplayConfig paths with friendly names, connection type and HDR state, keyed by GDI name.</summary>
    public static Dictionary<string, DisplayTarget> QueryTargets(ILogger logger, List<string> warnings)
    {
        var result = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int status = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount);
            if (status != 0)
            {
                warnings.Add($"Windows did not report the display configuration (error {status}); friendly names and HDR state may be missing.");
                return result;
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status != 0)
            {
                warnings.Add($"QueryDisplayConfig failed (error {status}); friendly names and HDR state may be missing.");
                return result;
            }

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = DisplayConfigSourceDeviceName.Create(path.sourceInfo.adapterId, path.sourceInfo.id);
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrEmpty(source.viewGdiDeviceName))
                {
                    continue;
                }

                var target = DisplayConfigTargetDeviceName.Create(path.targetInfo.adapterId, path.targetInfo.id);
                int targetStatus = DisplayConfigGetDeviceInfo(ref target);
                string friendly = targetStatus == 0 ? target.monitorFriendlyDeviceName ?? string.Empty : string.Empty;
                string code = targetStatus == 0 ? DisplayText.DecodeEdidManufacturer(target.edidManufactureId) : string.Empty;
                string path2 = targetStatus == 0 ? target.monitorDevicePath ?? string.Empty : string.Empty;
                uint technology = targetStatus == 0 ? target.outputTechnology : path.targetInfo.outputTechnology;

                bool hdrSupported = false, hdrEnabled = false;
                var color = DisplayConfigGetAdvancedColorInfo.Create(path.targetInfo.adapterId, path.targetInfo.id);
                if (DisplayConfigGetDeviceInfo(ref color) == 0)
                {
                    hdrSupported = color.Supported;
                    hdrEnabled = color.Enabled;
                }

                result[source.viewGdiDeviceName] = new DisplayTarget(source.viewGdiDeviceName, path.targetInfo.adapterId, path.targetInfo.id,
                    technology, friendly, code, path2, hdrSupported, hdrEnabled);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DisplayConfig query failed.");
            warnings.Add("Windows display configuration could not be read: " + ex.Message);
        }

        return result;
    }

    /// <summary>Monitor facts from WMI (root\WMI): EDID names, serials, physical size and built-in panel brightness.</summary>
    public static List<WmiMonitor> QueryWmi(ILogger logger, List<string> warnings)
    {
        var monitors = new Dictionary<string, WmiMonitor>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var ids = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, UserFriendlyName, ManufacturerName, SerialNumberID FROM WmiMonitorID");
            foreach (var mo in ids.Get())
            {
                string instance = mo["InstanceName"] as string ?? string.Empty;
                if (instance.Length == 0)
                {
                    continue;
                }

                monitors[DisplayVendorTools.NormalizeMonitorId(instance)] = new WmiMonitor(instance,
                    NullIfEmpty(DisplayText.WmiText(mo["UserFriendlyName"] as ushort[])),
                    NullIfEmpty(DisplayText.WmiText(mo["ManufacturerName"] as ushort[])),
                    NullIfEmpty(DisplayText.WmiText(mo["SerialNumberID"] as ushort[])), 0, 0, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WmiMonitorID unavailable.");
            warnings.Add("Monitor model and serial numbers could not be read from WMI.");
        }

        try
        {
            using var sizes = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams");
            foreach (var mo in sizes.Get())
            {
                string key = DisplayVendorTools.NormalizeMonitorId(mo["InstanceName"] as string);
                int w = Convert.ToInt32(mo["MaxHorizontalImageSize"] ?? 0);
                int h = Convert.ToInt32(mo["MaxVerticalImageSize"] ?? 0);
                monitors[key] = (monitors.TryGetValue(key, out var m) ? m : new WmiMonitor(key, null, null, null, 0, 0, null)) with { WidthCm = w, HeightCm = h };
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WmiMonitorBasicDisplayParams unavailable.");
        }

        try
        {
            using var brightness = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var mo in brightness.Get())
            {
                string key = DisplayVendorTools.NormalizeMonitorId(mo["InstanceName"] as string);
                int value = Convert.ToInt32(mo["CurrentBrightness"] ?? 0);
                monitors[key] = (monitors.TryGetValue(key, out var m) ? m : new WmiMonitor(key, null, null, null, 0, 0, null)) with { Brightness = value };
            }
        }
        catch (Exception)
        {
            // Only laptops/tablets expose WmiMonitorBrightness; external monitors legitimately throw "Not supported".
        }

        return monitors.Values.ToList();
    }

    /// <summary>
    /// Reads brightness and contrast over DDC/CI. Monitors that do not speak DDC/CI often answer slowly or not at all,
    /// so the call is bounded by a timeout and reports <c>Supported = false</c> rather than blocking the page.
    /// </summary>
    public static DdcReading ReadDdc(IntPtr hMonitor, ILogger logger)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return new DdcReading(false, null, null);
        }

        try
        {
            var task = Task.Run(() => ReadDdcCore(hMonitor));
            if (task.Wait(DdcTimeout))
            {
                return task.Result;
            }

            logger.LogDebug("DDC/CI query timed out for monitor {Handle}.", hMonitor);
            return new DdcReading(false, null, null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "DDC/CI query failed for monitor {Handle}.", hMonitor);
            return new DdcReading(false, null, null);
        }
    }

    private static DdcReading ReadDdcCore(IntPtr hMonitor)
    {
        var handles = GetPhysicalMonitors(hMonitor);
        if (handles.Length == 0)
        {
            return new DdcReading(false, null, null);
        }

        try
        {
            var h = handles[0].hPhysicalMonitor;
            int? brightness = null, contrast = null;
            bool any = false;
            if (GetMonitorBrightness(h, out uint bMin, out uint bCur, out uint bMax))
            {
                brightness = ToPercent(bMin, bCur, bMax);
                any = true;
            }

            if (GetMonitorContrast(h, out uint cMin, out uint cCur, out uint cMax))
            {
                contrast = ToPercent(cMin, cCur, cMax);
                any = true;
            }

            return new DdcReading(any, brightness, contrast);
        }
        finally
        {
            DestroyPhysicalMonitors((uint)handles.Length, handles);
        }
    }

    /// <summary>Writes brightness or contrast over DDC/CI, scaling 0..100 to the monitor's own range.</summary>
    public static bool WriteDdc(IntPtr hMonitor, bool contrast, int percent, out string error)
    {
        error = string.Empty;
        var handles = GetPhysicalMonitors(hMonitor);
        if (handles.Length == 0)
        {
            error = "The monitor did not expose a DDC/CI handle.";
            return false;
        }

        try
        {
            var h = handles[0].hPhysicalMonitor;
            bool ok;
            if (contrast)
            {
                ok = GetMonitorContrast(h, out uint min, out _, out uint max) && SetMonitorContrast(h, FromPercent(min, max, percent));
            }
            else
            {
                ok = GetMonitorBrightness(h, out uint min, out _, out uint max) && SetMonitorBrightness(h, FromPercent(min, max, percent));
            }

            if (!ok)
            {
                error = $"The monitor rejected the DDC/CI command (error {Marshal.GetLastWin32Error()}).";
            }

            return ok;
        }
        finally
        {
            DestroyPhysicalMonitors((uint)handles.Length, handles);
        }
    }

    private static PhysicalMonitor[] GetPhysicalMonitors(IntPtr hMonitor)
    {
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
        {
            return [];
        }

        var handles = new PhysicalMonitor[count];
        return GetPhysicalMonitorsFromHMONITOR(hMonitor, count, handles) ? handles : [];
    }

    internal static int ToPercent(uint min, uint current, uint max)
    {
        if (max <= min)
        {
            return (int)Math.Clamp(current, 0, 100);
        }

        return (int)Math.Round((current - (double)min) / (max - min) * 100);
    }

    internal static uint FromPercent(uint min, uint max, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (max <= min)
        {
            return (uint)percent;
        }

        return (uint)Math.Round(min + (max - min) * (percent / 100.0));
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
