using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.History;
using PcChecker.Core.Localization;
using PcChecker.Core.Security;
using PcChecker.Core.Storage;
using PcChecker.Core.Upgrades;
using Xunit;

namespace PcChecker.Core.Tests;

public class UpgradeMemoryRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;

    internal static MemoryModuleInfo Stick(string locator, string bank = "BANK 0", long size = 16 * Gb, string part = "F5-6000J3636F16G",
        int rated = 6000, int configured = 6000, string type = "DDR5") =>
        new($"{locator} {size / Gb} GB", "G.Skill", locator, "OK", bank, locator, size, rated, configured, type, "DIMM", part, "0001", 1100, 64, 64,
            new Dictionary<string, string>());

    [Theory]
    [InlineData("DIMM_A2", "BANK 0", 'A', 2)] // ASUS
    [InlineData("DIMM_B1", "BANK 2", 'B', 1)]
    [InlineData("DDR5_A2", "BANK 0", 'A', 2)] // Gigabyte
    [InlineData("DIMMB2", "BANK 3", 'B', 2)] // MSI
    [InlineData("ChannelA-DIMM1", "BANK 0", 'A', 2)] // ASRock / Intel reference
    [InlineData("Controller0-ChannelB-DIMM0", "BANK 0", 'B', 1)]
    [InlineData("DIMM 1", "P0 CHANNEL A", 'A', 2)] // AMD reference BIOS
    [InlineData("DIMM 0", "P0 CHANNEL B", 'B', 1)]
    [InlineData("DIMM_A2", "P0 CHANNEL A", 'A', 2)] // ASUS on AMD
    public void Slot_positions_are_read_from_every_vendor_spelling(string locator, string bank, char channel, int index)
    {
        var slot = MemoryLayoutRules.ParseSlot(locator, bank);
        Assert.Equal(channel, slot.Channel);
        Assert.Equal(index, slot.IndexInChannel);
    }

    [Theory]
    [InlineData("DIMM1", "BANK 0")]
    [InlineData("XMM1", "")]
    [InlineData(null, null)]
    public void Unknown_slot_labels_stay_unknown(string? locator, string? bank)
    {
        Assert.Null(MemoryLayoutRules.ParseSlot(locator, bank).Label);
    }

    [Fact]
    public void Two_sticks_in_the_same_channel_run_single_channel()
    {
        var layout = MemoryLayoutRules.Evaluate([Stick("DIMM_A1"), Stick("DIMM_A2")], 4, "ASUSTeK COMPUTER INC.", false);
        Assert.True(layout.SameChannel);
        Assert.Equal(ChannelMode.SingleChannel, layout.Mode);
        Assert.False(layout.WrongSlots);
    }

    [Fact]
    public void Two_sticks_in_A1_and_B1_on_a_four_slot_board_are_in_the_wrong_slots()
    {
        var wrong = MemoryLayoutRules.Evaluate([Stick("DIMM_A1"), Stick("DIMM_B1")], 4, "ASUSTeK COMPUTER INC.", false);
        Assert.True(wrong.WrongSlots);
        Assert.Equal(ChannelMode.DualChannel, wrong.Mode);
        Assert.Equal("DIMM_A2 + DIMM_B2", wrong.RecommendedSlots);

        var right = MemoryLayoutRules.Evaluate([Stick("DIMM_A2"), Stick("DIMM_B2")], 4, "ASUSTeK COMPUTER INC.", false);
        Assert.False(right.WrongSlots);
        Assert.False(right.SameChannel);

        // Two-slot boards and laptops have no "wrong" pair.
        Assert.False(MemoryLayoutRules.Evaluate([Stick("DIMM_A1"), Stick("DIMM_B1")], 2, "ASUS", false).WrongSlots);
        Assert.False(MemoryLayoutRules.Evaluate([Stick("ChannelA-DIMM0"), Stick("ChannelB-DIMM0")], 4, "LENOVO", true).WrongSlots);
    }

    [Fact]
    public void Recommended_slot_names_follow_the_board_makers_labels()
    {
        Assert.Equal("DIMM_A2 + DIMM_B2", MemoryLayoutRules.RecommendedSlotNames("ASUSTeK COMPUTER INC.", "DDR5"));
        Assert.Equal("DDR4_A2 + DDR4_B2", MemoryLayoutRules.RecommendedSlotNames("Gigabyte Technology Co., Ltd.", "DDR4"));
        Assert.Equal("DIMMA2 + DIMMB2", MemoryLayoutRules.RecommendedSlotNames("Micro-Star International Co., Ltd.", "DDR5"));
        Assert.Equal("A2 + B2", MemoryLayoutRules.RecommendedSlotNames("ASRock", "DDR5"));
    }

    [Fact]
    public void One_stick_is_single_channel_and_mixed_kits_are_found()
    {
        Assert.Equal(ChannelMode.SingleChannel, MemoryLayoutRules.Evaluate([Stick("DIMM_A2")], 4, null, false).Mode);
        Assert.True(MemoryLayoutRules.IsMixedKit([Stick("DIMM_A2"), Stick("DIMM_B2", part: "CMK16GX4M1E3200C16")]));
        Assert.True(MemoryLayoutRules.IsMixedKit([Stick("DIMM_A2"), Stick("DIMM_B2", size: 8 * Gb)]));
        Assert.False(MemoryLayoutRules.IsMixedKit([Stick("DIMM_A2"), Stick("DIMM_B2")]));
    }

    [Fact]
    public void Memory_that_is_often_nearly_full_asks_for_the_next_size()
    {
        var t = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 200)
            .Select(i => new HistorySample(t.AddMinutes(i), 60, new Dictionary<string, double> { [HistoryMetrics.Ram] = i % 4 == 0 ? 91 : 60 }))
            .ToList();
        var pressure = MemoryLayoutRules.EvaluatePressure(samples);
        Assert.True(pressure.IsOftenFull);
        Assert.Equal(0.25, pressure.HighShare, 2);
        Assert.Equal(91, pressure.PeakPercent);

        Assert.Equal(32, MemoryLayoutRules.RecommendedTotalGb(16 * Gb, pressure));
        Assert.Equal(64, MemoryLayoutRules.RecommendedTotalGb(32 * Gb, pressure));
        Assert.Null(MemoryLayoutRules.RecommendedTotalGb(32 * Gb, MemoryPressure.None));
        Assert.Null(MemoryLayoutRules.RecommendedTotalGb(16 * Gb, MemoryPressure.None));
        Assert.Equal(16, MemoryLayoutRules.RecommendedTotalGb(8 * Gb, MemoryPressure.None));
        Assert.Equal(32, MemoryLayoutRules.RecommendedTotalGb(8 * Gb, pressure));
        Assert.False(MemoryLayoutRules.EvaluatePressure(samples.Take(10).ToList()).IsOftenFull); // too little data
    }
}

