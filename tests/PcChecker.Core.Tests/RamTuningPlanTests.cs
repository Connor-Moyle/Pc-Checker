using PcChecker.Core.Crashes;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.RamTuning;
using PcChecker.Core.Sensors;
using Xunit;
using static PcChecker.Core.Tests.RamTuningSamples;

namespace PcChecker.Core.Tests;

public class RamFactsBuilderTests
{
    [Fact]
    public void Demo_pc_facts_show_xmp_off_dual_channel_and_a_hynix_kit()
    {
        var facts = DemoIntel();
        Assert.Equal("DDR5", facts.MemoryType);
        Assert.Equal(RamPlatformKind.IntelLga1700Ddr5, facts.Platform.Kind);
        Assert.Equal("Disabled", facts.Xmp.State);
        Assert.Equal(4800, facts.ConfiguredMts);
        Assert.Equal(6000, facts.RatedMts);
        Assert.Equal(ChannelMode.Dual, facts.Layout.Mode);
        Assert.True(facts.Layout.RecommendedSlots);
        Assert.All(facts.Modules, m => Assert.Equal(DramIc.HynixDdr5Gen16, m.Ic.Ic));
        Assert.Equal(1, facts.MaxRanks);
        Assert.Equal(PcMaker.Asus, facts.BoardMaker);
        Assert.NotNull(facts.SupportPage);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("0", null)]
    [InlineData("junk", null)]
    public void Ranks_come_from_smbios_attributes(string attributes, int? ranks) =>
        Assert.Equal(ranks, RamFactsBuilder.Ranks(new Dictionary<string, string> { ["Attributes"] = attributes }));

    [Fact]
    public void Dimm_temperatures_are_the_memory_temperature_sensors()
    {
        SensorReading[] readings =
        [
            new("ram.temp.a2", "Kit", HardwareKind.Memory, SensorKind.Temperature, "DIMM A2", 44, null, null, "°C"),
            new("ram.load", "Kit", HardwareKind.Memory, SensorKind.Load, "Memory", 40, null, null, "%"),
            new("cpu.temp", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Package", 60, null, null, "°C"),
        ];
        var temps = RamFactsBuilder.DimmTemperatures(readings);
        Assert.Single(temps);
        Assert.Equal(44, temps[0].Celsius);
        Assert.Empty(RamFactsBuilder.DimmTemperatures(null));
    }
}

public class RamPlanBuilderTests
{
    private static readonly DateTime Today = new(2026, 10, 28);

    [Fact]
    public void Demo_pc_plan_says_xmp_is_off_and_has_every_stage()
    {
        var plan = RamPlanBuilder.Build(DemoIntel(), Today);
        Assert.Equal(RamPlanTone.Warning, plan.Tone);
        Assert.Contains("XMP is off", plan.Headline);
        Assert.Contains("4800", plan.Headline);
        Assert.Contains("6000", plan.Headline);
        Assert.Equal(6000, plan.ExpectedMts);
        foreach (RamStage stage in Enum.GetValues<RamStage>())
        {
            Assert.NotEmpty(plan.StepsFor(stage, advanced: true));
        }

        var profile = plan.Steps.Single(s => s.Id == "profile");
        Assert.Equal(RamStepState.ToDo, profile.State);
        Assert.Equal("Ai Tweaker > Ai Overclock Tuner > XMP I", profile.MenuPath);
        Assert.Contains(plan.Steps, s => s.Id == "intel-defaults");
        Assert.NotEmpty(plan.IfItDoesNotBoot);
        Assert.NotEmpty(plan.VoltageLimits);
    }

    [Fact]
    public void Simple_mode_hides_the_advanced_steps()
    {
        var plan = RamPlanBuilder.Build(DemoIntel(), Today);
        Assert.Empty(plan.StepsFor(RamStage.ManualTimings, advanced: false));
        Assert.All(plan.StepsFor(RamStage.ManualTimings, advanced: true), s => Assert.True(s.Advanced));
        Assert.DoesNotContain(plan.Steps.Where(s => s.Stage <= RamStage.EnableProfile), s => s.Advanced);

        var trfc = plan.Steps.Single(s => s.Id == "trfc");
        Assert.Contains("540", trfc.SafeRange);
    }

