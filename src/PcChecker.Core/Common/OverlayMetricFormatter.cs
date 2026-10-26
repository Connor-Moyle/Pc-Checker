using System.Globalization;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Common;

/// <summary>Turns raw overlay metric values into short display strings ("58 °C", "2.1 GB", "--").</summary>
public static class OverlayMetricFormatter
{
    /// <summary>Shown when a metric has no sensor on this machine.</summary>
    public const string Placeholder = "--";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>True for metrics that are temperatures (threshold colouring applies).</summary>
    public static bool IsTemperature(string metricId) => metricId is OverlayMetricIds.CpuTemp or OverlayMetricIds.GpuTemp
        or OverlayMetricIds.GpuHotspot or OverlayMetricIds.DiskTemp;

    /// <summary>The unit the resolver is expected to deliver the value in.</summary>
    public static string Unit(string metricId) => metricId switch
    {
        OverlayMetricIds.CpuTemp or OverlayMetricIds.GpuTemp or OverlayMetricIds.GpuHotspot or OverlayMetricIds.DiskTemp => "°C",
        OverlayMetricIds.CpuLoad or OverlayMetricIds.GpuLoad or OverlayMetricIds.RamPercent or OverlayMetricIds.DiskActivity
            or OverlayMetricIds.GpuFan or OverlayMetricIds.BatteryPercent => "%",
        OverlayMetricIds.CpuClock or OverlayMetricIds.GpuClock or OverlayMetricIds.GpuMemClock => "MHz",
        OverlayMetricIds.CpuPower or OverlayMetricIds.GpuPower or OverlayMetricIds.SystemPower or OverlayMetricIds.BatteryWatts => "W",
        OverlayMetricIds.GpuVram => "MB",
        OverlayMetricIds.RamUsed => "GB",
        OverlayMetricIds.NetDown or OverlayMetricIds.NetUp => "MB/s",
        _ => string.Empty,
    };

    /// <summary>Formats a value for the given metric. Null, NaN or infinity becomes <see cref="Placeholder"/>.</summary>
    public static string Format(string metricId, double? value, string? unitOverride = null)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return Placeholder;
        }

        double v = value.Value;
        string unit = unitOverride ?? Unit(metricId);

        if (metricId == OverlayMetricIds.GpuVram && unit == "MB")
        {
            return v >= 1024 ? Number(v / 1024, 1) + " GB" : Number(v, 0) + " MB";
        }

        if (metricId is OverlayMetricIds.NetDown or OverlayMetricIds.NetUp && unit == "MB/s")
        {
            if (v < 0.1)
            {
                return Number(v * 1024, 0) + " KB/s";
            }

            return Number(v, v < 10 ? 2 : 1) + " MB/s";
        }

        int decimals = Decimals(metricId, v, unit);
        string text = Number(v, decimals);
        return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
    }

    /// <summary>Clock metric text, e.g. "14:05:33".</summary>
    public static string FormatTime(DateTime now) => now.ToString("HH:mm:ss", Culture);

    /// <summary>Decimal places for a metric (temperatures, loads and clocks are whole numbers; watts and GB get one).</summary>
    public static int Decimals(string metricId, double value, string? unit = null)
    {
        unit ??= Unit(metricId);
        return unit switch
        {
            "°C" or "%" or "MHz" or "RPM" => 0,
            "W" => value >= 100 ? 0 : 1,
            "GB" => 1,
            "MB" => 0,
            _ => 1,
        };
    }

    private static string Number(double value, int decimals) =>
        Math.Round(value, decimals).ToString("F" + decimals, Culture);
}