public class UpgradeBalanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 19, 0, 0, TimeSpan.Zero);

    private static List<HistorySample> Session(int minutes, double gpu, double cpu, double? core = null, DateTimeOffset? start = null) =>
        Enumerable.Range(0, minutes).Select(i =>
        {
            var values = new Dictionary<string, double> { [HistoryMetrics.GpuLoad] = gpu, [HistoryMetrics.CpuLoad] = cpu };
            if (core is { } c)
            {
                values[GamingBalanceRules.BusiestCoreMetric] = c;
            }

            return new HistorySample((start ?? Start).AddMinutes(i), 60, values);
        }).ToList();

    [Theory]
    [InlineData(30.0, 50.0, null, SampleBound.NotGaming)]
    [InlineData(98.0, 45.0, null, SampleBound.GpuLimited)]
    [InlineData(95.0, 69.0, null, SampleBound.GpuLimited)]
    [InlineData(97.0, 75.0, null, SampleBound.Neither)] // both busy
    [InlineData(70.0, 60.0, null, SampleBound.CpuLimited)] // total-only fallback
    [InlineData(70.0, 40.0, null, SampleBound.Neither)] // capped frame rate
    [InlineData(70.0, 35.0, 97.0, SampleBound.CpuLimited)] // one core maxed
    [InlineData(70.0, 70.0, 60.0, SampleBound.Neither)] // per-core says no core is maxed
    [InlineData(85.0, 60.0, null, SampleBound.Neither)]
    [InlineData(91.0, 40.0, null, SampleBound.GpuLimited)] // nearly flat out
    [InlineData(89.0, 40.0, null, SampleBound.Neither)]
    public void Samples_are_classified_with_the_documented_thresholds(double gpu, double cpu, double? core, SampleBound expected)
    {
        Assert.Equal(expected, GamingBalanceRules.Classify(gpu, cpu, core));
    }

    [Fact]
    public void A_long_gpu_bound_history_is_gpu_limited_with_high_confidence()
    {
        var analysis = GamingBalanceRules.Analyze(Session(150, 98, 40));
        Assert.Equal(BoundVerdict.GpuLimited, analysis.Verdict);
        Assert.Equal(AdviceConfidence.High, analysis.Confidence);
        Assert.Single(analysis.Sessions);
        Assert.Equal(150, analysis.Sessions[0].Minutes, 3);
    }

    [Fact]
    public void Cpu_limited_from_the_total_alone_is_at_most_medium_confidence_but_per_core_data_can_be_high()
    {
        var totalOnly = GamingBalanceRules.Analyze(Session(150, 65, 62));
        Assert.Equal(BoundVerdict.CpuLimited, totalOnly.Verdict);
        Assert.Equal(AdviceConfidence.Medium, totalOnly.Confidence);
        Assert.False(totalOnly.HasPerCoreData);

        var perCore = GamingBalanceRules.Analyze(Session(150, 65, 35, core: 98));
        Assert.Equal(BoundVerdict.CpuLimited, perCore.Verdict);
        Assert.Equal(AdviceConfidence.High, perCore.Confidence);
        Assert.True(perCore.HasPerCoreData);
    }

    [Fact]
    public void Too_little_gaming_gives_no_verdict_and_gaps_split_sessions()
    {
        Assert.Equal(BoundVerdict.NoData, GamingBalanceRules.Analyze(Session(10, 98, 40)).Verdict);
        Assert.Equal(BoundVerdict.NoData, GamingBalanceRules.Analyze(Session(60, 20, 10)).Verdict); // desktop work, no game

        var two = Session(30, 98, 40).Concat(Session(40, 66, 63, start: Start.AddHours(3))).ToList();
        var analysis = GamingBalanceRules.Analyze(two);
        Assert.Equal(2, analysis.Sessions.Count);
        Assert.Equal(BoundVerdict.CpuLimited, analysis.Sessions[0].Verdict); // newest first
        Assert.Equal(BoundVerdict.GpuLimited, analysis.Sessions[1].Verdict);
        Assert.Equal(BoundVerdict.Balanced, GamingBalanceRules.VerdictFor(0.2, 0.2));
    }

    [Fact]
    public void Demo_history_has_a_cpu_limited_session_yesterday()
    {
        var now = new DateTimeOffset(2026, 10, 27, 23, 30, 0, TimeSpan.Zero);
        var analysis = GamingBalanceRules.Analyze(DemoHistoryGenerator.Generate(now, zone: TimeZoneInfo.Utc));
        Assert.NotEqual(BoundVerdict.NoData, analysis.Verdict);
        Assert.Contains(analysis.Sessions, s => s.Verdict == BoundVerdict.CpuLimited && s.Start.Date == now.AddDays(-1).Date);
        Assert.Contains(analysis.Sessions, s => s.Verdict != BoundVerdict.CpuLimited);
    }
}

