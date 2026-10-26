using System.Text.Json;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+O", HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift, "O", 0x4F)]
    [InlineData("ctrl + alt + f9", HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt, "F9", 0x78)]
    [InlineData("Win+Space", HotkeyModifierKeys.Win, "Space", 0x20)]
    [InlineData("Control+Shift+7", HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift, "7", 0x37)]
    [InlineData("Alt+NumPad5", HotkeyModifierKeys.Alt, "NumPad5", 0x65)]
    [InlineData("Shift+Esc", HotkeyModifierKeys.Shift, "Esc", 0x1B)]
    [InlineData("F12", HotkeyModifierKeys.None, "F12", 0x7B)]
    public void TryParse_accepts_common_forms(string text, HotkeyModifierKeys modifiers, string key, uint vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out var error), error);
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(vk, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("O")]
    public void TryParse_rejects_invalid_text_with_a_reason(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture, out var error));
        Assert.Null(gesture);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ToString_is_canonical_regardless_of_input_order()
    {
        var gesture = HotkeyGesture.Parse("shift+win+ctrl+alt+k");
        Assert.Equal("Ctrl+Alt+Shift+Win+K", gesture.ToString());
        Assert.Equal(gesture, HotkeyGesture.Parse(gesture.ToString()));
    }

    [Fact]
    public void Parse_throws_format_exception_for_bad_text()
    {
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse("Ctrl+"));
    }

    [Fact]
    public void Modifier_values_match_win32_mod_constants()
    {
        Assert.Equal(1, (int)HotkeyModifierKeys.Alt);
        Assert.Equal(2, (int)HotkeyModifierKeys.Control);
        Assert.Equal(4, (int)HotkeyModifierKeys.Shift);
        Assert.Equal(8, (int)HotkeyModifierKeys.Win);
    }
}