    [Fact]
    public void A_bios_from_before_intels_microcode_fix_must_be_updated_first()
    {
        var plan = RamPlanBuilder.Build(DemoIntel(new DateTime(2024, 6, 1)), Today);
        var bios = plan.Steps.Single(s => s.Id == "bios-update");
        Assert.Equal(RamStepState.ToDo, bios.State);
        Assert.Contains("microcode", bios.Title);
        Assert.Equal("Tool > ASUS EZ Flash 3 Utility", bios.MenuPath);
        Assert.NotNull(bios.LinkUrl);
        Assert.StartsWith("https://", bios.LinkUrl);

        var recent = RamPlanBuilder.Build(DemoIntel(new DateTime(2025, 6, 1)), Today).Steps.Single(s => s.Id == "bios-update");
        Assert.Equal(RamStepState.Check, recent.State);
    }

    [Fact]
    public void Am5_with_expo_on_and_a_hynix_kit_is_done_with_the_safe_part()
    {
        var facts = Am5Expo();
        Assert.Equal(RamPlatformKind.Am5, facts.Platform.Kind);
        Assert.Equal(ChannelMode.Dual, facts.Layout.Mode);
        var plan = RamPlanBuilder.Build(facts, Today);
        Assert.Equal("EXPO", plan.ProfileName);
        Assert.Equal(RamPlanTone.Good, plan.Tone);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "profile").State);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "am5-ratio").State);
        Assert.Equal("OC > FCLK Frequency", plan.Steps.Single(s => s.Id == "am5-fclk").MenuPath);
        Assert.Contains(plan.Steps, s => s.Id == "am5-mcr");
        Assert.Contains(plan.Steps, s => s.Id == "am5-vsoc" && !s.Advanced);
        Assert.Contains(plan.Steps, s => s.Id == "am5-8000" && s.Advanced);
        Assert.Contains(plan.Steps, s => s.Id == "twr");
        Assert.DoesNotContain(plan.Steps, s => s.Id == "intel-defaults");
        Assert.Contains(plan.VoltageLimits, v => v.Name.StartsWith("VSOC", StringComparison.Ordinal) && v.DailyMaxVolts == 1.30);
    }

    [Fact]
    public void Am5_fast_kits_are_brought_back_to_one_to_one()
    {
        var plan = RamPlanBuilder.Build(Am5Expo(rated: 7200, configured: 7200, part: "F5-7200J3445G16GX2-TZ5RK"), Today);
        Assert.Equal(6000, plan.ExpectedMts);
        Assert.Equal(RamStepState.ToDo, plan.Steps.Single(s => s.Id == "am5-ratio").State);
        Assert.Equal(RamPlanTone.Info, plan.Tone);
    }

    [Fact]
    public void Zen4_has_no_ddr5_8000_step()
    {
        var plan = RamPlanBuilder.Build(Am5Expo(cpu: "AMD Ryzen 7 7800X3D 8-Core Processor"), Today);
        Assert.DoesNotContain(plan.Steps, s => s.Id == "am5-8000");
    }

    [Fact]
    public void Am4_b_die_plan_uses_docp_fclk_1800_and_b_die_refresh()
    {
        var facts = Am4BDie();
        Assert.Equal(DramIc.SamsungBDie, facts.Modules[0].Ic.Ic);
        var plan = RamPlanBuilder.Build(facts, Today);
        Assert.Equal("D.O.C.P.", plan.ProfileName);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "am4-ratio").State);
        Assert.Contains("1800", plan.Steps.Single(s => s.Id == "am4-ratio").Title);
        var trfc = plan.Steps.Single(s => s.Id == "trfc");
        Assert.Contains("504", trfc.SafeRange);
        Assert.DoesNotContain(plan.Steps, s => s.Id == "trefi");
        Assert.Contains(plan.VoltageLimits, v => v.Name.StartsWith("VDIMM", StringComparison.Ordinal) && v.DailyMaxVolts == 1.50);
    }

    [Fact]
    public void Unknown_chips_get_no_timing_values()
    {
        var facts = RamFactsBuilder.Build(
            [Module("DIMM_A2", "CMK32GX5M2B5600C36", 5600, 5600, vendor: "Corsair"), Module("DIMM_B2", "CMK32GX5M2B5600C36", 5600, 5600, vendor: "Corsair")],
            4, "Intel Core i7-13700K", Board(PcMaker.Msi, "PRO Z790-A WIFI", new DateTime(2025, 3, 1), "i7-13700K"), null, DateTimeOffset.Now);
        var plan = RamPlanBuilder.Build(facts, Today);
        Assert.Contains(plan.Steps, s => s.Id == "find-ic" && s.State == RamStepState.NotApplicable);
        Assert.DoesNotContain(plan.Steps, s => s.Id == "trfc");
    }

    [Fact]
    public void Warm_modules_rule_out_a_higher_trefi()
    {
        SensorReading[] hot = [new("ram.temp.a", "Kit", HardwareKind.Memory, SensorKind.Temperature, "DIMM A1", 53, null, null, "°C")];
        var plan = RamPlanBuilder.Build(Am5Expo(readings: hot), Today);
        Assert.Equal(RamStepState.NotApplicable, plan.Steps.Single(s => s.Id == "trefi").State);

        var cool = RamPlanBuilder.Build(Am5Expo(), Today);
        Assert.Equal(RamStepState.Optional, cool.Steps.Single(s => s.Id == "trefi").State);
    }

    [Fact]
    public void Laptops_get_no_bios_plan()
    {
        var facts = RamFactsBuilder.Build([Module("Controller0-ChannelA-DIMM0", "M425R1GB4BB0-CQKOL", 5600, 5600, vendor: "Samsung")], 2,
            "13th Gen Intel(R) Core(TM) i7-13700H", null, null, DateTimeOffset.Now);
        var plan = RamPlanBuilder.Build(facts, Today);
        Assert.Single(plan.Steps);
        Assert.Equal(RamStepState.NotApplicable, plan.Steps[0].State);
    }

    [Fact]
    public void Single_channel_is_the_headline_and_a_step()
    {
        var facts = RamFactsBuilder.Build(
            [Module("DIMM_A1", "F5-6000J3038F16GX2-TZ5RK", 6000, 6000), Module("DIMM_A2", "F5-6000J3038F16GX2-TZ5RK", 6000, 6000)],
            4, "Intel Core i7-13700K", null, null, DateTimeOffset.Now);
        var plan = RamPlanBuilder.Build(facts, Today);
        Assert.Contains("single channel", plan.Headline);
        Assert.Contains(plan.Steps, s => s.Id == "slots" && s.State == RamStepState.ToDo);
    }

    [Fact]
    public void Four_modules_expect_a_lower_speed()
    {
        var part = "F5-6000J3038F16GX2-TZ5RK";
        var facts = RamFactsBuilder.Build(
            [Module("DIMM_A1", part, 6000, 6000), Module("DIMM_A2", part, 6000, 6000), Module("DIMM_B1", part, 6000, 6000), Module("DIMM_B2", part, 6000, 6000)],
            4, "AMD Ryzen 7 7800X3D", null, null, DateTimeOffset.Now);
        var plan = RamPlanBuilder.Build(facts, Today);
        Assert.Equal(5200, plan.ExpectedMts);
        Assert.Equal(5600, plan.ExpectedMaxMts);
        Assert.Contains(plan.Steps, s => s.Id == "am5-four");
    }
}