public class UpgradePlatformTests
{
    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", CpuUpgradePaths.Lga1700, 13, "Core i7-13700K")]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", CpuUpgradePaths.Lga1151Coffee, 9, "Core i7-9700K")]
    [InlineData("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", CpuUpgradePaths.Lga1200, 10, "Core i5-10400F")]
    [InlineData("Intel(R) Core(TM) i7-4790K CPU @ 4.00GHz", CpuUpgradePaths.Lga1150, 4, "Core i7-4790K")]
    public void Intel_sockets_come_from_the_generation(string name, string socket, int generation, string model)
    {
        var cpu = CpuUpgradePaths.Identify(name);
        Assert.True(cpu.IsIntel);
        Assert.Equal(socket, cpu.Socket);
        Assert.Equal(generation, cpu.IntelGeneration);
        Assert.Equal(model, cpu.Model);
        Assert.False(cpu.IsLaptop);
    }

    [Fact]
    public void Amd_core_ultra_and_laptop_chips_are_recognised()
    {
        var x3d = CpuUpgradePaths.Identify("AMD Ryzen 7 5800X3D 8-Core Processor");
        Assert.Equal(CpuUpgradePaths.Am4, x3d.Socket);
        Assert.Equal("Ryzen 7 5800X3D", x3d.Model);
        Assert.Equal(CpuUpgradePaths.Am5, CpuUpgradePaths.Identify("AMD Ryzen 9 7950X 16-Core Processor").Socket);
        var ultra = CpuUpgradePaths.Identify("Intel(R) Core(TM) Ultra 9 285K");
        Assert.Equal(CpuUpgradePaths.Lga1851, ultra.Socket);
        Assert.Equal("Core Ultra 9 285K", ultra.Model);

        var laptop = CpuUpgradePaths.Identify("12th Gen Intel(R) Core(TM) i7-12700H");
        Assert.True(laptop.IsLaptop);
        Assert.Null(laptop.Socket);
        Assert.Empty(CpuUpgradePaths.UpgradesFrom(laptop));
        Assert.True(CpuUpgradePaths.Identify("Intel(R) Core(TM) Ultra 7 155H").IsLaptop);
    }

    [Fact]
    public void Socket_table_offers_only_faster_chips_for_the_same_socket()
    {
        var from13700K = CpuUpgradePaths.UpgradesFrom(CpuUpgradePaths.Identify("13th Gen Intel(R) Core(TM) i7-13700K"));
        Assert.Equal(["Core i7-14700K", "Core i9-14900K"], from13700K.Select(o => o.Name).OrderBy(n => n).ToArray());

        var from3600 = CpuUpgradePaths.UpgradesFrom(CpuUpgradePaths.Identify("AMD Ryzen 5 3600 6-Core Processor"));
        Assert.Contains(from3600, o => o.Name == "Ryzen 7 5800X3D");
        Assert.Equal(4, from3600.Count);

        Assert.Empty(CpuUpgradePaths.UpgradesFrom(CpuUpgradePaths.Identify("AMD Ryzen 7 5800X3D 8-Core Processor")));
        var from9800 = CpuUpgradePaths.UpgradesFrom(CpuUpgradePaths.Identify("AMD Ryzen 7 9800X3D 8-Core Processor"));
        Assert.Empty(from9800);

        Assert.True(CpuUpgradePaths.PathFor(CpuUpgradePaths.Lga1700)!.EndOfPlatform);
        Assert.Contains("0x12B", CpuUpgradePaths.PathFor(CpuUpgradePaths.Lga1700)!.BeforeYouSwap);
        Assert.False(CpuUpgradePaths.PathFor(CpuUpgradePaths.Am5)!.EndOfPlatform);
        Assert.Empty(CpuUpgradePaths.PathFor(CpuUpgradePaths.Lga1150)!.Options);
        Assert.Null(CpuUpgradePaths.PathFor("Socket 939"));
    }

    [Fact]
    public void Raptor_lake_microcode_check()
    {
        var i7 = CpuUpgradePaths.Identify("13th Gen Intel(R) Core(TM) i7-13700K");
        Assert.True(CpuUpgradePaths.NeedsRaptorLakeMicrocode(i7, "0x129"));
        Assert.False(CpuUpgradePaths.NeedsRaptorLakeMicrocode(i7, "0x12B"));
        Assert.False(CpuUpgradePaths.NeedsRaptorLakeMicrocode(i7, "0x12F"));
        Assert.Null(CpuUpgradePaths.NeedsRaptorLakeMicrocode(i7, null));
        Assert.False(CpuUpgradePaths.NeedsRaptorLakeMicrocode(CpuUpgradePaths.Identify("12th Gen Intel(R) Core(TM) i7-12700K"), "0x2C"));
    }

    [Fact]
    public void Psu_estimate_adds_cpu_gpu_and_the_rest_with_headroom()
    {
        var psu = PsuEstimator.Estimate(CpuUpgradePaths.Identify(DemoData.CpuName), GpuRules.Classify(DemoData.GpuName));
        Assert.Equal(253, psu.CpuWatts);
        Assert.Equal(200, psu.GpuWatts);
        Assert.True(psu.CpuFromTable && psu.GpuFromTable);
        Assert.Equal(528, psu.PeakWatts);
        Assert.Equal(750, psu.RecommendedWatts);

        var small = PsuEstimator.Estimate(CpuUpgradePaths.Identify("AMD Ryzen 5 5600X 6-Core Processor"), GpuRules.Classify("NVIDIA GeForce RTX 4060"));
        Assert.Equal(76 + 115 + PsuEstimator.RestWatts, small.PeakWatts);
        Assert.Equal(450, small.RecommendedWatts);

        var unknown = PsuEstimator.Estimate(CpuIdentity.Unknown, GpuRules.Classify("Some Future GPU"));
        Assert.False(unknown.GpuFromTable);
        Assert.Equal(200, unknown.GpuWatts);
        Assert.Equal(1600, PsuEstimator.RoundUpToStandard(5000));
        Assert.Equal(650, PsuEstimator.RoundUpToStandard(551));
    }

    [Fact]
    public void Gpu_generation_tier_vram_and_link_rules()
    {
        var rtx4070 = GpuRules.Classify("NVIDIA GeForce RTX 4070");
        Assert.Equal(2022, rtx4070.ReleaseYear);
        Assert.Equal(GpuTier.UpperMid, rtx4070.Tier);
        Assert.Equal(GpuAge.Recent, GpuRules.AgeOf(rtx4070, 2026));
        Assert.Equal(GpuAge.Old, GpuRules.AgeOf(GpuRules.Classify("NVIDIA GeForce GTX 1060 6GB"), 2026));
        Assert.Equal(GpuTier.High, GpuRules.Classify("AMD Radeon RX 9070 XT").Tier);
        Assert.Equal(220, GpuRules.Classify("NVIDIA GeForce RTX 4070 SUPER").BoardPowerW);
        Assert.Equal(GpuTier.Integrated, GpuRules.Classify("Intel(R) UHD Graphics 770", isIntegrated: true).Tier);

        Assert.Equal(VramFit.Tight, GpuRules.EvaluateVram(8, 2560));
        Assert.Equal(VramFit.TooLittle, GpuRules.EvaluateVram(8, 3840));
        Assert.Equal(VramFit.Fine, GpuRules.EvaluateVram(12, 2560));
        Assert.Equal(VramFit.Fine, GpuRules.EvaluateVram(8, 1920));
        Assert.Equal(VramFit.Tight, GpuRules.EvaluateVram(6, 1920));
        Assert.Equal(8, GpuRules.VramGb(8_589_934_592));

        Assert.True(GpuRules.IsLinkNarrowed(new PcieLinkInfo(4, 4, 8, 16)));
        Assert.False(GpuRules.IsLinkNarrowed(new PcieLinkInfo(1, 4, 16, 16))); // idle power saving drops the generation, not the lanes
        Assert.Equal("PCIe 4.0 x16", GpuRules.DescribeLink(4, 16));

        Assert.Equal(2, UpgradeAdvisor.SuggestGpuClass(rtx4070, GpuAge.Recent, 2560)!.Level);
        Assert.Equal(3, UpgradeAdvisor.SuggestGpuClass(GpuRules.Classify("NVIDIA GeForce RTX 3060"), GpuAge.Recent, 3840)!.Level);
        Assert.Null(UpgradeAdvisor.SuggestGpuClass(GpuRules.Classify("NVIDIA GeForce RTX 5090"), GpuAge.Current, 3840));
    }

    [Fact]
    public void Pcie_link_output_is_parsed_from_plain_integers()
    {
        var link = PcieLinkParser.Parse("DEVPKEY_PciDevice_CurrentLinkSpeed=4\r\nDEVPKEY_PciDevice_MaxLinkSpeed=4\r\nDEVPKEY_PciDevice_CurrentLinkWidth=8\r\nDEVPKEY_PciDevice_MaxLinkWidth=16\r\n");
        Assert.Equal(new PcieLinkInfo(4, 4, 8, 16), link);
        Assert.Null(PcieLinkParser.Parse("garbage"));
        Assert.Equal(new PcieLinkInfo(null, null, 16, null), PcieLinkParser.Parse("DEVPKEY_PciDevice_CurrentLinkWidth=16\nDEVPKEY_PciDevice_MaxLinkSpeed=99"));
    }

    [Fact]
    public void Windows_11_readiness_rules()
    {
        var tr = Translator.English;
        var tpm2 = new TpmInfo(true, true, true, "2.0, 0, 1.59");
        const long Gb = 1024L * 1024 * 1024;

        Assert.Equal(Win11Status.AlreadyWindows11, Windows11Rules.Evaluate(new Windows11Inputs(26100, null, null, false, null, null, null), tr).Status);
        Assert.Equal(Win11Status.Ready, Windows11Rules.Evaluate(new Windows11Inputs(19045, tpm2, true, false, true, 16 * Gb, 500_000_000_000), tr).Status);

        var tpmOff = Windows11Rules.Evaluate(new Windows11Inputs(19045, new TpmInfo(false, null, null, null), false, false, true, 16 * Gb, 500_000_000_000), tr);
        Assert.Equal(Win11Status.ReadyAfterBiosChange, tpmOff.Status);
        Assert.Contains(tpmOff.Checks, c => c.Id == "tpm" && c.Passed == false && c.FixableInBios);

        var oldCpu = Windows11Rules.Evaluate(new Windows11Inputs(19045, tpm2, true, false, false, 16 * Gb, 500_000_000_000), tr);
        Assert.Equal(Win11Status.NotReady, oldCpu.Status);

        Assert.False(CpuUpgradePaths.IsWindows11Supported(CpuUpgradePaths.Identify("Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz")));
        Assert.True(CpuUpgradePaths.IsWindows11Supported(CpuUpgradePaths.Identify("Intel(R) Core(TM) i5-8400 CPU @ 2.80GHz")));
        Assert.False(CpuUpgradePaths.IsWindows11Supported(CpuUpgradePaths.Identify("AMD Ryzen 7 1700 Eight-Core Processor")));
        Assert.True(CpuUpgradePaths.IsWindows11Supported(CpuUpgradePaths.Identify("AMD Ryzen 5 2600 Six-Core Processor")));
        Assert.True(CpuUpgradePaths.IsWindows11Supported(CpuUpgradePaths.Identify("Intel(R) Core(TM) Ultra 7 265K")));
    }
}

