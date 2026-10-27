using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Reports;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// Drive temperatures: a cool SSD must never show 70 °C because a threshold, a controller sensor, a packed SMART
/// raw value or a normalized SMART value was read as its temperature.
/// </summary>
public class DriveTemperatureTests
{
    private const string Nvme = "Samsung SSD 990 PRO 2TB";
    private const string Hdd = "WDC WD40EZAZ-00SF3B0";

    // ---- NVMe health log and Identify Controller ----------------------------------------------------------------

    /// <summary>A 512-byte SMART / Health Information log: composite 309 K (36 °C), sensor 1 = 307 K, sensor 2 = 335 K.</summary>
    private static byte[] HealthLog(int compositeK = 309, int sensor1K = 307, int sensor2K = 335)
    {
        var log = new byte[512];
        log[0] = 0x00;                         // critical warning
        log[1] = (byte)(compositeK & 0xFF);    // composite temperature, little-endian Kelvin
        log[2] = (byte)(compositeK >> 8);
        log[3] = 100;                          // available spare
        log[4] = 10;                           // available spare threshold
        log[5] = 3;                            // percentage used
        log[200] = (byte)(sensor1K & 0xFF);
        log[201] = (byte)(sensor1K >> 8);
        log[202] = (byte)(sensor2K & 0xFF);
        log[203] = (byte)(sensor2K >> 8);
        return log;
    }

    /// <summary>A 4096-byte Identify Controller structure with WCTEMP 343 K (70 °C) and CCTEMP 353 K (80 °C).</summary>
    private static byte[] IdentifyController(int warningK = 343, int criticalK = 353)
    {
        var id = new byte[4096];
        id[266] = (byte)(warningK & 0xFF);
        id[267] = (byte)(warningK >> 8);
        id[268] = (byte)(criticalK & 0xFF);
        id[269] = (byte)(criticalK >> 8);
        return id;
    }

    [Fact]
    public void Nvme_composite_comes_from_health_log_bytes_1_and_2()
    {
        var log = HealthLog();
        Assert.Equal(0x35, log[1]);
        Assert.Equal(0x01, log[2]);
        Assert.Equal(36, DriveTemperatureRules.NvmeCompositeFromHealthLog(log));
    }

    [Fact]
    public void Nvme_thresholds_come_from_identify_controller_not_the_health_log()
    {
        var (warning, critical) = DriveTemperatureRules.NvmeThresholdsFromIdentify(IdentifyController());
        Assert.Equal(70, warning);
        Assert.Equal(80, critical);

        // The health log has no thresholds at 266..269: reading them there gives nothing, never 70 °C.
        var (fromLog, _) = DriveTemperatureRules.NvmeThresholdsFromIdentify(HealthLog());
        Assert.Null(fromLog);
    }

    [Fact]
    public void Nvme_extra_sensors_are_read_from_bytes_200_onwards()
    {
        var log = HealthLog();
        Assert.Equal(34, DriveTemperatureRules.NvmeSensorFromHealthLog(log, 1));
        Assert.Equal(62, DriveTemperatureRules.NvmeSensorFromHealthLog(log, 2));
        Assert.Null(DriveTemperatureRules.NvmeSensorFromHealthLog(log, 3)); // 0 = not implemented
        Assert.Null(DriveTemperatureRules.NvmeSensorFromHealthLog(log, 9));
    }

    [Theory]
    [InlineData(309, 36.0)]
    [InlineData(273, null)]   // 0 °C: "not reported" on every drive we know
    [InlineData(0, null)]
    [InlineData(65535, null)]
    public void Kelvin_is_converted_once_with_a_whole_offset(int kelvin, double? expected) =>
        Assert.Equal(expected, DriveTemperatureRules.KelvinToCelsius(kelvin));

    [Fact]
    public void Short_buffers_return_nothing()
    {
        Assert.Null(DriveTemperatureRules.NvmeCompositeFromHealthLog(new byte[2]));
        Assert.Equal((null, null), DriveTemperatureRules.NvmeThresholdsFromIdentify(new byte[100]));
    }

