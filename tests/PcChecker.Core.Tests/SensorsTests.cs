using LibreHardwareMonitor.Hardware;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class SensorMappingTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, HardwareKind.Cpu)]
    [InlineData(HardwareType.GpuNvidia, HardwareKind.Gpu)]
    [InlineData(HardwareType.GpuAmd, HardwareKind.Gpu)]
    [InlineData(HardwareType.GpuIntel, HardwareKind.Gpu)]
    [InlineData(HardwareType.SuperIO, HardwareKind.Motherboard)]
    [InlineData(HardwareType.EmbeddedController, HardwareKind.Motherboard)]
    [InlineData(HardwareType.Storage, HardwareKind.Storage)]
    [InlineData(HardwareType.Psu, HardwareKind.Psu)]
    [InlineData(HardwareType.Battery, HardwareKind.Battery)]
    [InlineData(HardwareType.Cooler, HardwareKind.Cooler)]
    public void Hardware_types_map_to_kinds(HardwareType type, HardwareKind expected) =>
        Assert.Equal(expected, SensorMapping.ToHardwareKind(type));

    [Theory]
    [InlineData(SensorType.Temperature, SensorKind.Temperature, "°C")]
    [InlineData(SensorType.Fan, SensorKind.Fan, "RPM")]
    [InlineData(SensorType.Control, SensorKind.Control, "%")]
    [InlineData(SensorType.SmallData, SensorKind.Data, "MB")]
    [InlineData(SensorType.Data, SensorKind.Data, "GB")]
    [InlineData(SensorType.Throughput, SensorKind.Throughput, "MB/s")]
    [InlineData(SensorType.Energy, SensorKind.Other, "mWh")]
    [InlineData(SensorType.Current, SensorKind.Other, "A")]
    public void Sensor_types_map_to_kinds_and_units(SensorType type, SensorKind expectedKind, string expectedUnit)
    {
        Assert.Equal(expectedKind, SensorMapping.ToSensorKind(type));
        Assert.Equal(expectedUnit, SensorMapping.UnitFor(type));
    }

    [Fact]
    public void Values_are_converted_and_cleaned()
    {
        Assert.Null(SensorMapping.ConvertValue(SensorType.Temperature, null));
        Assert.Null(SensorMapping.ConvertValue(SensorType.Temperature, float.NaN));
        Assert.Null(SensorMapping.ConvertValue(SensorType.Load, float.PositiveInfinity));
        Assert.Equal(55.5, SensorMapping.ConvertValue(SensorType.Temperature, 55.5f)!.Value, 3);
        Assert.Equal(2.0, SensorMapping.ConvertValue(SensorType.Throughput, 2 * 1024 * 1024f)!.Value, 6);
    }

    [Fact]
    public void Ids_are_stable_and_not_doubled()
    {
        Assert.Equal("/intelcpu/0/temperature/0", SensorMapping.BuildId("/intelcpu/0", "/intelcpu/0/temperature/0"));
        Assert.Equal("/nvme/0/temperature/1", SensorMapping.BuildId("/nvme/0", "/temperature/1"));
        Assert.Equal("/hw", SensorMapping.BuildId("/hw", null));
        Assert.Equal("/x/y", SensorMapping.BuildId(null, "/x/y"));
    }

    [Fact]
    public void Formatting_picks_sensible_decimals()
    {
        Assert.Equal("—", SensorMapping.FormatValue(null, "°C"));
        Assert.Equal("1.208", SensorMapping.FormatValue(1.2081, "V"));
        Assert.Equal("820", SensorMapping.FormatValue(820.4, "RPM"));
        Assert.Equal("58.5", SensorMapping.FormatValue(58.46, "°C"));
        Assert.True(SensorMapping.GroupOrder(HardwareKind.Cpu) < SensorMapping.GroupOrder(HardwareKind.Storage));
    }
}

public class AlertTrackerTests
{
    private static readonly MonitoringSettings Settings = new() { CpuWarnC = 85, CpuCritC = 95 };

