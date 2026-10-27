using System.Runtime.InteropServices;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>Pure power-source rules for starting a stress test.</summary>
public static class StressSafety
{
    /// <summary>Below this battery level a stress test is refused (a hard drain can shut a laptop off mid-test).</summary>
    public const int MinimumBatteryPercent = 20;

    /// <summary>
    /// On mains power (or unknown): run. On battery at or above 20 %: run with a warning. On battery below 20 % (or an
    /// unknown level): refuse.
    /// </summary>
    public static StressPreflight Evaluate(bool? onBattery, int? batteryPercent)
    {
        if (onBattery != true)
        {
            return StressPreflight.Ok with { BatteryPercent = batteryPercent };
        }

        if (batteryPercent is null or < MinimumBatteryPercent)
        {
            string level = batteryPercent is { } p ? $"{p} %" : "an unknown level";
            return new StressPreflight(false, true, batteryPercent,
                $"This laptop is on battery at {level}. Plug in the charger first: a stress test drains the battery very quickly and the PC could shut off mid-test.");
        }

        return new StressPreflight(true, true, batteryPercent,
            $"This laptop is on battery ({batteryPercent} %). A stress test drains it fast and runs slower than on the charger; plug in for meaningful results.");
    }
}

/// <summary>Reads the power source with <c>GetSystemPowerStatus</c>; any failure means "unknown" (treated as mains power).</summary>
public static class PowerStatusProbe
{
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

    /// <summary>(on battery, battery percent); nulls when Windows does not know or there is no battery.</summary>
    public static (bool? OnBattery, int? Percent) Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        try
        {
            if (!GetSystemPowerStatus(out var s))
            {
                return (null, null);
            }

            return Interpret(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>Pure decoding of the SYSTEM_POWER_STATUS bytes (255 = unknown, battery flag 128 = no battery).</summary>
    public static (bool? OnBattery, int? Percent) Interpret(byte acLineStatus, byte batteryFlag, byte batteryLifePercent)
    {
        if (batteryFlag == 128)
        {
            return (false, null);
        }

        bool? onBattery = acLineStatus switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
        int? percent = batteryLifePercent <= 100 ? batteryLifePercent : null;
        return (onBattery, percent);
    }
}
