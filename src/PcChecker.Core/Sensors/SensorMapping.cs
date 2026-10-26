using LibreHardwareMonitor.Hardware;

namespace PcChecker.Core.Sensors;

/// <summary>
/// Pure mapping between LibreHardwareMonitor types and PC Checker's <see cref="HardwareKind"/>/<see cref="SensorKind"/>,
/// plus the unit text and value conversion each sensor type needs. No hardware access, so it is unit-testable anywhere.
/// </summary>
public static class SensorMapping
{
    private const double BytesPerMegabyte = 1024d * 1024d;

    /// <summary>Maps a LibreHardwareMonitor hardware type to the part of the PC it belongs to.</summary>
    public static HardwareKind ToHardwareKind(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
        HardwareType.Memory => HardwareKind.Memory,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => HardwareKind.Motherboard,
        HardwareType.Storage => HardwareKind.Storage,
        HardwareType.Network => HardwareKind.Network,
        HardwareType.Psu => HardwareKind.Psu,
        HardwareType.Cooler => HardwareKind.Cooler,
        HardwareType.Battery => HardwareKind.Battery,
        _ => HardwareKind.Other,
    };

    /// <summary>Maps a LibreHardwareMonitor sensor type to what PC Checker calls it.</summary>
    public static SensorKind ToSensorKind(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load => SensorKind.Load,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Power => SensorKind.Power,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Flow => SensorKind.Flow,
        SensorType.Control => SensorKind.Control,
        SensorType.Data or SensorType.SmallData => SensorKind.Data,
        SensorType.Throughput => SensorKind.Throughput,
        SensorType.Level or SensorType.Humidity => SensorKind.Level,
        SensorType.Factor => SensorKind.Factor,
        _ => SensorKind.Other,
    };

    /// <summary>Unit text for a LibreHardwareMonitor sensor type after <see cref="ConvertValue"/> has been applied.</summary>
    public static string UnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "MB/s",
        SensorType.Factor => "x",
        SensorType.Frequency => "Hz",
        SensorType.TimeSpan => "s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.Conductivity => "µS/cm",
        _ => string.Empty,
    };

    /// <summary>
    /// Converts a raw LibreHardwareMonitor value into the unit <see cref="UnitFor"/> reports (throughput B/s → MB/s).
    /// NaN and infinities become null so the UI shows "—" instead of garbage.
    /// </summary>
    public static double? ConvertValue(SensorType type, float? raw)
    {
        if (raw is null || float.IsNaN(raw.Value) || float.IsInfinity(raw.Value))
        {
            return null;
        }

        double value = raw.Value;
        return type == SensorType.Throughput ? value / BytesPerMegabyte : value;
    }

    /// <summary>
    /// A stable id for a sensor. LibreHardwareMonitor identifiers already include the hardware path
    /// (for example <c>/intelcpu/0/temperature/0</c>), so they stay the same across restarts of the app.
    /// </summary>
    public static string BuildId(string? hardwareIdentifier, string? sensorIdentifier)
    {
        var sensor = (sensorIdentifier ?? string.Empty).Trim();
        var hardware = (hardwareIdentifier ?? string.Empty).Trim();
        if (sensor.Length == 0)
        {
            return hardware;
        }

        return hardware.Length == 0 || sensor.StartsWith(hardware, StringComparison.OrdinalIgnoreCase)
            ? sensor
            : hardware + sensor;
    }

    /// <summary>Friendly name for a hardware kind, used for page group headers.</summary>
    public static string DisplayName(HardwareKind kind) => kind switch
    {
        HardwareKind.Cpu => "Processor",
        HardwareKind.Gpu => "Graphics card",
        HardwareKind.Memory => "Memory",
        HardwareKind.Motherboard => "Motherboard",
        HardwareKind.Storage => "Storage",
        HardwareKind.Network => "Network",
        HardwareKind.Psu => "Power supply",
        HardwareKind.Cooler => "Cooler",
        HardwareKind.Battery => "Battery",
        _ => "Other",
    };

    /// <summary>Friendly name for a sensor kind ("Temperature", "Fan speed", …).</summary>
    public static string DisplayName(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Temperature",
        SensorKind.Load => "Load",
        SensorKind.Clock => "Clock",
        SensorKind.Power => "Power",
        SensorKind.Voltage => "Voltage",
        SensorKind.Fan => "Fan speed",
        SensorKind.Flow => "Flow",
        SensorKind.Control => "Fan control",
        SensorKind.Data => "Data",
        SensorKind.Throughput => "Throughput",
        SensorKind.Level => "Level",
        SensorKind.Factor => "Factor",
        _ => "Other",
    };

    /// <summary>Order in which hardware groups are shown: the hot parts first.</summary>
    public static int GroupOrder(HardwareKind kind) => kind switch
    {
        HardwareKind.Cpu => 0,
        HardwareKind.Gpu => 1,
        HardwareKind.Motherboard => 2,
        HardwareKind.Storage => 3,
        HardwareKind.Memory => 4,
        HardwareKind.Cooler => 5,
        HardwareKind.Psu => 6,
        HardwareKind.Battery => 7,
        HardwareKind.Network => 8,
        _ => 9,
    };

    /// <summary>Formats a value with a sensible number of decimals for its unit.</summary>
    public static string FormatValue(double? value, string unit)
    {
        if (value is not { } v || double.IsNaN(v))
        {
            return "—";
        }

        string format = unit switch
        {
            "V" => "0.000",
            "A" or "x" or "GB" or "MB/s" or "L/h" => "0.0",
            "°C" => Math.Abs(v) < 100 ? "0.0" : "0",
            "W" => Math.Abs(v) < 10 ? "0.0" : "0",
            _ => "0",
        };
        return v.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
    }
}