public class UpgradeAdvisorTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 23, 30, 0, TimeSpan.Zero);

    private static PhysicalDrive Drive(string model, DriveMedia media, string bus, long size, bool system, params string[] volumes) => new()
    {
        Number = system ? 0 : 1,
        Model = model,
        Serial = model + "-SN",
        Media = media,
        BusType = bus,
        SizeBytes = size,
        IsSystemDisk = system,
        Volumes = volumes,
        Verdict = new DriveHealthVerdict(DriveVerdict.Good, "Healthy.", []),
    };

    private static LogicalDriveInfo Volume(string letter, long size, long free) =>
        new(letter, letter, null, "NTFS", "Local disk", size, free, null, false, new Dictionary<string, string>());

    private static InstalledGame Game(string name, string path, long bytes) =>
        new(name, GameLauncher.Steam, name, name, path, bytes, null, null, null, null, null, null, null);

    private static HardwareInventory Desktop(params MemoryModuleInfo[] sticks)
    {
        var inventory = DemoHardwareInventoryService.Build();
        inventory.MemoryModules.Clear();
        inventory.MemoryModules.AddRange(sticks);
        return inventory;
    }

    [Fact]
    public void Windows_on_a_hard_drive_is_the_top_upgrade_and_games_on_a_hard_drive_move_for_free()
    {
        var hw = Desktop(UpgradeMemoryRulesTests.Stick("DIMM_A2"), UpgradeMemoryRulesTests.Stick("DIMM_B2"));
        hw.LogicalDrives.Clear();
        hw.LogicalDrives.Add(Volume("C:", 1000 * Gb, 400 * Gb));
        hw.LogicalDrives.Add(Volume("E:", 1000 * Gb, 800 * Gb));
        var facts = new UpgradeFacts
        {
            Now = Now,
            Hardware = hw,
            Drives =
            [
                Drive("ST1000DM010", DriveMedia.Hdd, "SATA", 1000 * Gb, true, "C:"),
                Drive("CT1000MX500SSD1", DriveMedia.Ssd, "SATA", 1000 * Gb, false, "E:"),
            ],
            Games = [Game("Big Game", @"C:\Games\Big Game", 120 * Gb), Game("Other", @"C:\Games\Other", 60 * Gb)],
        };

        var report = UpgradeAdvisor.Build(facts, Translator.English);
        Assert.Equal("storage.system-ssd", report.Upgrades[0].Id);
        Assert.Equal(UpgradeImpact.Big, report.Upgrades[0].Impact);
        Assert.Contains(report.FixFirst, f => f.Id == "storage.games-move" && f.Detail.Contains("E:", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Upgrades, u => u.Id == "storage.games-ssd");
    }

    [Fact]
    public void Games_on_a_hard_drive_without_ssd_room_become_an_upgrade_and_a_full_drive_is_a_free_fix()
    {
        var hw = Desktop(UpgradeMemoryRulesTests.Stick("DIMM_A2"), UpgradeMemoryRulesTests.Stick("DIMM_B2"));
        hw.LogicalDrives.Clear();
        hw.LogicalDrives.Add(Volume("C:", 500 * Gb, 30 * Gb));
        hw.LogicalDrives.Add(Volume("D:", 2000 * Gb, 900 * Gb));
        var facts = new UpgradeFacts
        {
            Now = Now,
            Hardware = hw,
            Drives =
            [
                Drive("Samsung SSD 870 EVO 500GB", DriveMedia.Ssd, "SATA", 500 * Gb, true, "C:"),
                Drive("ST2000DM008", DriveMedia.Hdd, "SATA", 2000 * Gb, false, "D:"),
            ],
            Games = [Game("Big Game", @"D:\SteamLibrary\Big Game", 120 * Gb)],
        };

        var report = UpgradeAdvisor.Build(facts, Translator.English);
        Assert.Contains(report.Upgrades, u => u.Id == "storage.games-ssd");
        Assert.Contains(report.Upgrades, u => u.Id == "storage.nvme" && u.Impact == UpgradeImpact.Small);
        Assert.Contains(report.FixFirst, f => f.Id == "storage.full:C:" && f.PageKey == "disk-space");
        Assert.Contains(report.Upgrades, u => u.Id == "storage.bigger");
        Assert.True(UpgradeAdvisor.M2SlotLikely("Intel Z790", false));
        Assert.Null(UpgradeAdvisor.M2SlotLikely("Intel H61", false));
        Assert.True(UpgradeAdvisor.M2SlotLikely(null, true));
    }

    [Fact]
    public void Memory_setup_problems_are_free_fixes_and_one_stick_is_an_upgrade()
    {
        var sameChannel = UpgradeAdvisor.Build(new UpgradeFacts
        {
            Now = Now,
            Hardware = Desktop(UpgradeMemoryRulesTests.Stick("DIMM_A1"), UpgradeMemoryRulesTests.Stick("DIMM_A2", configured: 4800)),
        }, Translator.English);
        Assert.Contains(sameChannel.FixFirst, f => f.Id == "memory.same-channel");
        Assert.Contains(sameChannel.FixFirst, f => f.Id == "memory.xmp" && f.PageKey == UpgradeAdvisor.RamTuningPage);

        var single = UpgradeAdvisor.Build(new UpgradeFacts { Now = Now, Hardware = Desktop(UpgradeMemoryRulesTests.Stick("DIMM_A2", size: 8 * Gb)) },
            Translator.English);
        Assert.Contains(single.Upgrades, u => u.Id == "memory.dual");
        Assert.Contains(single.Upgrades, u => u.Id == "memory.capacity" && u.Title.Contains("16 GB", StringComparison.Ordinal));
    }

    [Fact]
    public void Ranking_orders_by_score_removes_duplicates_and_caps_the_list()
    {
        UpgradeSuggestion S(string id, int score) => new(id, UpgradeArea.Storage, score, UpgradeAdvisor.ImpactFor(score), id, "why", "impact", []);
        var ranked = UpgradeAdvisor.Rank([S("a", 30), S("b", 95), S("a", 60), S("c", 0), S("d", 41), S("e", 50), S("f", 52), S("g", 53), S("h", 54)]);
        Assert.Equal(UpgradeAdvisor.MaxUpgrades, ranked.Count);
        Assert.Equal("b", ranked[0].Id);
        Assert.Equal(60, ranked[1].Score);
        Assert.Single(ranked, u => u.Id == "a");
        Assert.DoesNotContain(ranked, u => u.Id == "c");
        Assert.Equal(UpgradeImpact.Big, UpgradeAdvisor.ImpactFor(70));
        Assert.Equal(UpgradeImpact.Noticeable, UpgradeAdvisor.ImpactFor(40));
        Assert.Equal(UpgradeImpact.Small, UpgradeAdvisor.ImpactFor(39));
    }

    [Fact]
    public void Laptops_only_get_memory_and_storage_upgrades()
    {
        var hw = Desktop(UpgradeMemoryRulesTests.Stick("ChannelA-DIMM0", size: 8 * Gb));
        hw.System = hw.System! with { PcSystemType = "Mobile", ChassisType = "Notebook" };
        hw.Processors.Clear();
        hw.Processors.Add(new CpuInfo("12th Gen Intel(R) Core(TM) i7-12700H", "GenuineIntel", "CPU0", "OK", null, "x64", "U3E1", 14, 20, 2300, 2300, null, null,
            null, null, null, true, 5, 64, new Dictionary<string, string>()));
        var report = UpgradeAdvisor.Build(new UpgradeFacts
        {
            Now = Now,
            Hardware = hw,
            History = UpgradeBalanceSamples.Gpu(120),
        }, Translator.English);

        Assert.True(report.IsLaptop);
        Assert.Null(report.Psu);
        Assert.All(report.Upgrades, u => Assert.True(u.Area is UpgradeArea.Memory or UpgradeArea.Storage, u.Id));
        Assert.Contains(report.Upgrades, u => u.Id == "memory.capacity" && u.Note!.Contains("soldered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Demo_pc_gets_consistent_advice_from_the_demo_services()
    {
        var service = new UpgradeAdvisorService(new DemoHardwareInventoryService(), new DemoDriveInventoryService(), new DemoGameLibraryService(),
            new DemoHistoryService(), new DemoSecurityStatusService(), new DemoDisplayService(), new DemoPcieLinkReader(),
            NullLogger<UpgradeAdvisorService>.Instance);

        var report = await service.AnalyzeAsync(Translator.English, null, CancellationToken.None);

        Assert.Same(report, service.Last);
        Assert.False(report.IsLaptop);
        Assert.Contains(report.Glance, g => g.Value.Contains("RTX 4070", StringComparison.Ordinal));
        Assert.Contains(report.Glance, g => g.Label == "Memory" && g.Detail!.Contains("dual channel", StringComparison.Ordinal));
        Assert.Equal(750, report.Psu!.RecommendedWatts);
        Assert.Equal(Win11Status.AlreadyWindows11, report.Windows11.Status);

        // Games on the WD hard drive (D:) with room on the SATA SSD (E:); the WD drive has early warning signs.
        Assert.Contains(report.FixFirst, f => f.Id == "storage.games-move");
        Assert.Contains(report.FixFirst, f => f.Id.StartsWith("storage.backup:", StringComparison.Ordinal) && f.Severity == Severity.High);
        Assert.Contains(report.Upgrades, u => u.Id.StartsWith("storage.replace:", StringComparison.Ordinal));
        Assert.DoesNotContain(report.FixFirst, f => f.Id is "memory.same-channel" or "memory.slots" or "memory.xmp");
        Assert.Contains(report.Upgrades, u => u.Id == "cpu.dropin" && u.Options.Any(o => o.Contains("14700K", StringComparison.Ordinal)));
        Assert.Contains(report.Balance.Sessions, s => s.Verdict == BoundVerdict.CpuLimited);
        Assert.Equal(BoundVerdict.GpuLimited, report.Balance.Verdict); // most evenings: the RTX 4070 runs flat out at 1440p
        Assert.Contains(report.Upgrades, u => u.Id == "gpu.upgrade" && u.Options.Contains("GeForce RTX 5070 Ti"));

        string summary = UpgradeSummaryText.BuildSummary(report, true, Translator.English);
        Assert.Contains("Your PC at a glance", summary, StringComparison.Ordinal);
        Assert.Contains("Fix first (free)", summary, StringComparison.Ordinal);
        Assert.Contains("RTX 4070", summary, StringComparison.Ordinal);
        string prompt = UpgradeSummaryText.AskPrompt(report, true, Translator.English);
        Assert.StartsWith("Please make me a personalised upgrade plan", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not guess prices", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_words_and_durations()
    {
        var tr = Translator.English;
        Assert.Equal("45 min", UpgradeSummaryText.Duration(45, tr));
        Assert.Equal("2 h 20 min", UpgradeSummaryText.Duration(140, tr));
        Assert.Contains("History recording is off", UpgradeSummaryText.VerdictDetail(BoundAnalysis.None, false, tr), StringComparison.Ordinal);
        Assert.Contains("15 minutes", UpgradeSummaryText.VerdictDetail(BoundAnalysis.None, true, tr), StringComparison.Ordinal);
        Assert.Equal(string.Empty, UpgradeSummaryText.ConfidenceText(BoundAnalysis.None, tr));
    }
}

internal static class UpgradeBalanceSamples
{
    public static IReadOnlyList<HistorySample> Gpu(int minutes) =>
        Enumerable.Range(0, minutes).Select(i => new HistorySample(new DateTimeOffset(2026, 10, 20, 19, 0, 0, TimeSpan.Zero).AddMinutes(i), 60,
            new Dictionary<string, double> { [HistoryMetrics.GpuLoad] = 99, [HistoryMetrics.CpuLoad] = 30 })).ToList();
}