    // ---- SATA SMART 194 / 190 -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x0000_002D_0014_0023L, 35.0)] // current 35, min 20, max 45
    [InlineData(0x0000_0046_0012_0021L, 33.0)] // lifetime maximum 70 °C in byte 4 is not the reading
    [InlineData(0x0000_0000_0000_0024L, 36.0)]
    [InlineData(0L, null)]
    [InlineData(0xFFL, null)]
    public void Smart_194_uses_only_the_lowest_raw_byte(long raw, double? expected) =>
        Assert.Equal(expected, DriveTemperatureRules.FromSmart194(raw));

    [Fact]
    public void Smart_190_never_uses_the_normalized_value_as_degrees()
    {
        // Samsung SATA SSD: normalized 67 (= 100 - 33), raw 33.
        var samsung = new[] { new SmartAttribute(190, "Airflow Temperature", 67, 52, 0, 33, false) };
        Assert.Equal(33, DriveTemperatureRules.FromSmartAttributes(samsung));

        // Raw not reported: no temperature, not "100 - 67".
        var noRaw = new[] { new SmartAttribute(190, "Airflow Temperature", 67, 52, 0, 0, false) };
        Assert.Null(DriveTemperatureRules.FromSmartAttributes(noRaw));
    }

    [Fact]
    public void Smart_194_wins_over_190_whatever_the_order()
    {
        var attributes = new[]
        {
            new SmartAttribute(190, "Airflow Temperature", 30, 30, 45, 0x0000_0046_0014_0046L, false), // 70 in low byte
            new SmartAttribute(194, "Temperature", 114, 98, 0, 0x0000_002D_0014_0023L, false),
        };
        Assert.Equal(35, DriveTemperatureRules.FromSmartAttributes(attributes));
    }

    [Fact]
    public void Smart_blob_facts_read_the_packed_194_value()
    {
        var blob = SmartBlobParser.Build(
        [
            (190, 0x0032, 67, 52, 0x0000_0000_0000_0021L),
            (194, 0x0022, 114, 98, 0x0000_0046_0014_0023L),
        ]);
        var facts = SmartBlobParser.ExtractFacts(SmartBlobParser.Parse(blob));
        Assert.Equal(35, facts.TemperatureC);
    }

    // ---- WMI MSFT_StorageReliabilityCounter ---------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 70.0, null)]   // 0 = not reported; TemperatureMax is never the reading
    [InlineData(34.0, 70.0, 34.0)]
    [InlineData(255.0, 0.0, null)]
    [InlineData(65535.0, 0.0, null)]
    public void Reliability_counter_uses_temperature_only(double temperature, double max, double? expected) =>
        Assert.Equal(expected, DriveTemperatureRules.FromReliabilityCounter(temperature, max));

    // ---- Choosing between sources -------------------------------------------------------------------------------

    [Fact]
    public void A_value_equal_to_the_warning_threshold_loses_to_a_disagreeing_reading()
    {
        double? chosen = DriveTemperatureRules.Choose(
            [(DriveTemperatureSource.ReliabilityCounter, 70), (DriveTemperatureSource.Smart, 36)], 70, 80);
        Assert.Equal(36, chosen);
    }

    [Fact]
    public void Hardware_monitor_composite_is_preferred_and_zero_is_ignored()
    {
        double? chosen = DriveTemperatureRules.Choose(
        [
            (DriveTemperatureSource.ReliabilityCounter, 37),
            (DriveTemperatureSource.Smart, 0),
            (DriveTemperatureSource.HardwareMonitor, 36),
        ]);
        Assert.Equal(36, chosen);
        Assert.Null(DriveTemperatureRules.Choose([(DriveTemperatureSource.HardwareMonitor, 0), (DriveTemperatureSource.Smart, null)]));
    }

    [Fact]
    public void A_hot_drive_that_really_is_at_the_threshold_is_kept()
    {
        double? chosen = DriveTemperatureRules.Choose(
            [(DriveTemperatureSource.HardwareMonitor, 70), (DriveTemperatureSource.Smart, 69)], 70, 80);
        Assert.Equal(70, chosen);
    }