public class OverlayMetricFormatterTests
{
    [Theory]
    [InlineData(OverlayMetricIds.CpuTemp, 58.4, "58 °C")]
    [InlineData(OverlayMetricIds.GpuLoad, 99.6, "100 %")]
    [InlineData(OverlayMetricIds.CpuClock, 5299.7, "5300 MHz")]
    [InlineData(OverlayMetricIds.GpuPower, 42.26, "42.3 W")]
    [InlineData(OverlayMetricIds.SystemPower, 245.4, "245 W")]
    [InlineData(OverlayMetricIds.RamUsed, 13.44, "13.4 GB")]
    [InlineData(OverlayMetricIds.RamPercent, 42.0, "42 %")]
    [InlineData(OverlayMetricIds.GpuVram, 840.0, "840 MB")]
    [InlineData(OverlayMetricIds.GpuVram, 2140.0, "2.1 GB")]
    [InlineData(OverlayMetricIds.NetDown, 0.05, "51 KB/s")]
    [InlineData(OverlayMetricIds.NetDown, 1.8, "1.80 MB/s")]
    [InlineData(OverlayMetricIds.NetUp, 112.34, "112.3 MB/s")]
    [InlineData(OverlayMetricIds.BatteryPercent, 73.0, "73 %")]
    public void Format_uses_metric_specific_units_and_decimals(string id, double value, string expected)
    {
        Assert.Equal(expected, OverlayMetricFormatter.Format(id, value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Format_shows_placeholder_for_missing_values(double? value)
    {
        Assert.Equal("--", OverlayMetricFormatter.Format(OverlayMetricIds.GpuTemp, value));
    }

    [Fact]
    public void Format_honours_unit_override()
    {
        Assert.Equal("1450 RPM", OverlayMetricFormatter.Format(OverlayMetricIds.GpuFan, 1450.3, "RPM"));
        Assert.Equal("55 %", OverlayMetricFormatter.Format(OverlayMetricIds.GpuFan, 55, "%"));
    }

    [Fact]
    public void Temperature_metrics_are_flagged()
    {
        Assert.True(OverlayMetricFormatter.IsTemperature(OverlayMetricIds.CpuTemp));
        Assert.True(OverlayMetricFormatter.IsTemperature(OverlayMetricIds.GpuHotspot));
        Assert.False(OverlayMetricFormatter.IsTemperature(OverlayMetricIds.CpuLoad));
    }

    [Fact]
    public void FormatTime_uses_24_hour_clock()
    {
        Assert.Equal("14:05:33", OverlayMetricFormatter.FormatTime(new DateTime(2026, 1, 2, 14, 5, 33)));
    }

    [Fact]
    public void Every_known_metric_has_a_unit_or_is_the_clock()
    {
        foreach (var (id, _) in OverlayMetricIds.All)
        {
            if (id == OverlayMetricIds.Time)
            {
                continue;
            }

            Assert.False(string.IsNullOrEmpty(OverlayMetricFormatter.Unit(id)), id);
        }
    }
}

public class OverlayLayoutTests
{
    private static readonly OverlayRect Area = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 16, 16)]
    [InlineData(OverlayCorner.TopRight, 1920 - 200 - 16, 16)]
    [InlineData(OverlayCorner.BottomLeft, 16, 1040 - 100 - 16)]
    [InlineData(OverlayCorner.BottomRight, 1920 - 200 - 16, 1040 - 100 - 16)]
    public void Place_puts_the_overlay_in_the_requested_corner(OverlayCorner corner, double x, double y)
    {
        var (px, py) = OverlayLayout.Place(corner, 0, 0, Area, 200, 100);
        Assert.Equal(x, px);
        Assert.Equal(y, py);
    }

    [Fact]
    public void Place_respects_monitor_offset_for_secondary_screens()
    {
        var second = new OverlayRect(1920, 0, 2560, 1400);
        var (x, y) = OverlayLayout.Place(OverlayCorner.BottomRight, 0, 0, second, 300, 80, margin: 10);
        Assert.Equal(1920 + 2560 - 300 - 10, x);
        Assert.Equal(1400 - 80 - 10, y);
    }

    [Fact]
    public void Custom_position_is_used_but_clamped_inside_the_work_area()
    {
        Assert.Equal((400, 300), OverlayLayout.Place(OverlayCorner.Custom, 400, 300, Area, 200, 100));
        Assert.Equal((1720, 940), OverlayLayout.Place(OverlayCorner.Custom, 5000, 5000, Area, 200, 100));
        Assert.Equal((0, 0), OverlayLayout.Place(OverlayCorner.Custom, -50, -50, Area, 200, 100));
    }

    [Fact]
    public void Clamp_prefers_top_left_when_the_overlay_is_larger_than_the_area()
    {
        var tiny = new OverlayRect(0, 0, 100, 100);
        Assert.Equal((0, 0), OverlayLayout.Clamp(40, 40, tiny, 500, 500));
    }

    [Theory]
    [InlineData(10, 10, OverlayCorner.TopLeft)]
    [InlineData(1700, 10, OverlayCorner.TopRight)]
    [InlineData(10, 900, OverlayCorner.BottomLeft)]
    [InlineData(1700, 900, OverlayCorner.BottomRight)]
    public void NearestCorner_classifies_by_centre(double x, double y, OverlayCorner expected)
    {
        Assert.Equal(expected, OverlayLayout.NearestCorner(x, y, Area, 200, 100));
    }
}

public class OverlayPresetCatalogTests
{
    [Fact]
    public void Built_in_presets_exist_and_normalize_cleanly()
    {
        var presets = OverlayPresetCatalog.BuiltIn();
        Assert.Equal(3, presets.Count);
        Assert.All(presets, p => Assert.True(OverlayPresetCatalog.IsBuiltIn(p.Name)));
        Assert.Equal(3, OverlayPresetCatalog.EnabledInOrder(presets[0].Metrics).Count);
        Assert.Contains(presets, p => p.Name == OverlayPresetCatalog.TempsOnly && OverlayPresetCatalog.EnabledInOrder(p.Metrics).All(m => OverlayMetricFormatter.IsTemperature(m.Id)));
        Assert.All(presets, p => Assert.Equal(OverlayMetricIds.All.Count, OverlayPresetCatalog.Normalize(p.Metrics).Count));
    }

