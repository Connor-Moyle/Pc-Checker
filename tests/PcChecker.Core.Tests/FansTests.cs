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
    public void Pairing_falls_back_to_unique_names_never_guesses_by_order_and_never_crosses_hardware()
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

        // Index 5 vs 9 and "#4" vs "#9": no confident match, so no pair (the old "by order" guess is gone).
        Assert.Null(byControl["/lpc/nct/control/5"].Rpm);
        Assert.Equal(FanPairing.NoConfidentMatch, byControl["/lpc/nct/control/5"].Reason);
        Assert.Null(byControl["/lpc/nct/control/6"].Rpm);
        Assert.Equal("/gpu/nvidia/0/fan/0", byControl["/gpu/nvidia/0/control/0"].Rpm!.Id);
        Assert.Equal("/lpc/nct/fan/9", Assert.Single(FanPairing.Unpaired(sensors, pairs)).Id);
    }

    [Fact]
    public void Pairing_by_index_matches_the_chip_channel_even_when_the_names_differ()
    {
        // LibreHardwareMonitor names some boards' fans "System Fan"/"CPU Fan" and their controls "Fan 1"/"Fan 2"; both
        // use the chip channel as index, which is what really links them.
        var sensors = new[]
        {
            Rpm("/lpc/ite", 0, "System Fan"), Rpm("/lpc/ite", 1, "CPU Fan"), Rpm("/lpc/ite", 2, "Auxiliary Fan"),
            Control("/lpc/ite", 0, "Fan 1"), Control("/lpc/ite", 1, "Fan 2"),
        };
        var pairs = FanPairing.Pair(sensors);
        Assert.Equal(["System Fan", "CPU Fan"], pairs.Select(p => p.Rpm!.Name));
        Assert.Equal("Auxiliary Fan", Assert.Single(FanPairing.Unpaired(sensors, pairs)).Name);
    }

    [Fact]
    public void Pairing_refuses_ambiguous_indexes_and_names()
    {
        // Two RPM sensors on the same index, and two controls sharing the name key: nothing is certain, nothing is paired.
        var sensors = new[]
        {
            Control("/lpc/nct", 0, "Fan Control #1"), Rpm("/lpc/nct", 0, "Fan #1"),
            new FanSensorInfo("/lpc/nct/fan/0b", "/lpc/nct", "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Fan, "Fan #1", 0, 700),
            Control("/lpc/nct", 4, "Pump"), Control("/lpc/nct", 5, "Pump"), Rpm("/lpc/nct", 8, "Pump"),
        };
        var pairs = FanPairing.Pair(sensors);
        Assert.All(pairs, p => Assert.Null(p.Rpm));
        Assert.All(pairs, p => Assert.Equal(FanPairing.NoConfidentMatch, p.Reason));
        Assert.Equal(3, FanPairing.Unpaired(sensors, pairs).Count);
    }

    [Fact]
    public void Pairing_reports_rpm_only_fans_and_friendly_names()
    {
        var sensors = new[] { Control("/lpc/nct", 0, "Fan Control #1"), Rpm("/lpc/nct", 0, "Fan #1"), Rpm("/lpc/nct", 4, "Fan #5") };
        var pairs = FanPairing.Pair(sensors);
        var unpaired = Assert.Single(FanPairing.Unpaired(sensors, pairs));
        Assert.Equal("/lpc/nct/fan/4", unpaired.Id);

        // Which numbered header is the CPU fan differs from board to board: never guessed.
        Assert.Equal("Fan 1 (motherboard header)", FanPairing.FriendlyName(pairs[0].Control, pairs[0].Rpm));
        Assert.Equal("Fan 5 (motherboard header)", FanPairing.FriendlyName(unpaired));
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

    [Fact]
    public void Session_is_stale_when_the_pid_was_reused_by_another_process()
    {
        var started = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var session = new FanSession { ProcessId = 4242, ProcessStartedUtc = started, ProcessName = "PcChecker" };

        Assert.True(FanSessionStore.IsStale(session, _ => null));
        Assert.False(FanSessionStore.IsStale(session, _ => new ProcessIdentity(started, "PcChecker")));
        Assert.False(FanSessionStore.IsStale(session, _ => new ProcessIdentity(started.AddSeconds(1), "pcchecker")));
        // Same id, different start time: a new process inherited the number after the crash.
        Assert.True(FanSessionStore.IsStale(session, _ => new ProcessIdentity(started.AddMinutes(30), "PcChecker")));
        // Same id, different image.
        Assert.True(FanSessionStore.IsStale(session, _ => new ProcessIdentity(started, "chrome")));
        // Unknown start time or name (access denied): only what is known is compared.
        Assert.False(FanSessionStore.IsStale(session, _ => new ProcessIdentity(null, null)));
        Assert.True(FanSessionStore.IsStale(session, _ => new ProcessIdentity(null, "svchost")));

        var current = FanSessionStore.NewForCurrentProcess();
        Assert.Equal(Environment.ProcessId, current.ProcessId);
        Assert.False(current.CleanExit);
        Assert.False(FanSessionStore.IsStale(current, FanSessionStore.LookupProcess));
        Assert.NotNull(FanSessionStore.LookupProcess(Environment.ProcessId));
        Assert.Null(FanSessionStore.LookupProcess(int.MaxValue - 7));
    }

    [Fact]
    public void Clean_exit_is_recorded_only_for_the_current_process_and_round_trips()
    {
        var path = FanSessionStore.DefaultPath(_dir);
        var foreign = new FanSession { ProcessId = 4242, FanIds = ["/gpu/nvidia/0/control/0"] };
        FanSessionStore.Write(path, foreign);
        FanSessionStore.MarkCleanExit(path);
        Assert.False(FanSessionStore.TryRead(path)!.CleanExit);

        var mine = FanSessionStore.NewForCurrentProcess();
        mine.FanIds = ["/lpc/nct/control/0"];
        mine.ProfileName = "Quiet night";
        FanSessionStore.Write(path, mine);
        FanSessionStore.MarkCleanExit(path);
        var read = FanSessionStore.TryRead(path);
        Assert.NotNull(read);
        Assert.True(read!.CleanExit);
        Assert.Equal("Quiet night", read.ProfileName);
        Assert.Equal(mine.ProcessStartedUtc, read.ProcessStartedUtc);
        Assert.Equal(mine.ProcessName, read.ProcessName);

        // A file without the new fields (older version) still reads as a live session.
        File.WriteAllText(path, """{ "ProcessId": 4242, "FanIds": ["a"] }""");
        var legacy = FanSessionStore.TryRead(path);
        Assert.NotNull(legacy);
        Assert.False(legacy!.CleanExit);
        Assert.Null(legacy.ProcessStartedUtc);
        Assert.False(FanSessionStore.IsStale(legacy, _ => new ProcessIdentity(DateTimeOffset.UtcNow, "x")));
    }

    [Fact]
    public void Restart_detection_uses_the_boot_time_and_the_uptime_counter()
    {
        var boot = new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
        var session = new FanSession { BootUtc = boot, UpdatedUtc = boot.AddMinutes(30) };

        // Same boot: later, with the uptime grown by the same amount.
        Assert.False(FanSessionStore.RebootedSince(session, boot.AddHours(2), TimeSpan.FromHours(2)));
        // Same boot after a small clock adjustment.
        Assert.False(FanSessionStore.RebootedSince(session, boot.AddHours(2).AddSeconds(40), TimeSpan.FromHours(2)));
        // Restarted: the uptime counter is lower than when the file was written.
        Assert.True(FanSessionStore.RebootedSince(session, boot.AddMinutes(40), TimeSpan.FromMinutes(5)));
        // Restarted long ago, uptime already past the old value: the boot times differ.
        Assert.True(FanSessionStore.RebootedSince(session, boot.AddHours(5), TimeSpan.FromHours(3)));
        // Files from older versions have no boot time: never assume a restart.
        Assert.False(FanSessionStore.RebootedSince(new FanSession { UpdatedUtc = boot }, boot.AddDays(3), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Fans_left_without_firmware_control_depend_on_how_the_session_ended()
    {
        var crashed = new FanSession { FanIds = ["/lpc/nct/control/0", "/gpu/nvidia/0/control/0"], NeedsRestartFanIds = ["/lpc/nct/control/1", "/LPC/nct/control/0"] };
        Assert.Equal(["/lpc/nct/control/0", "/gpu/nvidia/0/control/0", "/lpc/nct/control/1"], FanSessionStore.FansWithoutFirmwareControl(crashed, rebooted: false));
        Assert.Empty(FanSessionStore.FansWithoutFirmwareControl(crashed, rebooted: true));

        // Closing normally hands every fan back except those a crash left stuck earlier in this boot.
        var clean = new FanSession { CleanExit = true, FanIds = ["/lpc/nct/control/0", "/lpc/nct/control/1"], NeedsRestartFanIds = ["/lpc/nct/control/1"] };
        Assert.Equal(["/lpc/nct/control/1"], FanSessionStore.FansWithoutFirmwareControl(clean, rebooted: false));
        Assert.Empty(FanSessionStore.FansWithoutFirmwareControl(clean, rebooted: true));

        Directory.CreateDirectory(_dir);
        var path = FanSessionStore.DefaultPath(_dir);
        File.WriteAllText(path, """{ "ProcessId": 4242, "FanIds": ["a"], "NeedsRestartFanIds": null }""");
        var legacy = FanSessionStore.TryRead(path);
        Assert.NotNull(legacy);
        Assert.Empty(legacy!.NeedsRestartFanIds);
        Assert.Null(legacy.BootUtc);
    }

    [Fact]
    public async Task Crash_recovery_keeps_the_marker_when_the_fans_cannot_be_found()
    {
        var settings = new FakeSettingsService(_dir);
        var path = FanSessionStore.DefaultPath(_dir);
        FanSessionStore.Write(path, new FanSession { ProcessId = 0, FanIds = ["/lpc/nct/control/0"], BootUtc = FanSessionStore.CurrentBootUtc() });

        using var sensors = new DemoSensorService();
        using (var svc = new LibreHardwareFanControlService(new RecordingHost(), sensors, settings, new PassThroughGuard(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibreHardwareFanControlService>.Instance))
        {
            // The hardware monitor exposes no fans (driver blocked or still loading): nothing was reverted, so say so.
            var notices = await svc.RunStartupAsync(CancellationToken.None);
            var notice = Assert.Single(notices);
            Assert.Contains("could not", notice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("already back under automatic", notice, StringComparison.OrdinalIgnoreCase);

            var kept = FanSessionStore.TryRead(path);
            Assert.NotNull(kept);
            Assert.Contains("/lpc/nct/control/0", kept!.NeedsRestartFanIds);

            await svc.ShutdownAsync();
        }

        // A normal exit keeps the unconfirmed fan, so the next start still tries to recover it.
        var afterExit = FanSessionStore.TryRead(path);
        Assert.NotNull(afterExit);
        Assert.True(afterExit!.CleanExit);
        Assert.Equal(["/lpc/nct/control/0"], FanSessionStore.FansWithoutFirmwareControl(afterExit, rebooted: false));
    }

    [Fact]
    public async Task A_clean_exit_without_a_profile_is_not_reported_as_fans_left_under_control()
    {
        // Closing the hardware monitor hands every fan back, so a clean exit leaves nothing to revert or report.
        var settings = new FakeSettingsService(_dir);
        var path = FanSessionStore.DefaultPath(_dir);
        FanSessionStore.Write(path, new FanSession { ProcessId = 0, CleanExit = true, FanIds = ["/lpc/nct/control/0"], BootUtc = FanSessionStore.CurrentBootUtc() });

        using var sensors = new DemoSensorService();
        using var svc = new LibreHardwareFanControlService(new RecordingHost(), sensors, settings, new PassThroughGuard(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibreHardwareFanControlService>.Instance);

        Assert.Empty(await svc.RunStartupAsync(CancellationToken.None));
        Assert.False(File.Exists(path));
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
        Assert.Equal(5, demo.GetRawControls().Count); // the read-only CPU_OPT header has no control
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

    // ---- Threading ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fan_discovery_never_touches_the_hardware_monitor_on_the_calling_thread()
    {
        var host = new RecordingHost();
        var settings = new FakeSettingsService();
        using var sensors = new DemoSensorService();
        using var svc = new LibreHardwareFanControlService(host, sensors, settings, new PassThroughGuard(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibreHardwareFanControlService>.Instance);

        // The host is already open, so every await before the hardware walk completes synchronously; the walk must
        // still be moved off the caller (in the app, the UI thread).
        Task<IReadOnlyList<FanDevice>>? pending = null;
        int callerThread = 0;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            pending = svc.GetFansAsync(CancellationToken.None);
        });
        caller.Start();
        caller.Join();

        await pending!;
        Assert.NotEmpty(host.CallingThreads);
        Assert.DoesNotContain(callerThread, host.CallingThreads);
    }

    [Fact]
    public async Task Startup_detects_the_fans_so_the_dashboard_and_tray_can_use_them()
    {
        // No marker and "apply on startup" off: the startup work still detects the fans once.
        var fake = new FakeGpuFanHost();
        using var sensors = new DemoSensorService();
        using var svc = new LibreHardwareFanControlService(fake, sensors, new FakeSettingsService(_dir), new PassThroughGuard(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibreHardwareFanControlService>.Instance);
        Assert.False(svc.IsAvailable);

        Assert.Empty(await svc.RunStartupAsync(CancellationToken.None));
        Assert.True(svc.IsAvailable);
        Assert.Null(svc.UnavailableReason);
    }

    [Fact]
    public async Task A_fan_that_refuses_to_go_back_to_automatic_stays_watched_and_is_retried()
    {
        var fake = new FakeGpuFanHost { RefuseDefault = true };
        using var sensors = new DemoSensorService();
        using var svc = new LibreHardwareFanControlService(fake, sensors, new FakeSettingsService(_dir), new PassThroughGuard(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibreHardwareFanControlService>.Instance);
        var fan = Assert.Single(await svc.GetFansAsync(CancellationToken.None));
        Assert.True((await svc.SetManualAsync(fan.Id, 60, CancellationToken.None)).Success);

        Assert.False((await svc.SetAutoAsync(fan.Id, CancellationToken.None)).Success);
        Assert.Equal([fan.Id], svc.SoftwareControlledFanIds);
        Assert.Equal(FanMode.Manual, Assert.Single(await svc.GetFansAsync(CancellationToken.None)).Mode);

        Assert.False((await svc.RevertAllToAutoAsync(CancellationToken.None)).Success);
        Assert.Equal(2, fake.DefaultCalls); // "Revert all" tried it again

        fake.RefuseDefault = false;
        Assert.True((await svc.RevertAllToAutoAsync(CancellationToken.None)).Success);
        Assert.Empty(svc.SoftwareControlledFanIds);
        Assert.Equal(3, fake.DefaultCalls);
    }

    /// <summary>A hardware monitor with one graphics card fan control, built from interface fakes.</summary>
    private sealed class FakeGpuFanHost : IHardwareMonitorHost
    {
        private readonly object _lock = new();
        private readonly LibreHardwareMonitor.Hardware.IComputer _computer;

        public FakeGpuFanHost()
        {
            var id = new LibreHardwareMonitor.Hardware.Identifier("gpu-nvidia", "0");
            LibreHardwareMonitor.Hardware.IHardware? hardware = null;
            var control = Fake.Create<LibreHardwareMonitor.Hardware.IControl>(
                ("get_MinSoftwareValue", _ => 0f),
                ("get_MaxSoftwareValue", _ => 100f),
                ("SetSoftware", args =>
                {
                    Percent = (float)args![0]!;
                    return null;
                }),
                ("SetDefault", _ =>
                {
                    DefaultCalls++;
                    return RefuseDefault ? throw new InvalidOperationException("The driver refused.") : null;
                }));
            var sensor = Fake.Create<LibreHardwareMonitor.Hardware.ISensor>(
                ("get_SensorType", _ => LibreHardwareMonitor.Hardware.SensorType.Control),
                ("get_Control", _ => control),
                ("get_Identifier", _ => new LibreHardwareMonitor.Hardware.Identifier("gpu-nvidia", "0", "control", "0")),
                ("get_Name", _ => "GPU Fan"),
                ("get_Value", _ => (float?)Percent),
                ("get_Hardware", _ => hardware));
            hardware = Fake.Create<LibreHardwareMonitor.Hardware.IHardware>(
                ("get_HardwareType", _ => LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia),
                ("get_Name", _ => "NVIDIA GeForce RTX 4070"),
                ("get_Identifier", _ => id),
                ("get_Sensors", _ => new[] { sensor }),
                ("get_SubHardware", _ => Array.Empty<LibreHardwareMonitor.Hardware.IHardware>()));
            _computer = Fake.Create<LibreHardwareMonitor.Hardware.IComputer>(
                ("get_Hardware", _ => new List<LibreHardwareMonitor.Hardware.IHardware> { hardware }));
        }

        public bool RefuseDefault { get; set; }
        public int DefaultCalls { get; private set; }
        public float Percent { get; private set; } = 40;
        public bool IsOpen => true;
        public string? OpenError => null;
        public int Generation => 1;

        public Task EnsureOpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public T? WithComputer<T>(Func<LibreHardwareMonitor.Hardware.IComputer, T> func)
        {
            lock (_lock)
            {
                return func(_computer);
            }
        }

        public void WithComputer(Action<LibreHardwareMonitor.Hardware.IComputer> action)
        {
            lock (_lock)
            {
                action(_computer);
            }
        }
    }

    /// <summary>An interface fake: named members answer from a handler, everything else returns its default value.</summary>
    public class Fake : System.Reflection.DispatchProxy
    {
        private readonly Dictionary<string, Func<object?[]?, object?>> _members = new(StringComparer.Ordinal);

        public static T Create<T>(params (string Member, Func<object?[]?, object?> Handler)[] members)
            where T : class
        {
            var proxy = Create<T, Fake>();
            var fake = (Fake)(object)proxy;
            foreach (var (member, handler) in members)
            {
                fake._members[member] = handler;
            }

            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            if (_members.TryGetValue(targetMethod.Name, out var handler))
            {
                return handler(args);
            }

            var type = targetMethod.ReturnType;
            return type.IsValueType && type != typeof(void) && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class RecordingHost : IHardwareMonitorHost
    {
        private readonly object _lock = new();
        private readonly List<int> _threads = [];

        public bool IsOpen => true;
        public string? OpenError => null;

        public IReadOnlyList<int> CallingThreads
        {
            get
            {
                lock (_lock)
                {
                    return _threads.ToList();
                }
            }
        }

        public Task EnsureOpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public T? WithComputer<T>(Func<LibreHardwareMonitor.Hardware.IComputer, T> func)
        {
            Record();
            return default;
        }

        public void WithComputer(Action<LibreHardwareMonitor.Hardware.IComputer> action) => Record();

        private void Record()
        {
            lock (_lock)
            {
                _threads.Add(Environment.CurrentManagedThreadId);
            }
        }
    }

    private sealed class PassThroughGuard : PcChecker.Core.Restore.IChangeGuard
    {
        public Task<PcChecker.Core.Common.OperationResult<T>> RunGuardedAsync<T>(string description, PcChecker.Core.Restore.ChangeOptions options,
            Func<CancellationToken, Task<PcChecker.Core.Common.OperationResult<T>>> change, IProgress<PcChecker.Core.Common.ProgressUpdate>? progress,
            CancellationToken cancellationToken) => change(cancellationToken);

        public Task<PcChecker.Core.Common.OperationResult> RunGuardedAsync(string description, PcChecker.Core.Restore.ChangeOptions options,
            Func<CancellationToken, Task<PcChecker.Core.Common.OperationResult>> change, IProgress<PcChecker.Core.Common.ProgressUpdate>? progress,
            CancellationToken cancellationToken) => change(cancellationToken);
    }

    private sealed class FakeSettingsService(string? directory = null) : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsDirectory => directory ?? Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(SettingsDirectory, "fake-settings.json");
        public event EventHandler<AppSettings>? Changed;
        public Task LoadAsync() => Task.CompletedTask;

        public Task SaveAsync()
        {
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }
    }
}