    [Fact]
    public void A_composite_reading_at_the_threshold_beats_a_stale_reliability_counter()
    {
        double? chosen = DriveTemperatureRules.Choose(
            [(DriveTemperatureSource.HardwareMonitor, 70), (DriveTemperatureSource.ReliabilityCounter, 41)], 70, 80);
        Assert.Equal(70, chosen);
    }

    // ---- LibreHardwareMonitor sensor names ----------------------------------------------------------------------

    [Theory]
    [InlineData("Composite Temperature", DriveSensorRole.Drive, "Temperature")]
    [InlineData("Temperature", DriveSensorRole.Drive, "Temperature")]
    [InlineData("Temperature #1", DriveSensorRole.Extra, "Sensor 1")]
    [InlineData("Temperature #2", DriveSensorRole.Extra, "Sensor 2")]
    [InlineData("Temperature 2", DriveSensorRole.Extra, "Sensor 2")]
    [InlineData("Temperature 2 (controller)", DriveSensorRole.Extra, "Controller")]
    [InlineData("Airflow Temperature", DriveSensorRole.Extra, "Airflow Temperature")]
    [InlineData("Warning Temperature", DriveSensorRole.Limit, "Warning Temperature")]
    [InlineData("Critical Temperature", DriveSensorRole.Limit, "Critical Temperature")]
    [InlineData("Maximum Temperature", DriveSensorRole.Limit, "Maximum Temperature")]
    public void Sensor_names_are_classified(string name, DriveSensorRole role, string label)
    {
        var info = DriveTemperatureRules.Classify(name);
        Assert.Equal(role, info.Role);
        Assert.Equal(label, info.Label);
    }

    [Fact]
    public void Lhm_096_nvme_sensors_are_normalized()
    {
        Assert.Equal(("Temperature", (double?)36), DriveTemperatureRules.NormalizeSensor("Composite Temperature", 36));
        Assert.Equal(("Sensor 2", (double?)62), DriveTemperatureRules.NormalizeSensor("Temperature #2", 62));
        Assert.Null(DriveTemperatureRules.NormalizeSensor("Warning Temperature", 70));
        Assert.Null(DriveTemperatureRules.NormalizeSensor("Critical Temperature", 80));
        Assert.Equal(("Temperature", (double?)null), DriveTemperatureRules.NormalizeSensor("Composite Temperature", 0));
    }

    // ---- Consumers: hottest drive, overlay, tray, report, alerts ------------------------------------------------

    private static SensorReading Temp(string id, string hardware, HardwareKind kind, string name, double? value) =>
        new(id, hardware, kind, SensorKind.Temperature, name, value, null, null, "°C");

    /// <summary>What a cool NVMe looked like before the fix: a 70 °C threshold and a 62 °C controller next to a 36 °C drive.</summary>
    private static List<SensorReading> CoolNvmeWithRawLhmNames() =>
    [
        Temp("/cpu/0/temperature/0", "AMD Ryzen 7 7800X3D", HardwareKind.Cpu, "Core (Tctl/Tdie)", 52),
        Temp("/nvme/0/temperature/0", Nvme, HardwareKind.Storage, "Composite Temperature", 36),
        Temp("/nvme/0/temperature/1", Nvme, HardwareKind.Storage, "Temperature #1", 34),
        Temp("/nvme/0/temperature/2", Nvme, HardwareKind.Storage, "Temperature #2", 62),
        Temp("/nvme/0/temperature/10", Nvme, HardwareKind.Storage, "Warning Temperature", 70),
        Temp("/nvme/0/temperature/11", Nvme, HardwareKind.Storage, "Critical Temperature", 80),
    ];

    [Fact]
    public void Hottest_drive_is_the_composite_not_a_threshold_or_controller()
    {
        var hottest = DriveTemperatureRules.HottestDrive(CoolNvmeWithRawLhmNames());
        Assert.NotNull(hottest);
        Assert.Equal(36, hottest!.Value);
    }