    [Fact]
    public void Normalize_adds_missing_ids_drops_unknown_and_keeps_order()
    {
        var metrics = new List<OverlayMetric>
        {
            new() { Id = OverlayMetricIds.GpuTemp, Enabled = true, Label = "GPU", Order = 5 },
            new() { Id = "bogus.metric", Enabled = true, Order = 0 },
            new() { Id = OverlayMetricIds.CpuTemp, Enabled = true, Label = "  ", Order = 1 },
            new() { Id = OverlayMetricIds.CpuTemp, Enabled = false, Order = 9 },
        };

        var normalized = OverlayPresetCatalog.Normalize(metrics);
        Assert.Equal(OverlayMetricIds.All.Count, normalized.Count);
        Assert.Equal(OverlayMetricIds.CpuTemp, normalized[0].Id);
        Assert.Equal("CPU temp", normalized[0].Label);
        Assert.Equal(OverlayMetricIds.GpuTemp, normalized[1].Id);
        Assert.Equal("GPU", normalized[1].Label);
        Assert.Equal(Enumerable.Range(0, normalized.Count), normalized.Select(m => m.Order));
        Assert.Equal(2, normalized.Count(m => m.Enabled));
        Assert.DoesNotContain(normalized, m => m.Id == "bogus.metric");
    }

    [Fact]
    public void Capture_and_apply_round_trip_through_json()
    {
        var settings = new OverlaySettings { Corner = OverlayCorner.BottomRight, Opacity = 0.6, Scale = 1.5, Orientation = OverlayOrientation.Horizontal };
        settings.Metrics.First(m => m.Id == OverlayMetricIds.NetDown).Enabled = true;
        settings.Metrics.First(m => m.Id == OverlayMetricIds.NetDown).Label = "Down";

        var preset = OverlayPresetCatalog.Capture(settings, " Benchmarking ");
        Assert.Equal("Benchmarking", preset.Name);
        Assert.True(OverlayPresetCatalog.Save(settings, preset));

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<OverlaySettings>(json)!;
        var found = OverlayPresetCatalog.Find("benchmarking", restored.Presets);
        Assert.NotNull(found);

        var target = new OverlaySettings();
        OverlayPresetCatalog.Apply(found!, target);
        Assert.Equal(OverlayCorner.BottomRight, target.Corner);
        Assert.Equal(0.6, target.Opacity);
        Assert.Equal(1.5, target.Scale);
        Assert.Equal(OverlayOrientation.Horizontal, target.Orientation);
        Assert.Equal("Down", target.Metrics.First(m => m.Id == OverlayMetricIds.NetDown).Label);
        Assert.True(target.Metrics.First(m => m.Id == OverlayMetricIds.NetDown).Enabled);
        Assert.NotSame(found!.Metrics, target.Metrics);
    }

    [Fact]
    public void Save_rejects_built_in_names_and_replaces_duplicates()
    {
        var settings = new OverlaySettings();
        Assert.False(OverlayPresetCatalog.Save(settings, new OverlayPreset { Name = OverlayPresetCatalog.Full }));
        Assert.False(OverlayPresetCatalog.Save(settings, new OverlayPreset { Name = " " }));
        Assert.True(OverlayPresetCatalog.Save(settings, new OverlayPreset { Name = "Mine", Opacity = 0.5 }));
        Assert.True(OverlayPresetCatalog.Save(settings, new OverlayPreset { Name = "mine", Opacity = 0.9 }));
        Assert.Single(settings.Presets);
        Assert.Equal(0.9, settings.Presets[0].Opacity);
        Assert.True(OverlayPresetCatalog.Delete(settings, "MINE"));
        Assert.Empty(settings.Presets);
        Assert.False(OverlayPresetCatalog.Delete(settings, OverlayPresetCatalog.Minimal));
    }

    [Fact]
    public void Apply_clamps_out_of_range_values()
    {
        var target = new OverlaySettings();
        OverlayPresetCatalog.Apply(new OverlayPreset { Name = "x", Opacity = 5, Scale = 0.01 }, target);
        Assert.Equal(1.0, target.Opacity);
        Assert.Equal(0.5, target.Scale);
    }
}

public class OverlaySensorMatcherTests
{
    private static SensorReading R(string id, HardwareKind hw, SensorKind kind, string name, double? value, string unit = "") =>
        new(id, "hw", hw, kind, name, value, value, value, unit);

    private static SensorSnapshot Snapshot(params SensorReading[] readings) => new(DateTimeOffset.Now, readings);

