using PcChecker.Core.Fans;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class FansTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", "fans-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
        }
    }

    // ---- FanCurveMath -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(20, 30)]   // below the first point → first point's percent
    [InlineData(30, 30)]
    [InlineData(35, 32.5)] // halfway between (30,30) and (40,35)
    [InlineData(80, 100)]
    [InlineData(95, 100)]  // above the last point → clamped
    public void Interpolate_is_linear_between_points_and_flat_outside(double temp, double expected)
    {
        var curve = FanCurveMath.Default("cpu", FanCurveMath.PresetBalanced);
        Assert.Equal(expected, FanCurveMath.Interpolate(curve.Points, temp), 3);
    }

    [Fact]
    public void Interpolate_handles_unsorted_points_and_empty_lists()
    {
        List<FanCurvePoint> unsorted = [new(80, 100), new(30, 20)];
        Assert.Equal(60, FanCurveMath.Interpolate(unsorted, 55), 3);
        Assert.Equal(50, FanCurveMath.Interpolate([], 55));
    }

    [Fact]
    public void Evaluate_clamps_to_0_100_without_history()
    {
        var curve = new FanCurve { TemperatureSensorId = "x", Points = [new(0, -20), new(100, 180)] };
        Assert.Equal(0, FanCurveMath.Evaluate(curve, 0, null));
        Assert.Equal(100, FanCurveMath.Evaluate(curve, 100, null));
    }

    [Fact]
    public void Evaluate_applies_hysteresis_before_slowing_down()
    {
        var curve = new FanCurve { TemperatureSensorId = "x", Points = [new(40, 40), new(80, 80)], HysteresisC = 5, RampStepPercent = 100 };
        // Running at 60 % (temp 60). Temperature drops to 57: with 5 °C hysteresis the curve at 62 °C is 62 % ≥ 60 → hold.
        Assert.Equal(60, FanCurveMath.Evaluate(curve, 57, 60));
        // Temperature drops to 50: curve at 55 °C is 55 % < 60 → allowed to fall to 55.
        Assert.Equal(55, FanCurveMath.Evaluate(curve, 50, 60));
        // Rising never waits for hysteresis.
        Assert.Equal(70, FanCurveMath.Evaluate(curve, 70, 60));
    }

    [Fact]
    public void Evaluate_limits_change_per_step()
    {
        var curve = new FanCurve { TemperatureSensorId = "x", Points = [new(30, 20), new(80, 100)], HysteresisC = 0, RampStepPercent = 5 };
        Assert.Equal(25, FanCurveMath.Evaluate(curve, 80, 20));
        Assert.Equal(95, FanCurveMath.Evaluate(curve, 30, 100));
        var noRamp = new FanCurve { TemperatureSensorId = "x", Points = curve.Points, HysteresisC = 0, RampStepPercent = 0 };
        Assert.Equal(21, FanCurveMath.Evaluate(noRamp, 80, 20)); // a step of 0 is treated as 1
    }

    [Fact]
    public void Normalize_sorts_clamps_and_dedupes()
    {
        var normalized = FanCurveMath.Normalize([new(90, 120), new(30, -5), new(30, 40), new(60, 50)]);
        Assert.Equal([30, 60, 90], normalized.Select(p => p.TemperatureC));
        Assert.Equal([40, 50, 100], normalized.Select(p => p.Percent));
        Assert.Equal(2, FanCurveMath.Normalize([]).Count);
        Assert.Equal(2, FanCurveMath.Normalize([new(50, 50)]).Count);
    }

    [Fact]
    public void Validate_reports_each_problem_in_plain_english()
    {
        Assert.NotNull(FanCurveMath.Validate(null));
        Assert.Contains("temperature", FanCurveMath.Validate(new FanCurve { Points = [new(30, 30), new(80, 100)] })!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two points", FanCurveMath.Validate(new FanCurve { TemperatureSensorId = "x", Points = [new(30, 30)] })!);
        Assert.Contains("Hysteresis", FanCurveMath.Validate(new FanCurve { TemperatureSensorId = "x", Points = [new(30, 30), new(80, 100)], HysteresisC = 50 })!);
        Assert.Null(FanCurveMath.Validate(FanCurveMath.Default("cpu", FanCurveMath.PresetSilent)));
    }

    [Fact]
    public void Built_in_profiles_are_well_formed_and_ordered_silent_to_performance()
    {
        var profiles = FanCurveMath.BuiltInProfiles("cpu.package", "gpu.core", ["fan.cpu", "fan.case"], ["fan.gpu"]);
        Assert.Equal(["Silent", "Balanced", "Performance"], profiles.Select(p => p.Name));
        foreach (var profile in profiles)
        {
            Assert.True(profile.IsBuiltIn);
            Assert.Equal(3, profile.Fans.Count);
            foreach (var (id, assignment) in profile.Fans)
            {
                Assert.Equal(FanMode.Curve, assignment.Mode);
                Assert.Null(FanCurveMath.Validate(assignment.Curve));
                Assert.Equal(id == "fan.gpu" ? "gpu.core" : "cpu.package", assignment.Curve!.TemperatureSensorId);
                var points = assignment.Curve.Points;
                Assert.Equal(points.OrderBy(p => p.TemperatureC), points);
                Assert.Equal(100, points[^1].Percent);
            }
        }

        // At the same temperature Silent runs slowest and Performance fastest.
        double At(FanProfile p) => FanCurveMath.Interpolate(p.Fans["fan.cpu"].Curve!.Points, 60);
        Assert.True(At(profiles[0]) < At(profiles[1]));
        Assert.True(At(profiles[1]) < At(profiles[2]));
    }

    [Fact]
    public void Clone_is_a_deep_copy()
    {
        var original = FanCurveMath.BuiltInProfiles("c", "g")[1];
        var copy = FanCurveMath.Clone(original);
        copy.Fans["fan.cpu"].Curve!.Points[0] = new FanCurvePoint(1, 1);
        copy.Fans["fan.cpu"].Curve!.HysteresisC = 9;
        Assert.NotEqual(1, original.Fans["fan.cpu"].Curve!.Points[0].TemperatureC);
        Assert.Equal(2, original.Fans["fan.cpu"].Curve!.HysteresisC);
    }

    // ---- FanPairing ---------------------------------------------------------------------------------------------

    private static FanSensorInfo Control(string hw, int index, string name, string hwName = "Nuvoton NCT6798D", HardwareKind kind = HardwareKind.Motherboard) =>
        new($"{hw}/control/{index}", hw, hwName, kind, SensorKind.Control, name, index, 50);

    private static FanSensorInfo Rpm(string hw, int index, string name, string hwName = "Nuvoton NCT6798D", HardwareKind kind = HardwareKind.Motherboard) =>
        new($"{hw}/fan/{index}", hw, hwName, kind, SensorKind.Fan, name, index, 900);

    [Fact]
    public void Pairing_matches_same_hardware_and_index_first()
    {
        var pairs = FanPairing.Pair([Control("/lpc/nct", 0, "Fan Control #1"), Control("/lpc/nct", 1, "Fan Control #2"), Rpm("/lpc/nct", 1, "Fan #2"), Rpm("/lpc/nct", 0, "Fan #1")]);
        Assert.Equal(2, pairs.Count);
        Assert.Equal("/lpc/nct/fan/0", pairs[0].Rpm!.Id);
        Assert.Equal("/lpc/nct/fan/1", pairs[1].Rpm!.Id);
        Assert.All(pairs, p => Assert.Equal("same hardware and index", p.Reason));
    }

    [Fact]
    public void Pairing_falls_back_to_names_then_order_and_never_crosses_hardware()
    {
        var sensors = new[]
        {
            Control("/lpc/nct", 3, "Fan Control #2"),
            Rpm("/lpc/nct", 7, "Fan #2"),
            Control("/lpc/nct", 5, "Fan Control #4"),
            Rpm("/lpc/nct", 9, "Fan #9"),
            Control("/gpu/nvidia/0", 0, "GPU Fan 1", "NVIDIA GeForce RTX 4070", HardwareKind.Gpu),
            Rpm("/gpu/nvidia/0", 0, "GPU Fan 1", "NVIDIA GeForce RTX 4070", HardwareKind.Gpu),
            Control("/lpc/nct", 6, "Fan Control #6"),
        };
        var pairs = FanPairing.Pair(sensors);
        Assert.Equal(4, pairs.Count);
        var byControl = pairs.ToDictionary(p => p.Control.Id);
        Assert.Equal("/lpc/nct/fan/7", byControl["/lpc/nct/control/3"].Rpm!.Id);
        Assert.Equal("same hardware and name", byControl["/lpc/nct/control/3"].Reason);
        Assert.Equal("/lpc/nct/fan/9", byControl["/lpc/nct/control/5"].Rpm!.Id);
        Assert.Equal("same hardware, by order", byControl["/lpc/nct/control/5"].Reason);
        Assert.Null(byControl["/lpc/nct/control/6"].Rpm);
        Assert.Equal("/gpu/nvidia/0/fan/0", byControl["/gpu/nvidia/0/control/0"].Rpm!.Id);
        Assert.Empty(FanPairing.Unpaired(sensors, pairs));
    }

    [Fact]
    public void Pairing_reports_rpm_only_fans_and_friendly_names()
    {
        var sensors = new[] { Control("/lpc/nct", 0, "Fan Control #1"), Rpm("/lpc/nct", 0, "Fan #1"), Rpm("/lpc/nct", 4, "Fan #5") };
        var pairs = FanPairing.Pair(sensors);
        var unpaired = Assert.Single(FanPairing.Unpaired(sensors, pairs));
        Assert.Equal("/lpc/nct/fan/4", unpaired.Id);
        Assert.Equal("CPU fan (header 1)", FanPairing.FriendlyName(pairs[0].Control, pairs[0].Rpm));
        Assert.Equal("Fan 3 (motherboard header)", FanPairing.FriendlyName(Control("/lpc/nct", 2, "Fan Control #3"), null));
        Assert.Equal("GPU fan 1", FanPairing.FriendlyName(Control("/gpu/nvidia/0", 0, "GPU Fan 1", "RTX", HardwareKind.Gpu), null));
        Assert.Equal("Pump", FanPairing.FriendlyName(Control("/cooler/0", 0, "Pump Control", "Corsair", HardwareKind.Cooler), null));
    }

    // ---- FanSessionStore ----------------------------------------------------------------------------------------

    [Fact]
    public void Session_file_round_trips_and_tolerates_garbage()
    {
        var path = FanSessionStore.DefaultPath(_dir);
        Assert.Null(FanSessionStore.TryRead(path));
        var session = new FanSession { ProcessId = 4242, ProfileName = "Quiet night", FanIds = ["/lpc/nct/control/0", "/gpu/nvidia/0/control/0"] };
        FanSessionStore.Write(path, session);
        var read = FanSessionStore.TryRead(path);
        Assert.NotNull(read);
        Assert.Equal(4242, read!.ProcessId);
        Assert.Equal("Quiet night", read.ProfileName);
        Assert.Equal(session.FanIds, read.FanIds);
        Assert.Equal(session.StartedUtc, read.StartedUtc);

        File.WriteAllText(path, "{ not json");
        Assert.Null(FanSessionStore.TryRead(path));
        Assert.Null(FanSessionStore.Deserialize(string.Empty));
        FanSessionStore.Delete(path);
        Assert.False(File.Exists(path));
        FanSessionStore.Delete(path); // deleting twice is fine
    }

    [Fact]
    public void Session_is_stale_only_when_its_process_is_gone()
    {
        var mine = new FanSession { ProcessId = Environment.ProcessId };
        Assert.False(FanSessionStore.IsStale(mine, _ => false));
        var other = new FanSession { ProcessId = 99999 };
        Assert.True(FanSessionStore.IsStale(other, _ => false));
        Assert.False(FanSessionStore.IsStale(other, _ => true));
        Assert.True(FanSessionStore.IsStale(new FanSession { ProcessId = 0 }, _ => true));
    }

    // ---- FanSafetyRules -----------------------------------------------------------------------------------------

    [Fact]
    public void Safety_finds_the_worst_critical_reading_and_releases_below_warning()
    {
        var monitoring = new MonitoringSettings { CpuWarnC = 85, CpuCritC = 95, GpuWarnC = 83, GpuCritC = 92, DiskCritC = 65 };
        SensorReading R(string id, HardwareKind hw, double v) => new(id, hw.ToString(), hw, SensorKind.Temperature, id, v, null, null, "°C");
        var calm = new[] { R("cpu", HardwareKind.Cpu, 60), R("gpu", HardwareKind.Gpu, 50), R("ssd", HardwareKind.Storage, 70) };
        Assert.Null(FanSafetyRules.FindCriticalReading(calm, monitoring)); // hot disks do not trigger the fan safety net
        Assert.True(FanSafetyRules.CanRelease(calm, monitoring));

        var hot = new[] { R("cpu", HardwareKind.Cpu, 96), R("gpu", HardwareKind.Gpu, 97) };
        Assert.Equal("gpu", FanSafetyRules.FindCriticalReading(hot, monitoring)!.Id); // 97 is 5 over, 96 is 1 over
        Assert.False(FanSafetyRules.CanRelease(new[] { R("cpu", HardwareKind.Cpu, 86) }, monitoring));
        Assert.True(FanSafetyRules.CanRelease(new[] { R("cpu", HardwareKind.Cpu, 84) }, monitoring));
    }

    [Theory]
    [InlineData(10, 20, false, 20)]
    [InlineData(10, 20, true, 10)]
    [InlineData(150, 20, false, 100)]
    [InlineData(double.NaN, 20, false, 20)]
    [InlineData(55.4, 20, false, 55)]
    public void Manual_percent_is_clamped_to_the_minimum_unless_allowed(double requested, double minimum, bool allow, double expected)
    {
        Assert.Equal(expected, FanSafetyRules.ClampManual(requested, minimum, allow));
    }

    // ---- Demo service -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Demo_service_applies_curves_profiles_and_reverts()
    {
        using var demo = new DemoFanControlService();
        var fans = await demo.GetFansAsync(CancellationToken.None);
        Assert.All(fans, f => Assert.Equal(FanMode.Auto, f.Mode));
        Assert.NotEmpty(demo.CapabilityNotes);
        Assert.Equal(4, demo.GetRawControls().Count);
        Assert.Contains(demo.GetTemperatureSources(), s => s.Id == "cpu.package");

        var below = await demo.SetManualAsync("fan.cpu", 5, false, CancellationToken.None);
        Assert.True(below.Success);
        Assert.Equal(20, (await demo.GetFansAsync(CancellationToken.None)).First(f => f.Id == "fan.cpu").Percent);
        Assert.Equal(["fan.cpu"], demo.SoftwareControlledFanIds);

        var curve = await demo.SetCurveAsync("fan.gpu", FanCurveMath.Default("gpu.core", FanCurveMath.PresetSilent), CancellationToken.None);
        Assert.True(curve.Success);
        Assert.Equal(FanMode.Curve, (await demo.GetFansAsync(CancellationToken.None)).First(f => f.Id == "fan.gpu").Mode);
        Assert.False((await demo.SetCurveAsync("fan.gpu", new FanCurve(), CancellationToken.None)).Success);

        Assert.True((await demo.ApplyProfileAsync(demo.BuiltInProfiles[2], CancellationToken.None)).Success);
        Assert.Equal("Performance", demo.ActiveProfile!.Name);
        Assert.False((await demo.ApplyProfileAsync(new FanProfile { Name = "Empty" }, CancellationToken.None)).Success);

        Assert.Empty(await demo.RunStartupAsync(CancellationToken.None));
        await demo.ShutdownAsync();
        Assert.Null(demo.ActiveProfile);
        Assert.Empty(demo.SoftwareControlledFanIds);
    }

    [Fact]
    public async Task Demo_service_honors_the_minimum_fan_speed_setting()
    {
        var settings = new FakeSettingsService();
        settings.Current.Fans.MinimumPercent = 35;
        using var demo = new DemoFanControlService(null, settings);

        Assert.True((await demo.SetManualAsync("fan.cpu", 10, false, CancellationToken.None)).Success);
        Assert.Equal(35, (await demo.GetFansAsync(CancellationToken.None)).First(f => f.Id == "fan.cpu").Percent);

        Assert.True((await demo.SetManualAsync("fan.cpu", 10, true, CancellationToken.None)).Success);
        Assert.Equal(10, (await demo.GetFansAsync(CancellationToken.None)).First(f => f.Id == "fan.cpu").Percent);

        settings.Current.Fans.MinimumPercent = 60;
        var profile = new FanProfile { Name = "Low" };
        profile.Fans["fan.case.front"] = new FanAssignment { Mode = FanMode.Manual, ManualPercent = 25 };
        Assert.True((await demo.ApplyProfileAsync(profile, CancellationToken.None)).Success);
        Assert.Equal(60, (await demo.GetFansAsync(CancellationToken.None)).First(f => f.Id == "fan.case.front").Percent);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public event EventHandler<AppSettings>? Changed;
        public Task LoadAsync() => Task.CompletedTask;

        public Task SaveAsync()
        {
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }
    }
}
