using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Storage;

/// <summary>What a drive temperature sensor stands for.</summary>
public enum DriveSensorRole
{
    /// <summary>The drive's own temperature (NVMe "Composite Temperature", SATA "Temperature"). Shown as the headline.</summary>
    Drive,

    /// <summary>An extra sensor on the drive (NVMe sensor 1..8, a controller sensor). Listed, never the headline.</summary>
    Extra,

    /// <summary>A limit the drive reports (warning or critical threshold, lifetime maximum). Not a reading at all.</summary>
    Limit,
}

/// <summary>How a drive temperature sensor is labelled and treated.</summary>
/// <param name="Role">Headline, extra sensor or limit.</param>
/// <param name="Label">The name to show ("Temperature", "Sensor 2", "Controller").</param>
/// <param name="SensorNumber">The NVMe temperature sensor number (1..8) for extra sensors, otherwise null.</param>
public sealed record DriveSensorInfo(DriveSensorRole Role, string Label, int? SensorNumber = null);

/// <summary>One drive temperature from one source, for <see cref="DriveTemperatureRules.Choose"/>.</summary>
public enum DriveTemperatureSource
{
    /// <summary>LibreHardwareMonitor's composite (NVMe) or SMART (SATA) temperature.</summary>
    HardwareMonitor,

    /// <summary>ATA SMART attribute 194 or 190 from the Windows storage driver.</summary>
    Smart,

    /// <summary>MSFT_StorageReliabilityCounter.Temperature from Windows Storage Management.</summary>
    ReliabilityCounter,
}

/// <summary>
/// Pure rules for drive temperatures: which sensor is "the drive's temperature", how to decode the raw values the
/// different sources report, and which values are not temperatures at all.
/// <para>
/// Known traps this guards against, each of which can show about 70 °C on a cool drive:
/// LibreHardwareMonitor 0.9.6 reports the NVMe warning and critical thresholds as temperature sensors
/// ("Warning Temperature" is often 70 °C); NVMe "Temperature #2" is often a controller sensor that runs 20–30 °C
/// hotter than the composite; SMART 194 packs the lifetime maximum into the upper bytes of its raw value; and the
/// normalized value of SMART 190 is "100 − temperature" on many drives.
/// </para>
/// </summary>
public static partial class DriveTemperatureRules
{
    /// <summary>The label the headline drive temperature gets everywhere.</summary>
    public const string DriveLabel = "Temperature";

    /// <summary>Lowest believable drive temperature. 0 means "not reported" on every source.</summary>
    public const double MinPlausibleC = 1;

    /// <summary>Highest believable drive temperature. Drives shut down well before this; 255 and 65535 are "no data".</summary>
    public const double MaxPlausibleC = 125;

    /// <summary>
    /// How much hotter than the drive limits an extra sensor (controller, NAND sensor) may run before it is flagged.
    /// Controllers routinely sit 10–25 °C above the composite temperature, so the drive limits would flag them all day.
    /// </summary>
    public const double ExtraSensorMarginC = 15;

    /// <summary>Two sources that differ by at least this much disagree.</summary>
    public const double DisagreementC = 3;

    /// <summary>NVMe "Temperature" in the SMART / Health Information log page (02h): bytes 1–2, little-endian, Kelvin.</summary>
    public const int NvmeHealthCompositeOffset = 1;

    /// <summary>NVMe "Temperature Sensor 1" in the health log page: bytes 200–201; sensors 2..8 follow, 2 bytes each.</summary>
    public const int NvmeHealthSensor1Offset = 200;

    /// <summary>Warning Composite Temperature Threshold (WCTEMP) in Identify Controller: bytes 266–267, Kelvin.</summary>
    public const int NvmeIdentifyWarningOffset = 266;

    /// <summary>Critical Composite Temperature Threshold (CCTEMP) in Identify Controller: bytes 268–269, Kelvin.</summary>
    public const int NvmeIdentifyCriticalOffset = 268;

    /// <summary>NVMe reports whole Kelvin, so the offset is 273 (not 273.15): 309 K is exactly 36 °C.</summary>
    public const int KelvinOffset = 273;

