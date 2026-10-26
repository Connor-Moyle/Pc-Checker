using System.Management;
using System.Runtime.InteropServices;
using PcChecker.Core.Common;

namespace PcChecker.Core.Power;

/// <summary>
/// Reads the battery through WMI (<c>root\WMI BatteryStatus</c> / <c>BatteryFullChargedCapacity</c>,
/// <c>Win32_Battery</c>) with <c>GetSystemPowerStatus</c> as a fallback. Every call is guarded: on a desktop or
/// when WMI is unhappy it returns <see cref="BatteryReading.None"/> instead of throwing.
/// </summary>
public static class WindowsBatteryReader
{
    public static BatteryReading Read()
    {
        if (!PlatformGuard.IsWindows)
        {
            return BatteryReading.None("Battery information is only available on Windows.");
        }

        var status = ReadSystemPowerStatus();
        if (status is { BatteryFlag: 128 })
        {
            // 128 = no system battery.
            return BatteryReading.None();
        }

        bool present = false, onBattery = false, charging = false;
        int? percent = null, remainingMwh = null, fullMwh = null;
        double? dischargeW = null, chargeW = null;
        string? note = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus");
            foreach (ManagementObject item in searcher.Get())
            {
                present = true;
                onBattery |= GetBool(item, "Discharging");
                charging |= GetBool(item, "Charging");
                double dischargeMw = GetDouble(item, "DischargeRate");
                double chargeMw = GetDouble(item, "ChargeRate");
                if (dischargeMw > 0)
                {
                    dischargeW = (dischargeW ?? 0) + dischargeMw / 1000.0;
                }

                if (chargeMw > 0)
                {
                    chargeW = (chargeW ?? 0) + chargeMw / 1000.0;
                }

                int remaining = (int)GetDouble(item, "RemainingCapacity");
                if (remaining > 0)
                {
                    remainingMwh = (remainingMwh ?? 0) + remaining;
                }
            }
        }
        catch (Exception ex)
        {
            note = "Detailed battery data is not available: " + ex.Message;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (ManagementObject item in searcher.Get())
            {
                int full = (int)GetDouble(item, "FullChargedCapacity");
                if (full > 0)
                {
                    fullMwh = (fullMwh ?? 0) + full;
                }
            }
        }
        catch
        {
            // Optional.
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (ManagementObject item in searcher.Get())
            {
                present = true;
                int p = (int)GetDouble(item, "EstimatedChargeRemaining");
                if (p is >= 0 and <= 100)
                {
                    percent = p;
                }

                // BatteryStatus 1 = discharging, 2 = on AC.
                int batteryStatus = (int)GetDouble(item, "BatteryStatus");
                onBattery |= batteryStatus == 1;
            }
        }
        catch
        {
            // Optional.
        }

        if (status is { } s)
        {
            present |= s.BatteryFlag != 128 && s.BatteryLifePercent != 255;
            onBattery |= s.ACLineStatus == 0;
            charging |= (s.BatteryFlag & 8) != 0;
            if (percent is null && s.BatteryLifePercent != 255)
            {
                percent = s.BatteryLifePercent;
            }
        }

        if (!present)
        {
            return BatteryReading.None(note);
        }

        if (percent is null && remainingMwh is { } r && fullMwh is { } f && f > 0)
        {
            percent = (int)Math.Clamp(Math.Round(100.0 * r / f), 0, 100);
        }

        var estimate = PowerMath.BatteryTimeRemaining(remainingMwh, dischargeW is { } d ? d * 1000 : null);
        if (estimate is null && status is { BatteryLifeTime: > 0 } s2 && onBattery)
        {
            estimate = TimeSpan.FromSeconds(s2.BatteryLifeTime);
        }

        return new BatteryReading(true, onBattery, charging, percent, dischargeW, chargeW, remainingMwh, fullMwh, estimate, note);
    }

    private static bool GetBool(ManagementBaseObject item, string name)
    {
        try
        {
            return item[name] is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static double GetDouble(ManagementBaseObject item, string name)
    {
        try
        {
            return item[name] is IConvertible c ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static SystemPowerStatus? ReadSystemPowerStatus()
    {
        try
        {
            return GetSystemPowerStatus(out var status) ? status : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
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
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