    [Fact]
    public void Prefers_named_sensors_over_the_first_match()
    {
        var snapshot = Snapshot(
            R("c1", HardwareKind.Cpu, SensorKind.Temperature, "CPU Core #1", 50),
            R("c2", HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", 61),
            R("g1", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", 70),
            R("g2", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 55));

        Assert.Equal(61, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.CpuTemp).Value);
        Assert.Equal(55, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.GpuTemp).Value);
        Assert.Equal(70, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.GpuHotspot).Value);
    }

    [Fact]
    public void Falls_back_to_first_sensor_of_the_kind_and_reports_missing()
    {
        var snapshot = Snapshot(R("c1", HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", 48));
        Assert.Equal(48, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.CpuTemp).Value);
        Assert.False(OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.GpuTemp).HasValue);
        Assert.False(OverlaySensorMatcher.Resolve(null, OverlayMetricIds.CpuTemp).HasValue);
        Assert.False(OverlaySensorMatcher.Resolve(snapshot, "not.a.metric").HasValue);
    }

    [Fact]
    public void Cpu_clock_is_the_fastest_core_excluding_bus_speed()
    {
        var snapshot = Snapshot(
            R("b", HardwareKind.Cpu, SensorKind.Clock, "Bus Speed", 100),
            R("k1", HardwareKind.Cpu, SensorKind.Clock, "CPU Core #1", 4800),
            R("k2", HardwareKind.Cpu, SensorKind.Clock, "CPU Core #2", 5300));
        Assert.Equal(5300, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.CpuClock).Value);
    }

    [Fact]
    public void Vram_and_network_are_normalised_from_sensor_units()
    {
        var snapshot = Snapshot(
            R("v", HardwareKind.Gpu, SensorKind.Data, "GPU Memory Used", 2.5, "GB"),
            R("d1", HardwareKind.Network, SensorKind.Throughput, "Download Speed", 1048576, "B/s"),
            R("d2", HardwareKind.Network, SensorKind.Throughput, "Download Speed", 0.5, "MB/s"),
            R("u", HardwareKind.Network, SensorKind.Throughput, "Upload Speed", 512, "KB/s"));

        var vram = OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.GpuVram);
        Assert.Equal(2560, vram.Value);
        Assert.Equal("MB", vram.Unit);
        Assert.Equal(1.5, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.NetDown).Value);
        Assert.Equal(0.5, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.NetUp).Value);
    }

    [Fact]
    public void Gpu_fan_prefers_percent_control_over_rpm()
    {
        var withControl = Snapshot(
            R("f", HardwareKind.Gpu, SensorKind.Fan, "GPU Fan 1", 1500, "RPM"),
            R("c", HardwareKind.Gpu, SensorKind.Control, "GPU Fan 1", 45, "%"));
        var fan = OverlaySensorMatcher.Resolve(withControl, OverlayMetricIds.GpuFan);
        Assert.Equal(45, fan.Value);
        Assert.Equal("%", fan.Unit);

        var rpmOnly = Snapshot(R("f", HardwareKind.Gpu, SensorKind.Fan, "GPU Fan 1", 1500, "RPM"));
        var rpm = OverlaySensorMatcher.Resolve(rpmOnly, OverlayMetricIds.GpuFan);
        Assert.Equal(1500, rpm.Value);
        Assert.Equal("RPM", rpm.Unit);
    }

    [Fact]
    public void System_power_uses_psu_sensor_or_estimates_from_parts()
    {
        var psu = Snapshot(R("p", HardwareKind.Psu, SensorKind.Power, "Total Output", 320));
        Assert.Equal(320, OverlaySensorMatcher.Resolve(psu, OverlayMetricIds.SystemPower).Value);

        var parts = Snapshot(
            R("c", HardwareKind.Cpu, SensorKind.Power, "CPU Package", 100),
            R("g", HardwareKind.Gpu, SensorKind.Power, "GPU Package", 200));
        Assert.Equal(Math.Round(300 * 1.15 + 25, 0), OverlaySensorMatcher.Resolve(parts, OverlayMetricIds.SystemPower).Value);
    }

    [Fact]
    public void Demo_sensor_service_feeds_the_default_overlay_metrics()
    {
        using var demo = new DemoSensorService();
        foreach (var metric in OverlayPresetCatalog.EnabledInOrder(OverlayMetricIds.CreateDefaultMetrics()))
        {
            Assert.True(OverlaySensorMatcher.Resolve(demo.Latest, metric.Id).HasValue, metric.Id);
        }
    }
}
