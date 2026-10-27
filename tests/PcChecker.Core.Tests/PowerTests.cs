using PcChecker.Core.Power;
using PcChecker.Core.Sensors;
using Xunit;

namespace PcChecker.Core.Tests;

public class PowerPlanParserTests
{
    private const string ListOutput = """
        Existing Power Schemes (* Active)
        -----------------------------------
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
        Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)
        Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)
        Power Scheme GUID: 11111111-2222-3333-4444-555555555555  (My custom (gaming) plan)
        """;

    [Fact]
    public void Parses_plans_and_active_marker()
    {
        var plans = PowerPlanParser.Parse(ListOutput);
        Assert.Equal(4, plans.Count);
        Assert.Equal("Balanced", plans[0].Name);
        Assert.True(plans[0].IsActive);
        Assert.Equal(PowerPlanParser.HighPerformance, plans[1].Id);
        Assert.False(plans[1].IsActive);
        Assert.Equal("Power saver", plans[2].Name);
        Assert.Single(plans, p => p.IsActive);
    }

    [Fact]
    public void Handles_empty_and_garbage_input()
    {
        Assert.Empty(PowerPlanParser.Parse(null));
        Assert.Empty(PowerPlanParser.Parse("nothing to see here"));
        Assert.Empty(PowerPlanParser.Parse("Power Scheme GUID: not-a-guid (Nope)"));
    }

    [Fact]
    public void Parses_active_scheme_and_describes_known_plans()
    {
        var active = PowerPlanParser.ParseActive("Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)");
        Assert.NotNull(active);
        Assert.True(active.IsActive);
        Assert.Equal(PowerPlanParser.HighPerformance, active.Id);
        Assert.Null(PowerPlanParser.ParseActive(string.Empty));
        Assert.Equal("Ultimate Performance", PowerPlanParser.DescribeKnown(PowerPlanParser.UltimatePerformance));
        Assert.Null(PowerPlanParser.DescribeKnown(Guid.NewGuid()));
        Assert.Contains("custom", PowerPlanParser.Explain(Guid.NewGuid()));
    }
}

public class PowerMathTests
{
    private static SensorReading Power(HardwareKind hw, string hardwareName, string name, double watts) =>
        new($"{hardwareName}/{name}", hardwareName, hw, SensorKind.Power, name, watts, watts, watts, "W");

    [Fact]
    public void Classifies_cpu_gpu_psu_and_other_readings()
    {
        Assert.Equal(PowerSource.CpuPackage, PowerMath.Classify(Power(HardwareKind.Cpu, "CPU", "CPU Package", 50)));
        Assert.Equal(PowerSource.Ignore, PowerMath.Classify(Power(HardwareKind.Cpu, "CPU", "CPU Cores", 40)));
        Assert.Equal(PowerSource.Gpu, PowerMath.Classify(Power(HardwareKind.Gpu, "GPU", "GPU Power", 120)));
        Assert.Equal(PowerSource.Gpu, PowerMath.Classify(Power(HardwareKind.Gpu, "GPU", "GPU Package", 120)));
        Assert.Equal(PowerSource.Ignore, PowerMath.Classify(Power(HardwareKind.Gpu, "GPU", "GPU Core", 90)));
        Assert.Equal(PowerSource.Psu, PowerMath.Classify(Power(HardwareKind.Psu, "PSU", "Power Out", 300)));
        Assert.Equal(PowerSource.Other, PowerMath.Classify(Power(HardwareKind.Storage, "NVMe", "Power", 4)));
        Assert.Equal(PowerSource.Ignore, PowerMath.Classify(Power(HardwareKind.Cpu, "CPU", "CPU Package", 50) with { Kind = SensorKind.Load }));
    }

    [Fact]
    public void Aggregates_without_double_counting()
    {
        var breakdown = PowerMath.Aggregate(
        [
            Power(HardwareKind.Cpu, "CPU", "CPU Package", 50),
            Power(HardwareKind.Cpu, "CPU", "CPU Cores", 40),
            Power(HardwareKind.Gpu, "GPU", "GPU Package", 120),
            Power(HardwareKind.Gpu, "GPU", "GPU Core", 90),
            Power(HardwareKind.Storage, "NVMe", "Power", 4),
            Power(HardwareKind.Memory, "RAM", "Power", 6),
            Power(HardwareKind.Cpu, "CPU", "Bogus", double.NaN),
        ]);

        Assert.Equal(50, breakdown.CpuPackageW);
        Assert.Equal(120, breakdown.GpuW);
        Assert.Equal(10, breakdown.OtherW);
        Assert.Null(breakdown.PsuW);
        Assert.True(breakdown.HasAnyReading);
        Assert.Equal(4, breakdown.Components.Count);
    }

    [Fact]
    public void Falls_back_to_largest_cpu_reading_without_package_sensor()
    {
        var breakdown = PowerMath.Aggregate(
        [
            Power(HardwareKind.Cpu, "CPU", "CPU Cores", 40),
            Power(HardwareKind.Cpu, "CPU", "CPU Graphics", 5),
        ]);
        Assert.Equal(40, breakdown.CpuPackageW);
        Assert.Contains(breakdown.Components, c => c.Source == PowerSource.CpuPackage);
    }