public class RamVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unchanged_speed_is_a_problem_and_a_working_profile_is_ok()
    {
        var before = DemoIntel();
        var plan = RamPlanBuilder.Build(before, Now.Date);
        var still = RamVerifier.Compare(plan, before, MemoryCrashSummary.None, Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Problem, still.Overall);
        Assert.Contains(still.Items, i => i.Outcome == CheckOutcome.Problem && i.Title.Contains("4800", StringComparison.Ordinal));

        var after = DemoIntel(configured: 6000);
        var done = RamVerifier.Compare(plan, after, MemoryCrashSummary.None, Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Ok, done.Overall);
        Assert.Equal(6000, done.ConfiguredMts);
        Assert.Equal("Enabled", done.XmpState);
    }

    [Fact]
    public void Crashes_and_hardware_errors_since_the_change_are_flagged()
    {
        var after = DemoIntel(configured: 6000);
        var plan = RamPlanBuilder.Build(after, Now.Date);
        var crash = RamVerifier.Compare(plan, after, new MemoryCrashSummary(1, 0, Now, ["0x1A"]), Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Problem, crash.Overall);
        Assert.Contains(crash.Items, i => i.Detail.Contains("0x1A", StringComparison.Ordinal));

        var whea = RamVerifier.Compare(plan, after, new MemoryCrashSummary(0, 3, Now, []), Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Warning, whea.Overall);
    }

    [Fact]
    public void Faster_than_planned_is_a_warning()
    {
        var plan = RamPlanBuilder.Build(DemoIntel(), Now.Date);
        var fast = DemoIntel(configured: 6400);
        Assert.Equal(CheckOutcome.Warning, RamVerifier.Compare(plan, fast, MemoryCrashSummary.None, null, Now).Overall);
    }

    [Fact]
    public void The_journal_keeps_the_newest_checks_and_survives_a_round_trip()
    {
        var journal = new RamTuningJournal { Baseline = RamVerifier.Baseline(DemoIntel(), Now.AddDays(-2)) };
        for (int i = 0; i < 60; i++)
        {
            journal.Add(new RamCheckRecord(Now.AddMinutes(i), 4800 + i, "Disabled", ChannelMode.Dual, CheckOutcome.Problem, "x", 0, 0));
        }

        Assert.Equal(RamTuningJournal.MaxChecks, journal.Checks.Count);
        Assert.Equal(4800 + 59, journal.Checks[0].ConfiguredMts);

        var copy = RamTuningJournalFile.Deserialize(RamTuningJournalFile.Serialize(journal));
        Assert.Equal(journal.Checks.Count, copy.Checks.Count);
        Assert.Equal(journal.Baseline!.Time, copy.Baseline!.Time);
        Assert.Equal(ChannelMode.Dual, copy.Checks[0].Channels);
        Assert.Empty(RamTuningJournalFile.Deserialize("not json").Checks);
        Assert.Null(RamTuningJournalFile.Deserialize(null).Baseline);
    }
}

