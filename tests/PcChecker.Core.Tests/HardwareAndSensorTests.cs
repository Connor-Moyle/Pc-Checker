using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class WmiPropertyMapperTests
{
    [Fact]
    public void Converts_mixed_values_and_drops_nulls()
    {
        var bag = new Dictionary<string, object?>
        {
            ["Name"] = "  Intel  ",
            ["Cores"] = 8,
            ["Virtual"] = true,
            ["Nothing"] = null,
            ["Empty"] = "",
            ["Ids"] = new[] { "PCI\\A", "PCI\\B" },
            ["Install"] = "20240115093000.000000+060",
            ["Ratio"] = 1.5,
            ["Bytes"] = new byte[] { 0xAB, 0xCD },
        };
        var result = WmiPropertyMapper.ToStringDictionary(bag);
        Assert.Equal("Intel", result["Name"]);
        Assert.Equal("8", result["Cores"]);
        Assert.Equal("Yes", result["Virtual"]);
        Assert.False(result.ContainsKey("Nothing"));
        Assert.False(result.ContainsKey("Empty"));
        Assert.Equal("PCI\\A, PCI\\B", result["Ids"]);
        Assert.StartsWith("2024-01-15", result["Install"]);
        Assert.Equal("1.5", result["Ratio"]);
        Assert.Equal("ABCD", result["Bytes"]);
    }

    [Fact]
    public void Dmtf_dates_parse_with_offset()
    {
        Assert.True(WmiPropertyMapper.TryParseDmtfDate("20240115093000.000000+000", out var dt));
        Assert.Equal(2024, dt.Year);
        Assert.Equal(15, dt.Day);
        Assert.True(WmiPropertyMapper.TryParseDmtfDate("20240115093000.000000***", out _));
        Assert.False(WmiPropertyMapper.TryParseDmtfDate("not a date", out _));
        Assert.False(WmiPropertyMapper.TryParseDmtfDate(null, out _));
    }

    [Fact]
    public void Typed_getters_coerce()
    {
        var bag = new Dictionary<string, object?> { ["Size"] = "1024", ["Big"] = ulong.MaxValue, ["Flag"] = "True", ["Num"] = (uint)7, ["When"] = "20200101000000.000000+000", ["List"] = "single" };
        Assert.Equal(1024, WmiPropertyMapper.GetInt64(bag, "Size"));
        Assert.Equal(long.MaxValue, WmiPropertyMapper.GetInt64(bag, "Big"));
        Assert.Equal(7, WmiPropertyMapper.GetInt32(bag, "Num"));
        Assert.True(WmiPropertyMapper.GetBool(bag, "Flag"));
        Assert.Null(WmiPropertyMapper.GetBool(bag, "Missing"));
        Assert.Equal(2020, WmiPropertyMapper.GetDate(bag, "When")!.Value.Year);
        Assert.Equal(["single"], WmiPropertyMapper.GetStringList(bag, "List"));
        Assert.Empty(WmiPropertyMapper.GetStringList(bag, "Missing"));
        Assert.Null(WmiPropertyMapper.GetString(bag, "Missing"));
    }

    [Fact]
    public void Redact_keeps_prefix_and_masks_rest()
    {
        Assert.Equal("AA:BB:**:**:**:**", WmiPropertyMapper.Redact("AA:BB:CC:DD:EE:FF", 5));
        Assert.Equal("S1234********", WmiPropertyMapper.Redact("S1234ABCDEFGH", 5));
        Assert.Equal("***", WmiPropertyMapper.Redact("abc", 4));
    }
}

public class SensorMathTests
{
    [Fact]
    public void Summarize_computes_stats_and_ignores_nan()
    {
        var s = SensorMath.Summarize([40.0, 50.0, double.NaN, 60.0]);
        Assert.NotNull(s);
        Assert.Equal(40, s.Min);
        Assert.Equal(60, s.Max);
        Assert.Equal(50, s.Average);
        Assert.Equal(60, s.Last);
        Assert.Equal(3, s.Count);
        Assert.Null(SensorMath.Summarize(Array.Empty<double>()));
    }

    [Fact]
    public void Window_keeps_recent_samples()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new[] { (now.AddMinutes(-10), 1.0), (now.AddMinutes(-2), 2.0), (now, 3.0) };
        var window = SensorMath.Window(history, TimeSpan.FromMinutes(5), now);
        Assert.Equal(2, window.Count);
        Assert.Equal(2.0, window[0].Value);
    }

    [Fact]
    public void Downsample_averages_buckets()
    {
        var values = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
        var down = SensorMath.Downsample(values, 10);
        Assert.Equal(10, down.Count);
        Assert.Equal(4.5, down[0]);
        Assert.Equal(94.5, down[9]);
        Assert.Same(values, SensorMath.Downsample(values, 200));
    }

    [Fact]
    public void Evaluate_thresholds()
    {
        Assert.Null(SensorMath.Evaluate(70, 85, 95));
        Assert.Equal(Severity.High, SensorMath.Evaluate(86, 85, 95));
        Assert.Equal(Severity.Critical, SensorMath.Evaluate(95, 85, 95));
        Assert.Null(SensorMath.Evaluate(double.NaN, 85, 95));
    }

    [Fact]
    public void EvaluateReading_uses_settings_per_hardware()
    {
        var settings = new MonitoringSettings { GpuWarnC = 80, GpuCritC = 90 };
        var now = DateTimeOffset.UtcNow;
        var hot = new SensorReading("gpu/temp", "GPU", HardwareKind.Gpu, SensorKind.Temperature, "Core", 91, 40, 91, "°C");
        var alert = SensorMath.EvaluateReading(hot, settings, now);
        Assert.NotNull(alert);
        Assert.Equal(Severity.Critical, alert.Severity);
        Assert.Equal(90, alert.Threshold);
        Assert.Null(SensorMath.EvaluateReading(hot with { Value = 50 }, settings, now));
        Assert.Null(SensorMath.EvaluateReading(hot with { Kind = SensorKind.Load }, settings, now));
        Assert.Null(SensorMath.EvaluateReading(hot with { Hardware = HardwareKind.Network }, settings, now));
    }

    [Fact]
    public void HeatFraction_and_units()
    {
        Assert.Equal(0, SensorMath.HeatFraction(20, 90));
        Assert.Equal(1, SensorMath.HeatFraction(95, 90));
        Assert.Equal(0.5, SensorMath.HeatFraction(60, 90), 3);
        Assert.Equal("°C", SensorMath.UnitFor(SensorKind.Temperature));
        Assert.Equal("RPM", SensorMath.UnitFor(SensorKind.Fan));
        Assert.Equal(HardwareKind.Gpu, SensorMath.KindFromTypeName("GpuNvidia"));
        Assert.Equal(HardwareKind.Other, SensorMath.KindFromTypeName(null));
    }
}