    [Fact]
    public void Hottest_drive_compares_drives_by_their_own_temperature()
    {
        var readings = CoolNvmeWithRawLhmNames();
        readings.Add(Temp("/hdd/1/temperature/0", Hdd, HardwareKind.Storage, "Temperature", 38));
        Assert.Equal(Hdd, DriveTemperatureRules.HottestDrive(readings)!.HardwareName);

        var snapshot = new SensorSnapshot(DateTimeOffset.Now, readings);
        Assert.Equal(38, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.DiskTemp).Value);
        Assert.Equal(38, SnapshotSummary.Summarize(snapshot).HottestDriveC);
    }

    [Fact]
    public void A_drive_without_a_composite_falls_back_to_its_first_plausible_sensor()
    {
        var readings = new List<SensorReading>
        {
            Temp("/nvme/0/temperature/1", Nvme, HardwareKind.Storage, "Sensor 1", 0),
            Temp("/nvme/0/temperature/2", Nvme, HardwareKind.Storage, "Sensor 2", 41),
        };
        Assert.Equal(41, DriveTemperatureRules.HottestDrive(readings)!.Value);
        Assert.Null(DriveTemperatureRules.HottestDrive([Temp("x", Nvme, HardwareKind.Storage, "Temperature", 0)]));
    }

    [Fact]
    public void Extra_drive_sensors_have_their_own_higher_limits()
    {
        var settings = new MonitoringSettings { DiskWarnC = 55, DiskCritC = 65 };
        var controller = Temp("c", Nvme, HardwareKind.Storage, "Controller", 62);
        var drive = Temp("d", Nvme, HardwareKind.Storage, "Temperature", 36);

        Assert.Equal((70.0, 80.0), SensorMath.ThresholdsFor(controller, settings)!.Value);
        Assert.Equal((55.0, 65.0), SensorMath.ThresholdsFor(drive, settings)!.Value);
        Assert.Null(SensorMath.EvaluateReading(controller, settings, DateTimeOffset.Now));

        var tracker = new AlertTracker();
        Assert.Null(tracker.Evaluate(controller, settings, DateTimeOffset.Now));
        Assert.NotNull(tracker.Evaluate(drive with { Value = 58 }, settings, DateTimeOffset.Now));
    }

    [Fact]
    public void Extra_sensor_limits_apply_on_top_of_thresholds_worked_out_elsewhere()
    {
        var settings = new MonitoringSettings { DiskWarnC = 55, DiskCritC = 65 };
        var controller = Temp("c", Nvme, HardwareKind.Storage, "Controller", 62);
        var drive = Temp("d", Nvme, HardwareKind.Storage, "Temperature", 36);
        var gpu = Temp("g", "GPU", HardwareKind.Gpu, "GPU Hot Spot", 80);

        // A role-based rule that only knows the hardware still ends with the higher limits for the controller sensor.
        Assert.Equal((70.0, 80.0), SensorMath.AdjustForSensor(controller, (55, 65), settings));
        Assert.Equal((55.0, 65.0), SensorMath.AdjustForSensor(drive, (55, 65), settings));
        Assert.Equal((95.0, 105.0), SensorMath.AdjustForSensor(gpu, (95, 105), settings));
        Assert.Null(SensorMath.AdjustForSensor(controller, null, settings));
    }

    [Fact]
    public void Cpu_and_gpu_limits_are_unchanged()
    {
        var settings = new MonitoringSettings();
        var cpu = Temp("cpu", "CPU", HardwareKind.Cpu, "CPU Package", 50);
        Assert.Equal(SensorMath.ThresholdsFor(HardwareKind.Cpu, settings), SensorMath.ThresholdsFor(cpu, settings));
    }

    [Fact]
    public void Demo_data_shows_a_cool_ssd_with_a_labelled_hotter_controller()
    {
        var snapshot = new DemoSensorService().Latest!;
        var nvme = snapshot.Readings.Where(r => r.HardwareName == DemoData.NvmeModel && r.Kind == SensorKind.Temperature).ToList();
        var drive = Assert.Single(nvme, r => r.Name == "Temperature");
        var controller = Assert.Single(nvme, r => r.Name == "Controller");
        Assert.InRange(drive.Value!.Value, 30, 40);
        Assert.InRange(controller.Value!.Value, 55, 65);
        Assert.True(DriveTemperatureRules.IsExtraDriveSensor(controller));
        Assert.NotEqual(controller.Id, DriveTemperatureRules.HottestDrive(snapshot.Readings)!.Id);
    }
}