    /// <summary>Classifies a drive temperature sensor by its name (LibreHardwareMonitor naming or our own).</summary>
    public static DriveSensorInfo Classify(string? sensorName)
    {
        var name = (sensorName ?? string.Empty).Trim();
        if (name.Length == 0
            || name.Equals("Temperature", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Composite Temperature", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Composite", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Drive Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return new DriveSensorInfo(DriveSensorRole.Drive, DriveLabel);
        }

        if (IsLimitName(name))
        {
            return new DriveSensorInfo(DriveSensorRole.Limit, name);
        }

        if (name.Contains("controller", StringComparison.OrdinalIgnoreCase))
        {
            return new DriveSensorInfo(DriveSensorRole.Extra, "Controller", NumberedSensor().Match(name) is { Success: true } c ? ParseNumber(c) : null);
        }

        if (NumberedSensor().Match(name) is { Success: true } m && ParseNumber(m) is { } n)
        {
            return new DriveSensorInfo(DriveSensorRole.Extra, "Sensor " + n.ToString(CultureInfo.InvariantCulture), n);
        }

        // Anything else (for example SATA "Airflow Temperature") is kept as an extra sensor under its own name.
        return new DriveSensorInfo(DriveSensorRole.Extra, name);
    }

    /// <summary>
    /// Turns a raw storage temperature sensor into what PC Checker shows: limits are dropped (null), the composite
    /// becomes "Temperature", numbered sensors become "Sensor N", and 0/255/65535 become "no value".
    /// </summary>
    public static (string Name, double? Value)? NormalizeSensor(string? sensorName, double? value)
    {
        var info = Classify(sensorName);
        return info.Role == DriveSensorRole.Limit ? null : (info.Label, Plausible(value));
    }

    /// <summary>True when the reading is a drive's headline temperature (a storage temperature classified as <see cref="DriveSensorRole.Drive"/>).</summary>
    public static bool IsDriveTemperature(SensorReading reading) =>
        reading is { Hardware: HardwareKind.Storage, Kind: SensorKind.Temperature } && Classify(reading.Name).Role == DriveSensorRole.Drive;

    /// <summary>True when the reading is an extra drive sensor (controller, sensor N) that has its own, higher limits.</summary>
    public static bool IsExtraDriveSensor(SensorReading reading) =>
        reading is { Hardware: HardwareKind.Storage, Kind: SensorKind.Temperature } && Classify(reading.Name).Role == DriveSensorRole.Extra;

    /// <summary>A believable drive temperature: not missing, not 0 ("not reported"), not 255/65535 ("no data").</summary>
    public static bool IsPlausible(double? celsius) =>
        celsius is { } c && !double.IsNaN(c) && c >= MinPlausibleC && c <= MaxPlausibleC;

    /// <summary>Returns the value when it is plausible, otherwise null.</summary>
    public static double? Plausible(double? celsius) => IsPlausible(celsius) ? celsius : null;

    /// <summary>
    /// The drive temperature of each drive in the readings: the headline (composite) sensor when the drive has one,
    /// otherwise its first plausible temperature. Extra sensors and limits never stand in for a drive that has a headline.
    /// </summary>
    public static IReadOnlyList<SensorReading> DriveTemperatures(IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var result = new List<SensorReading>();
        foreach (var drive in readings.Where(r => r is { Hardware: HardwareKind.Storage, Kind: SensorKind.Temperature })
                     .GroupBy(r => r.HardwareName, StringComparer.Ordinal))
        {
            var list = drive.ToList();
            var headline = list.Where(r => Classify(r.Name).Role == DriveSensorRole.Drive).ToList();
            if (headline.Count > 0)
            {
                // Two identical drives share a name: each contributes its own headline sensor.
                result.AddRange(headline.Where(r => IsPlausible(r.Value)));
                continue;
            }

            var fallback = list.FirstOrDefault(r => Classify(r.Name).Role == DriveSensorRole.Extra && IsPlausible(r.Value));
            if (fallback is not null)
            {
                result.Add(fallback);
            }
        }

        return result;
    }

    /// <summary>The hottest drive by its drive temperature (never a controller sensor or a threshold), or null.</summary>
    public static SensorReading? HottestDrive(IEnumerable<SensorReading> readings) =>
        DriveTemperatures(readings).MaxBy(r => r.Value);

    /// <summary>
    /// Warning and critical limits for a storage temperature reading: the drive limits from the settings for the drive
    /// temperature, the same limits plus <see cref="ExtraSensorMarginC"/> for extra sensors.
    /// </summary>
    public static (double Warn, double Crit) Thresholds(SensorReading reading, MonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(settings);
        return IsExtraDriveSensor(reading)
            ? (settings.DiskWarnC + ExtraSensorMarginC, settings.DiskCritC + ExtraSensorMarginC)
            : (settings.DiskWarnC, settings.DiskCritC);
    }

    /// <summary>Converts a whole-Kelvin NVMe value to °C. 0 K means "not reported" and returns null.</summary>
    public static double? KelvinToCelsius(int kelvin) => kelvin <= 0 ? null : Plausible(kelvin - KelvinOffset);

    /// <summary>Composite Temperature from an NVMe SMART / Health Information log page (bytes 1–2, Kelvin).</summary>
    public static double? NvmeCompositeFromHealthLog(ReadOnlySpan<byte> healthLog) =>
        healthLog.Length < NvmeHealthCompositeOffset + 2
            ? null
            : KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(healthLog.Slice(NvmeHealthCompositeOffset, 2)));