public class MemoryCrashRulesTests
{
    private static readonly DateTimeOffset Change = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static CrashEvent BlueScreen(uint code, DateTimeOffset time, WheaErrorInfo? whea = null, string id = "x") =>
        new() { Id = id + time.Ticks, Kind = CrashKind.BlueScreen, Time = time, BugCheckCode = code, Whea = whea };

    [Theory]
    [InlineData(0x1Au, true)]
    [InlineData(0x50u, true)]
    [InlineData(0x4Eu, true)]
    [InlineData(0x12Bu, true)]
    [InlineData(0x116u, false)]
    [InlineData(0x124u, false)]
    public void Memory_stop_codes_are_recognised(uint code, bool memory) =>
        Assert.Equal(memory, MemoryCrashRules.IsMemoryRelated(BlueScreen(code, Change)));

    [Fact]
    public void Whea_0x124_counts_only_with_a_memory_source()
    {
        Assert.True(MemoryCrashRules.IsMemoryRelated(BlueScreen(0x124, Change, new WheaErrorInfo { Components = ["Memory"] })));
        Assert.True(MemoryCrashRules.IsMemoryRelated(BlueScreen(0x124, Change, new WheaErrorInfo { Components = ["Processor"], MciStatus = 0xBC00000000000090 })));
        Assert.True(MemoryCrashRules.IsMemoryRelated(BlueScreen(0x124, Change, new WheaErrorInfo { Components = ["Processor"], MciStatus = 0xBC00000000000E0B })));
        Assert.False(MemoryCrashRules.IsMemoryRelated(BlueScreen(0x124, Change, new WheaErrorInfo { Components = ["Processor"], MciStatus = 0x9C20000000010135 })));
    }

    [Fact]
    public void Only_events_since_the_change_count_and_duplicates_count_once()
    {
        var report = new CrashReport
        {
            Events =
            [
                BlueScreen(0x1A, Change.AddDays(-1), id: "old"),
                BlueScreen(0x1A, Change.AddHours(5), id: "a"),
                BlueScreen(0x1A, Change.AddHours(5).AddSeconds(30), id: "b"),
                BlueScreen(0x50, Change.AddDays(2), id: "c"),
                BlueScreen(0x116, Change.AddDays(3), id: "d"),
            ],
            SystemEvents =
            [
                new CrashSystemEvent(Change.AddDays(-2), CrashSystemEventKind.WheaCorrected),
                new CrashSystemEvent(Change.AddDays(1), CrashSystemEventKind.WheaCorrected),
                new CrashSystemEvent(Change.AddDays(1), CrashSystemEventKind.DiskError),
            ],
        };

        var summary = MemoryCrashRules.Since(report, Change);
        Assert.Equal(2, summary.MemoryBlueScreens);
        Assert.Equal(1, summary.WheaErrors);
        Assert.Equal(["0x1A", "0x50"], summary.StopCodes);
        Assert.Equal(Change.AddDays(2), summary.Latest);
        Assert.False(MemoryCrashRules.Since(null, Change).Any);
    }
}