    private static SensorReading Cpu(double value) =>
        new("cpu/temp", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Package", value, value, value, "°C");

    [Fact]
    public void Alerts_once_when_crossing_and_again_after_rearm()
    {
        var tracker = new AlertTracker(rearmDeltaC: 3, repeatAfter: TimeSpan.FromMinutes(5));
        var t0 = DateTimeOffset.UtcNow;

        Assert.Null(tracker.Evaluate(Cpu(70), Settings, t0));
        var first = tracker.Evaluate(Cpu(86), Settings, t0.AddSeconds(1));
        Assert.NotNull(first);
        Assert.Equal(Severity.High, first.Severity);
        Assert.Equal(85, first.Threshold);

        // Hovering around the line must stay quiet.
        Assert.Null(tracker.Evaluate(Cpu(87), Settings, t0.AddSeconds(2)));
        Assert.Null(tracker.Evaluate(Cpu(84), Settings, t0.AddSeconds(3)));
        Assert.Null(tracker.Evaluate(Cpu(86), Settings, t0.AddSeconds(4)));
        Assert.True(tracker.IsActive("cpu/temp"));

        // Drop 3 °C below the threshold → re-armed.
        Assert.Null(tracker.Evaluate(Cpu(81), Settings, t0.AddSeconds(5)));
        Assert.False(tracker.IsActive("cpu/temp"));
        Assert.NotNull(tracker.Evaluate(Cpu(86), Settings, t0.AddSeconds(6)));
    }

    [Fact]
    public void Escalation_to_critical_alerts_immediately()
    {
        var tracker = new AlertTracker();
        var t0 = DateTimeOffset.UtcNow;
        Assert.NotNull(tracker.Evaluate(Cpu(86), Settings, t0));
        var critical = tracker.Evaluate(Cpu(96), Settings, t0.AddSeconds(1));
        Assert.NotNull(critical);
        Assert.Equal(Severity.Critical, critical.Severity);
        Assert.Equal(95, critical.Threshold);
        Assert.Null(tracker.Evaluate(Cpu(97), Settings, t0.AddSeconds(2)));

        // Back below critical − 3 but still warm: silent, then critical again alerts.
        Assert.Null(tracker.Evaluate(Cpu(90), Settings, t0.AddSeconds(3)));
        Assert.NotNull(tracker.Evaluate(Cpu(96), Settings, t0.AddSeconds(4)));
    }

    [Fact]
    public void Repeats_after_the_repeat_interval()
    {
        var tracker = new AlertTracker(repeatAfter: TimeSpan.FromMinutes(5));
        var t0 = DateTimeOffset.UtcNow;
        Assert.NotNull(tracker.Evaluate(Cpu(90), Settings, t0));
        Assert.Null(tracker.Evaluate(Cpu(90), Settings, t0.AddMinutes(4)));
        Assert.NotNull(tracker.Evaluate(Cpu(90), Settings, t0.AddMinutes(5)));
        tracker.Reset();
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void Ignores_non_temperature_and_unknown_hardware()
    {
        var tracker = new AlertTracker();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(tracker.Evaluate(Cpu(99) with { Kind = SensorKind.Load }, Settings, now));
        Assert.Null(tracker.Evaluate(Cpu(99) with { Hardware = HardwareKind.Network }, Settings, now));
        Assert.Null(tracker.Evaluate(Cpu(99) with { Value = null }, Settings, now));
        Assert.Null(tracker.Evaluate(Cpu(double.NaN), Settings, now));
    }
}

public class SensorRingBufferTests
{
    [Fact]
    public void Keeps_newest_samples_and_tracks_min_max()
    {
        var buffer = new SensorRingBuffer(3);
        var t = DateTimeOffset.UtcNow;
        buffer.Add(t, 10);
        buffer.Add(t.AddSeconds(1), 30);
        buffer.Add(t.AddSeconds(2), 20);
        buffer.Add(t.AddSeconds(3), 25);
        buffer.Add(t.AddSeconds(4), double.NaN);

        Assert.Equal(3, buffer.Count);
        Assert.Equal([30, 20, 25], buffer.All().Select(s => s.Value));
        Assert.Equal(10, buffer.Min);
        Assert.Equal(30, buffer.Max);
        Assert.Equal(25, buffer.Last);

        buffer.ResetMinMax();
        Assert.Equal(25, buffer.Min);
        Assert.Equal(25, buffer.Max);
    }

    [Fact]
    public void Window_and_resize_keep_recent_data()
    {
        var buffer = new SensorRingBuffer(10);
        var t = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            buffer.Add(t.AddSeconds(i), i);
        }

        var window = buffer.Window(TimeSpan.FromSeconds(3), t.AddSeconds(9));
        Assert.Equal([6, 7, 8, 9], window.Select(s => (int)s.Value));

        buffer.Resize(4);
        Assert.Equal(4, buffer.Capacity);
        Assert.Equal([6, 7, 8, 9], buffer.All().Select(s => (int)s.Value));
        buffer.Resize(8);
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void Capacity_follows_history_and_interval()
    {
        Assert.Equal(1800, SensorRingBuffer.CapacityFor(30, 1000));
        Assert.Equal(7200, SensorRingBuffer.CapacityFor(30, 250));
        Assert.Equal(60, SensorRingBuffer.CapacityFor(0, 100000));
    }
}

public class DemoSensorCatalogTests
{
    [Fact]
    public void Demo_sensors_cover_every_hardware_kind_and_pair_with_demo_fans()
    {
        var ids = DemoSensorCatalog.All.Select(s => s.Id).ToHashSet();
        Assert.Equal(DemoSensorCatalog.All.Count, ids.Count);
        foreach (var fan in DemoSensorCatalog.FanIds)
        {
            Assert.Contains(fan + ".rpm", ids);
            Assert.Contains(fan + ".control", ids);
        }

        var kinds = DemoSensorCatalog.All.Select(s => s.Hardware).ToHashSet();
        Assert.Contains(HardwareKind.Cpu, kinds);
        Assert.Contains(HardwareKind.Gpu, kinds);
        Assert.Contains(HardwareKind.Motherboard, kinds);
        Assert.Contains(HardwareKind.Storage, kinds);
        Assert.Contains(HardwareKind.Memory, kinds);
        Assert.Contains(DemoSensorCatalog.All, s => s.Kind == SensorKind.Voltage);
        Assert.Contains(DemoSensorCatalog.All, s => s.Kind == SensorKind.Power && s.Hardware == HardwareKind.Gpu);
    }

    [Fact]
    public void Demo_values_stay_realistic_and_below_thresholds()
    {
        var settings = new MonitoringSettings();
        foreach (var spec in DemoSensorCatalog.All)
        {
            for (int s = 0; s < 600; s += 37)
            {
                double value = spec.ValueAt(s, 1);
                Assert.True(value >= 0 || spec.Kind == SensorKind.Voltage, spec.Id);
                if (spec.Kind == SensorKind.Temperature && SensorMath.ThresholdsFor(spec.Hardware, settings) is { } t)
                {
                    Assert.True(value < t.Warn, $"{spec.Id} = {value}");
                }
            }
        }
    }

    [Fact]
    public void Demo_service_reports_min_max_and_warnings_contract()
    {
        using var svc = new DemoSensorService();
        var reading = svc.Latest!.Find("cpu.package")!;
        Assert.Equal("°C", reading.Unit);
        Assert.NotNull(reading.Min);
        Assert.NotNull(reading.Max);
        Assert.Empty(((ISensorServiceExtras)svc).Warnings);
        Assert.Contains("fan.cpu.control", svc.SensorIds);
        svc.ResetMinMax();
        Assert.True(svc.GetTemperatures().Count > 10);
    }
}