    /// <summary>Temperature Sensor <paramref name="number"/> (1..8) from an NVMe health log page; null when not implemented (0).</summary>
    public static double? NvmeSensorFromHealthLog(ReadOnlySpan<byte> healthLog, int number)
    {
        if (number is < 1 or > 8)
        {
            return null;
        }

        int offset = NvmeHealthSensor1Offset + (number - 1) * 2;
        return healthLog.Length < offset + 2 ? null : KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(healthLog.Slice(offset, 2)));
    }

    /// <summary>
    /// Warning (WCTEMP) and critical (CCTEMP) composite temperature thresholds from an NVMe Identify Controller
    /// structure. They are limits, never readings; 0 means the drive does not report one.
    /// </summary>
    public static (double? WarningC, double? CriticalC) NvmeThresholdsFromIdentify(ReadOnlySpan<byte> identifyController)
    {
        if (identifyController.Length < NvmeIdentifyCriticalOffset + 2)
        {
            return (null, null);
        }

        return (KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(identifyController.Slice(NvmeIdentifyWarningOffset, 2))),
                KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(identifyController.Slice(NvmeIdentifyCriticalOffset, 2))));
    }

    /// <summary>
    /// SMART 194 (Temperature): only the lowest byte of the 6-byte raw value is the current temperature. Many drives put
    /// the lifetime minimum and maximum in the higher bytes (0x00_2D_0014_0023 is 35 °C now, 20 °C min, 45 °C max).
    /// </summary>
    public static double? FromSmart194(long? raw) => raw is { } r ? Plausible(r & 0xFF) : null;

    /// <summary>
    /// SMART 190 (Airflow Temperature): the current temperature is the lowest raw byte. The normalized value is often
    /// "100 − temperature", so it is never used as degrees.
    /// </summary>
    public static double? FromSmart190(long? raw) => raw is { } r ? Plausible(r & 0xFF) : null;

    /// <summary>
    /// The drive temperature from ATA SMART attributes: 194 when it holds a plausible value, else 190. Normalized
    /// ("current"/"worst") values are never treated as degrees.
    /// </summary>
    public static double? FromSmartAttributes(IEnumerable<Health.SmartAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var list = attributes as IReadOnlyCollection<Health.SmartAttribute> ?? attributes.ToList();
        return list.Where(a => a.Id == 194).Select(a => FromSmart194(a.RawValue)).FirstOrDefault(v => v is not null)
            ?? list.Where(a => a.Id == 190).Select(a => FromSmart190(a.RawValue)).FirstOrDefault(v => v is not null);
    }

    /// <summary>
    /// MSFT_StorageReliabilityCounter: <c>Temperature</c> is the current value, <c>TemperatureMax</c> is a limit or
    /// lifetime peak and is never shown as the reading. 0 means "not reported".
    /// </summary>
    public static double? FromReliabilityCounter(double? temperature, double? temperatureMax = null)
    {
        _ = temperatureMax;
        return Plausible(temperature);
    }

    /// <summary>
    /// Picks the drive temperature from several sources. Implausible values are dropped. A value that equals one of
    /// the drive's reported limits (WCTEMP/CCTEMP) is dropped when another source disagrees with it, because it is the
    /// threshold read by mistake. Of what is left, LibreHardwareMonitor's composite reading wins, then SMART, then the
    /// Windows reliability counter.
    /// </summary>
    public static double? Choose(IEnumerable<(DriveTemperatureSource Source, double? Celsius)> candidates, params double?[] reportedLimits)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var values = candidates.Where(c => IsPlausible(c.Celsius)).Select(c => (c.Source, Value: c.Celsius!.Value)).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        var limits = reportedLimits.Where(l => IsPlausible(l)).Select(l => l!.Value).ToList();
        if (limits.Count > 0)
        {
            var notLimits = values.Where(v => !limits.Any(l => Math.Abs(l - v.Value) < 0.5)).ToList();
            bool someoneDisagrees = notLimits.Count > 0 && values.Any(v => notLimits.All(o => Math.Abs(o.Value - v.Value) >= DisagreementC));
            if (someoneDisagrees)
            {
                values = notLimits;
            }
        }

        return values.OrderBy(v => v.Source).First().Value;
    }

    private static bool IsLimitName(string name) =>
        name.Contains("warning", StringComparison.OrdinalIgnoreCase)
        || name.Contains("critical", StringComparison.OrdinalIgnoreCase)
        || name.Contains("threshold", StringComparison.OrdinalIgnoreCase)
        || name.Contains("limit", StringComparison.OrdinalIgnoreCase)
        || name.Contains("max", StringComparison.OrdinalIgnoreCase)
        || name.Contains("lifetime", StringComparison.OrdinalIgnoreCase);

    private static int? ParseNumber(Match match) =>
        int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 8 ? n : null;

    /// <summary>"Temperature #2", "Temperature 2", "Temperature Sensor 2", "Sensor 2".</summary>
    [GeneratedRegex(@"(?:temperature|sensor)\s*(?:sensor\s*)?#?\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedSensor();
}