public class QuickMemoryCheckTests
{
    private const long Mb = 1024L * 1024;

    [Theory]
    [InlineData(16L * 1024, 70, 11468)]
    [InlineData(3L * 1024, 70, 1024)]
    [InlineData(2L * 1024, 70, 0)]
    [InlineData(1L * 1024, 70, 0)]
    [InlineData(16L * 1024, 100, 14336)]
    [InlineData(16L * 1024, 1, 1638)]
    public void The_share_of_free_memory_always_leaves_two_gigabytes(long freeMb, int percent, long expectedMb)
    {
        long bytes = QuickMemoryCheck.PlanBytes(freeMb * Mb, percent);
        Assert.Equal(expectedMb, bytes / Mb);
        Assert.Equal(0, bytes % Mb);
        if (bytes > 0)
        {
            Assert.True(freeMb * Mb - bytes >= QuickMemoryCheck.ReserveBytes);
        }
    }

    [Fact]
    public void Blocks_cover_the_plan_and_give_every_worker_its_own_memory()
    {
        long bytes = 3L * 1024 * Mb;
        var blocks = QuickMemoryCheck.PlanBlocks(bytes, 8);
        Assert.True(blocks.Count >= 8);
        Assert.All(blocks, b => Assert.InRange(b, 8, QuickMemoryCheck.MaxBlockBytes));
        Assert.All(blocks, b => Assert.Equal(0, b % 8));
        Assert.True(bytes - blocks.Sum(b => (long)b) < QuickMemoryCheck.MinBlockBytes);
        Assert.Empty(QuickMemoryCheck.PlanBlocks(1000, 8));
    }

    [Fact]
    public void Low_free_memory_stops_the_check_but_unknown_does_not()
    {
        Assert.True(QuickMemoryCheck.ShouldStopForMemory(100 * Mb));
        Assert.False(QuickMemoryCheck.ShouldStopForMemory(4096 * Mb));
        Assert.False(QuickMemoryCheck.ShouldStopForMemory(null));
    }

    [Theory]
    [InlineData(QuickPattern.WalkingOnes)]
    [InlineData(QuickPattern.WalkingZeros)]
    [InlineData(QuickPattern.Random)]
    [InlineData(QuickPattern.MovingInversions)]
    public void Each_pattern_passes_on_good_memory_and_finds_an_injected_fault(QuickPattern pattern)
    {
        var block = new ulong[4096];
        Assert.Equal(0, QuickMemoryPatterns.Run(block, pattern, 42));

        long errors = QuickMemoryPatterns.Run(block, pattern, 42, b => b[1234] ^= 1UL << 17);
        Assert.True(errors >= 1);
    }

    [Fact]
    public void Walking_patterns_cover_every_bit_position()
    {
        var block = new ulong[64];
        QuickMemoryPatterns.Fill(block, QuickPattern.WalkingOnes, 1);
        Assert.Equal(ulong.MaxValue, block.Aggregate(0UL, (a, w) => a | w));
        QuickMemoryPatterns.Fill(block, QuickPattern.WalkingZeros, 1);
        Assert.Equal(0UL, block.Aggregate(ulong.MaxValue, (a, w) => a & w));
    }

    [Fact]
    public async Task A_short_run_finds_an_injected_fault()
    {
        var result = await QuickMemoryCheck.RunAsync(4 * Mb, TimeSpan.FromMilliseconds(800), () => 8192 * Mb, null, CancellationToken.None,
            b => b[b.Length / 2] ^= 0x10);
        Assert.Equal(QuickCheckStop.Finished, result.Stop);
        Assert.True(result.Errors > 0);
        Assert.Equal(4 * Mb, result.BytesTested);
        Assert.Contains("not stable", result.Summary);
    }