    [Fact]
    public void Estimates_total_or_uses_psu()
    {
        var (estimate, isEstimate) = PowerMath.EstimateTotal(50, 120, 10, null);
        Assert.True(isEstimate);
        Assert.Equal(Math.Round(180 * 1.15 + 25, 1), estimate);

        var (measured, measuredIsEstimate) = PowerMath.EstimateTotal(50, 120, 10, 310.4);
        Assert.False(measuredIsEstimate);
        Assert.Equal(310.4, measured);

        var (none, noneIsEstimate) = PowerMath.EstimateTotal(null, null, null, null);
        Assert.Null(none);
        Assert.True(noneIsEstimate);
    }

    [Fact]
    public void Integrates_energy_and_skips_sleep_gaps()
    {
        Assert.Equal(0.1, PowerMath.IntegrateEnergyWh(360, TimeSpan.FromSeconds(1)), 6);
        Assert.Equal(0, PowerMath.IntegrateEnergyWh(360, TimeSpan.FromMinutes(5)));
        Assert.Equal(0, PowerMath.IntegrateEnergyWh(null, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, PowerMath.IntegrateEnergyWh(100, TimeSpan.Zero));

        var session = new PowerSession(10, DateTimeOffset.UnixEpoch);
        var t = DateTimeOffset.UnixEpoch;
        session.Record(t, 200);
        session.Record(t.AddSeconds(1), 200);
        session.Record(t.AddSeconds(2), 400);
        Assert.Equal(200.0 / 3600 + 400.0 / 3600, session.EnergyWh, 6);
        Assert.Equal(3, session.History(TimeSpan.FromMinutes(1), t.AddSeconds(2)).Count);
        session.Reset(t.AddSeconds(3));
        Assert.Equal(0, session.EnergyWh);
        Assert.Equal(t.AddSeconds(3), session.Started);
    }

    [Fact]
    public void Power_history_follows_a_new_history_length()
    {
        var session = new PowerSession(SensorRingBuffer.CapacityFor(1, 1000), DateTimeOffset.UnixEpoch);
        var t = DateTimeOffset.UnixEpoch;
        for (int i = 0; i < 120; i++)
        {
            session.Record(t.AddSeconds(i), i);
        }

        // One minute at one sample a second keeps 60 samples; a longer history keeps the newest and then grows.
        Assert.Equal(60, session.HistoryCapacity);
        Assert.Equal(60, session.History(TimeSpan.FromHours(1), t.AddSeconds(119)).Count);
        session.ResizeHistory(SensorRingBuffer.CapacityFor(5, 1000));
        Assert.Equal(300, session.HistoryCapacity);
        var kept = session.History(TimeSpan.FromHours(1), t.AddSeconds(119));
        Assert.Equal(60, kept.Count);
        Assert.Equal(119, kept[^1].Value);
        for (int i = 120; i < 300; i++)
        {
            session.Record(t.AddSeconds(i), i);
        }

        Assert.Equal(240, session.History(TimeSpan.FromHours(1), t.AddSeconds(299)).Count);
    }

    [Fact]
    public void Costs_follow_price_and_draw()
    {
        Assert.Equal(0.09, PowerMath.CostPerHour(300, 0.30), 6);
        Assert.Equal(0.09 * 24, PowerMath.CostPerDay(300, 0.30), 6);
        Assert.Equal(0, PowerMath.CostPerHour(null, 0.30));
        Assert.Equal(0, PowerMath.CostPerHour(300, 0));
        Assert.Equal(0.15, PowerMath.SessionCost(500, 0.30), 6);
        Assert.Equal(0.5, PowerMath.Share(150, 300));
        Assert.Equal(0, PowerMath.Share(150, null));
    }

    [Fact]
    public void Battery_time_remaining()
    {
        var left = PowerMath.BatteryTimeRemaining(40_000, 20_000);
        Assert.NotNull(left);
        Assert.Equal(2, left.Value.TotalHours, 3);
        Assert.Null(PowerMath.BatteryTimeRemaining(40_000, 0));
        Assert.Null(PowerMath.BatteryTimeRemaining(null, 1000));
        Assert.Null(PowerMath.BatteryTimeRemaining(40_000, 1));
    }

    [Fact]
    public void Demo_power_follows_demo_sensors()
    {
        using var sensors = new DemoSensorService();
        using var power = new DemoPowerService(sensors);
        var snapshot = power.Latest;
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.CpuPackageW);
        Assert.NotNull(snapshot.GpuW);
        Assert.NotNull(snapshot.EstimatedTotalW);
        Assert.False(snapshot.OnBattery);
        Assert.True(((IPowerServiceExtras)power).IsEstimate);
        Assert.False(((IPowerServiceExtras)power).Battery.Present);
        Assert.True(power.EstimateCostPerHour(0.30) > 0);
    }

    [Fact]
    public async Task Demo_power_plans_switch()
    {
        using var power = new DemoPowerService();
        var plans = await power.GetPowerPlansAsync(CancellationToken.None);
        Assert.Equal(3, plans.Count);
        Assert.True(plans.Single(p => p.IsActive).Id == PowerPlanParser.Balanced);
        var result = await power.SetActivePowerPlanAsync(PowerPlanParser.HighPerformance, CancellationToken.None);
        Assert.True(result.Success);
        plans = await power.GetPowerPlansAsync(CancellationToken.None);
        Assert.Equal(PowerPlanParser.HighPerformance, plans.Single(p => p.IsActive).Id);
    }
}