    [Fact]
    public async Task A_clean_run_reports_no_errors_and_low_memory_stops_it_early()
    {
        var clean = await QuickMemoryCheck.RunAsync(4 * Mb, TimeSpan.FromMilliseconds(600), () => 8192 * Mb, null, CancellationToken.None);
        Assert.Equal(0, clean.Errors);
        Assert.True(clean.Rounds >= 1);

        var started = DateTime.UtcNow;
        long? Free() => DateTime.UtcNow - started > TimeSpan.FromMilliseconds(300) ? 100 * Mb : 8192 * Mb;
        var low = await QuickMemoryCheck.RunAsync(4 * Mb, TimeSpan.FromSeconds(20), Free, null, CancellationToken.None);
        Assert.Equal(QuickCheckStop.LowMemory, low.Stop);
        Assert.True(low.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancelling_and_too_little_memory_end_cleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelled = await QuickMemoryCheck.RunAsync(4 * Mb, TimeSpan.FromSeconds(20), () => 8192 * Mb, null, cts.Token);
        Assert.Equal(QuickCheckStop.Cancelled, cancelled.Stop);

        var none = await QuickMemoryCheck.RunAsync(0, TimeSpan.FromSeconds(1), () => 8192 * Mb, null, CancellationToken.None);
        Assert.Equal(QuickCheckStop.NotEnoughMemory, none.Stop);
    }
}

/// <summary>The plan's expected speed range agrees with its own steps, so following the plan never reads as a failure.</summary>
public class RamPlanRangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static RamFacts Am5Four(int configured)
    {
        var part = "F5-6000J3038F16GX2-TZ5RK";
        return RamFactsBuilder.Build(
            [Module("DIMM_A1", part, 6000, configured), Module("DIMM_A2", part, 6000, configured), Module("DIMM_B1", part, 6000, configured), Module("DIMM_B2", part, 6000, configured)],
            4, "AMD Ryzen 7 7800X3D", Board(PcMaker.Asus, "ROG STRIX X670E-E GAMING WIFI", new DateTime(2025, 6, 1), "AMD Ryzen 7 7800X3D"), null, DateTimeOffset.Now);
    }

    private static RamFacts Am4BDie4000(int configured) => RamFactsBuilder.Build(
        [Module("DIMM_A2", "F4-4000C16D-16GTZR", 4000, configured, "DDR4", 8 * Gb), Module("DIMM_B2", "F4-4000C16D-16GTZR", 4000, configured, "DDR4", 8 * Gb)],
        4, "AMD Ryzen 7 5800X 8-Core Processor", Board(PcMaker.Asus, "ROG STRIX B550-F GAMING", new DateTime(2024, 3, 1), "AMD Ryzen 7 5800X"),
        null, DateTimeOffset.Now);

    [Fact]
    public void Four_modules_on_am5_at_5200_follow_the_plan()
    {
        var facts = Am5Four(5200);
        Assert.Equal("Disabled", facts.Xmp.State);
        var plan = RamPlanBuilder.Build(facts, Now.Date);
        Assert.DoesNotContain("is off", plan.Headline, StringComparison.Ordinal);
        Assert.Equal(RamPlanTone.Good, plan.Tone);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "am5-four").State);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "profile").State);

        var check = RamVerifier.Compare(plan, facts, MemoryCrashSummary.None, Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Ok, check.Overall);
        Assert.DoesNotContain(check.Items, i => i.Outcome == CheckOutcome.Problem);
    }

    [Fact]
    public void Four_modules_on_am5_still_at_jedec_is_a_problem()
    {
        var facts = Am5Four(4800);
        var plan = RamPlanBuilder.Build(facts, Now.Date);
        Assert.Contains("EXPO is off", plan.Headline, StringComparison.Ordinal);
        var check = RamVerifier.Compare(plan, facts, MemoryCrashSummary.None, Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Problem, check.Overall);
    }

    [Fact]
    public void Am4_ddr4_4000_b_die_at_3600_follows_the_plan()
    {
        var facts = Am4BDie4000(3600);
        Assert.Equal(DramIc.SamsungBDie, facts.Modules[0].Ic.Ic);
        var plan = RamPlanBuilder.Build(facts, Now.Date);
        Assert.Equal(3600, plan.ExpectedMts);
        Assert.Equal(3800, plan.ExpectedMaxMts);
        Assert.Equal(RamPlanTone.Good, plan.Tone);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "am4-ratio").State);
        Assert.Equal(CheckOutcome.Ok, RamVerifier.Compare(plan, facts, MemoryCrashSummary.None, Now.AddDays(-1), Now).Overall);
    }

    [Fact]
    public void Am4_ddr4_4000_at_its_rating_is_not_called_good()
    {
        var facts = Am4BDie4000(4000);
        var plan = RamPlanBuilder.Build(facts, Now.Date);
        Assert.Equal(RamPlanTone.Info, plan.Tone);
        Assert.Equal(RamStepState.ToDo, plan.Steps.Single(s => s.Id == "am4-ratio").State);
        var check = RamVerifier.Compare(plan, facts, MemoryCrashSummary.None, Now.AddDays(-1), Now);
        Assert.Equal(CheckOutcome.Warning, check.Overall);
    }

    [Fact]
    public void Zen3_at_3800_is_inside_the_plan()
    {
        var facts = Am4BDie4000(3800);
        var plan = RamPlanBuilder.Build(facts, Now.Date);
        Assert.Equal(RamStepState.Done, plan.Steps.Single(s => s.Id == "am4-ratio").State);
        Assert.Equal(CheckOutcome.Ok, RamVerifier.Compare(plan, facts, MemoryCrashSummary.None, Now.AddDays(-1), Now).Overall);
    }

    [Fact]
    public void Ranges_never_exceed_the_kit_rating()
    {
        foreach (var facts in new[] { DemoIntel(), Am5Expo(), Am4BDie(), Am5Four(5200), Am4BDie4000(3600), Am5Expo(rated: 7200, configured: 7200, part: "F5-7200J3445G16GX2-TZ5RK") })
        {
            var (low, high) = RamPlanBuilder.ExpectedRange(facts, RamTuningRules.TargetFor(facts.Platform, facts.Modules.Count, facts.MaxRanks is >= 2, facts.SlotCount));
            Assert.NotNull(low);
            Assert.True(high >= low);
            Assert.True(high <= facts.RatedMts);
        }
    }

    [Fact]
    public void A_known_chip_without_easy_timings_says_so()
    {
        var part = "F5-7200J3646F24GX2-TZ5RK";
        var facts = RamFactsBuilder.Build(
            [Module("DIMM 1", part, 7200, 6000, capacity: 24 * Gb, bank: "P0 CHANNEL A"), Module("DIMM 1", part, 7200, 6000, capacity: 24 * Gb, bank: "P0 CHANNEL B")],
            4, "AMD Ryzen 7 9800X3D", Board(PcMaker.Msi, "MAG X870 TOMAHAWK WIFI", new DateTime(2025, 6, 1), "AMD Ryzen 7 9800X3D"), null, DateTimeOffset.Now);
        Assert.Equal(DramIc.HynixDdr5Gen24, facts.Modules[0].Ic.Ic);
        var plan = RamPlanBuilder.Build(facts, Now.Date);
        Assert.DoesNotContain(plan.Steps, s => s.Id == "find-ic");
        var step = plan.Steps.Single(s => s.Id == "no-easy-timings");
        Assert.Contains("SK hynix", step.Title, StringComparison.Ordinal);
        Assert.True(step.Advanced);
    }
}

public class MemoryWheaFilterTests
{
    private static readonly DateTimeOffset Change = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_memory_related_whea_errors_count()
    {
        var report = new CrashReport
        {
            SystemEvents =
            [
                // PCI Express (NVMe, GPU link) errors have nothing to do with memory settings.
                new CrashSystemEvent(Change.AddHours(1), CrashSystemEventKind.WheaCorrected) { EventId = 17, Whea = new WheaErrorInfo { Components = ["PCI Express"] } },
                // A cache error inside the processor.
                new CrashSystemEvent(Change.AddHours(2), CrashSystemEventKind.WheaCorrected) { EventId = 19, Whea = new WheaErrorInfo { Components = ["Processor"], MciStatus = 0x9C20000000010135 } },
                // A bus/interconnect error (Infinity Fabric on Ryzen).
                new CrashSystemEvent(Change.AddHours(3), CrashSystemEventKind.WheaCorrected) { EventId = 19, Whea = new WheaErrorInfo { Components = ["Processor"], MciStatus = 0x9C00000000000E0B } },
                // A memory error.
                new CrashSystemEvent(Change.AddHours(4), CrashSystemEventKind.WheaCorrected) { EventId = 47, Whea = new WheaErrorInfo { Components = ["Memory"] } },
                // An unreadable record still counts.
                new CrashSystemEvent(Change.AddHours(5), CrashSystemEventKind.WheaFatal) { EventId = 18 },
            ],
        };

        var summary = MemoryCrashRules.Since(report, Change);
        Assert.Equal(3, summary.WheaErrors);
        Assert.Equal(Change.AddHours(5), summary.Latest);
    }
}
