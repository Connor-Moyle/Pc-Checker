using PcChecker.Core.Crashes;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Hardware;
using PcChecker.Core.RamTuning;
using PcChecker.Core.Sensors;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Shared builders for the RAM tuning tests.</summary>
internal static class RamTuningSamples
{
    public const long Gb = 1024L * 1024 * 1024;

    public static MemoryModuleInfo Module(string locator, string part, int rated, int configured, string type = "DDR5", long capacity = 16 * Gb,
        string? bank = "BANK 0", string vendor = "G.Skill", string attributes = "1") =>
        new($"{locator} module", vendor, locator, "OK", bank, locator, capacity, rated, configured, type, "DIMM", part, "0001", 1350, 64, 64,
            new Dictionary<string, string> { ["Attributes"] = attributes });

    public static PcPlatformInfo Board(PcMaker maker, string product, DateTime biosDate, string cpu) => new()
    {
        BoardMaker = maker,
        BoardMakerName = SmbiosNormalizer.MakerName(maker),
        BoardProduct = product,
        BiosVersion = "1.0",
        BiosDate = biosDate,
        CpuName = cpu,
    };

    /// <summary>The demo PC: i7-13700K, ASUS ROG STRIX Z790-E, 2 × 16 GB G.Skill DDR5-6000 CL30 running at 4800 (XMP off).</summary>
    public static RamFacts DemoIntel(DateTime? biosDate = null, int configured = 4800) => RamFactsBuilder.Build(
        [Module("DIMM_A2", "F5-6000J3038F16GX2-TZ5RK", 6000, configured), Module("DIMM_B2", "F5-6000J3038F16GX2-TZ5RK", 6000, configured)],
        4, "13th Gen Intel(R) Core(TM) i7-13700K", Board(PcMaker.Asus, "ROG STRIX Z790-E GAMING WIFI", biosDate ?? new DateTime(2025, 1, 20), "i7-13700K"),
        null, DateTimeOffset.Now);

    /// <summary>AM5 with EXPO on and a G.Skill SK hynix kit (DDR5-6000 CL30, tRCD 38) on an MSI board.</summary>
    public static RamFacts Am5Expo(string cpu = "AMD Ryzen 7 9800X3D 8-Core Processor", int rated = 6000, int configured = 6000, int slots = 4,
        string part = "F5-6000J3038F16GX2-TZ5NR", IEnumerable<SensorReading>? readings = null) => RamFactsBuilder.Build(
        [Module("DIMM 1", part, rated, configured, bank: "P0 CHANNEL A"), Module("DIMM 1", part, rated, configured, bank: "P0 CHANNEL B")],
        slots, cpu, Board(PcMaker.Msi, "MAG X870 TOMAHAWK WIFI", new DateTime(2025, 6, 1), cpu), readings, DateTimeOffset.Now);

    /// <summary>AM4 with a Samsung B-die kit (G.Skill DDR4-3600 CL14) and XMP (D.O.C.P.) on, on an ASUS board.</summary>
    public static RamFacts Am4BDie(int configured = 3600) => RamFactsBuilder.Build(
        [Module("DIMM_A2", "F4-3600C14D-16GTZN", 3600, configured, "DDR4", 8 * Gb), Module("DIMM_B2", "F4-3600C14D-16GTZN", 3600, configured, "DDR4", 8 * Gb)],
        4, "AMD Ryzen 7 5800X 8-Core Processor", Board(PcMaker.Asus, "ROG STRIX B550-F GAMING", new DateTime(2024, 3, 1), "AMD Ryzen 7 5800X"),
        null, DateTimeOffset.Now);
}

public class RamPlatformDetectionTests
{
    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "DDR5", RamPlatformKind.IntelLga1700Ddr5, 13, true)]
    [InlineData("Intel Core i7-13700K", "DDR5", RamPlatformKind.IntelLga1700Ddr5, 13, true)]
    [InlineData("Intel(R) Core(TM) i9-14900KS", "DDR4", RamPlatformKind.IntelLga1700Ddr4, 14, true)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12600K", "DDR5", RamPlatformKind.IntelLga1700Ddr5, 12, false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700H", "DDR5", RamPlatformKind.Mobile, 13, false)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-1260P", "DDR4", RamPlatformKind.Mobile, 12, false)]
    public void Intel_desktop_names_map_to_their_platform(string cpu, string type, RamPlatformKind kind, int gen, bool needsFix)
    {
        var p = RamPlatformDetector.Detect(cpu, type);
        Assert.Equal(kind, p.Kind);
        Assert.Equal(gen, p.IntelGeneration);
        Assert.Equal(needsFix, p.NeedsIntelMicrocodeFix);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", RamPlatformKind.IntelArrowLake)]
    [InlineData("Intel(R) Core(TM) Ultra 5 245KF", RamPlatformKind.IntelArrowLake)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", RamPlatformKind.Mobile)]
    [InlineData("Intel(R) Core(TM) Ultra 9 275HX", RamPlatformKind.Mobile)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", RamPlatformKind.Am5)]
    [InlineData("AMD Ryzen 9 9950X 16-Core Processor", RamPlatformKind.Am5)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", RamPlatformKind.Am4)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", RamPlatformKind.Am4)]
    [InlineData("AMD Ryzen 7 2700X Eight-Core Processor", RamPlatformKind.Am4Legacy)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", RamPlatformKind.Mobile)]
    [InlineData("AMD Ryzen Threadripper 7980X 64-Cores", RamPlatformKind.OtherDesktop)]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", RamPlatformKind.OtherDesktop)]
    [InlineData(null, RamPlatformKind.Unknown)]
    public void Processor_names_map_to_the_memory_platform(string? cpu, RamPlatformKind kind) =>
        Assert.Equal(kind, RamPlatformDetector.Detect(cpu, null).Kind);

    [Fact]
    public void Amd_platform_carries_generation_and_x3d_from_cpu_traits()
    {
        var p = RamPlatformDetector.Detect("AMD Ryzen 7 9800X3D 8-Core Processor", "DDR5");
        Assert.Equal(ZenGeneration.Zen5, p.Zen);
        Assert.True(p.IsX3D);
        Assert.True(p.IsAmd);
        Assert.False(p.NeedsIntelMicrocodeFix);
    }
}

public class RamIcCatalogTests
{
    [Theory]
    [InlineData("F5-6000J3038F16GX2-TZ5RK", "G.Skill", "DDR5", 16, DramIc.HynixDdr5Gen16, IcConfidence.Likely)]
    [InlineData("F5-6400J3239G16GX2-TZ5RK", "G.Skill", "DDR5", 16, DramIc.HynixDdr5Gen16, IcConfidence.Likely)]
    [InlineData("F5-7200J3646F24GX2-TZ5RK", "G.Skill", "DDR5", 24, DramIc.HynixDdr5Gen24, IcConfidence.Likely)]
    [InlineData("F5-6000J3040G32GX2-FX5", "G.Skill", "DDR5", 32, DramIc.Unknown, IcConfidence.Unknown)]
    [InlineData("F5-6000J3636F16GX2-TZ5RK", "G.Skill", "DDR5", 16, DramIc.Unknown, IcConfidence.Unknown)]
    [InlineData("KF560C30BBEK2-32", "Kingston", "DDR5", 16, DramIc.HynixDdr5Gen16, IcConfidence.Likely)]
    [InlineData("CP2K16G60C36U5B", "Crucial Technology", "DDR5", 16, DramIc.MicronDdr5, IcConfidence.Known)]
    [InlineData("CT16G48C40U5", "Micron", "DDR5", 16, DramIc.MicronDdr5, IcConfidence.Known)]
    [InlineData("HMCG78AGBUA081N", "SK Hynix", "DDR5", 16, DramIc.HynixDdr5Gen16, IcConfidence.Known)]
    [InlineData("CMK32GX5M2B6000C30", "Corsair", "DDR5", 16, DramIc.HynixDdr5Gen16, IcConfidence.Likely)]
    [InlineData("F4-3200C14D-16GTZ", "G.Skill", "DDR4", 8, DramIc.SamsungBDie, IcConfidence.Likely)]
    [InlineData("F4-3600C14D-16GTZN", "G.Skill", "DDR4", 8, DramIc.SamsungBDie, IcConfidence.Likely)]
    [InlineData("F4-3600C16D-16GVKC", "G.Skill", "DDR4", 8, DramIc.Unknown, IcConfidence.Unknown)]
    [InlineData("BL2K8G32C16U4B", "Crucial", "DDR4", 8, DramIc.MicronDdr4, IcConfidence.Known)]
    [InlineData("", null, "DDR5", 16, DramIc.Unknown, IcConfidence.Unknown)]
    public void Part_numbers_map_to_the_likely_chip(string part, string? maker, string type, int gb, DramIc ic, IcConfidence confidence)
    {
        var guess = RamIcCatalog.Guess(part, maker, type, gb * RamTuningSamples.Gb);
        Assert.Equal(ic, guess.Ic);
        Assert.Equal(confidence, guess.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(guess.Reason));
    }

    [Fact]
    public void Unknown_corsair_kits_explain_the_label_version()
    {
        var guess = RamIcCatalog.Guess("CMK32GX5M2B5600C36", "Corsair", "DDR5", 16 * RamTuningSamples.Gb);
        Assert.Equal(DramIc.Unknown, guess.Ic);
        Assert.Contains("5.xx SK hynix", guess.Reason);
    }

    [Theory]
    [InlineData("F5-6000J3038F16GX2-TZ5RK", 6000, 30, 38)]
    [InlineData("KF560C30BBEK2-32", 6000, 30, null)]
    [InlineData("CMK32GX5M2B6000C30", 6000, 30, null)]
    [InlineData("CP16G60C36U5B", 6000, 36, null)]
    [InlineData("F4-3600C16D-16GTZNC", 3600, 16, null)]
    [InlineData("FF3D532G6000HC30DC01", 6000, 30, null)]
    public void Timings_are_read_from_part_numbers(string part, int mts, int cl, int? trcd)
    {
        var t = RamIcCatalog.ParseTimings(part);
        Assert.Equal(mts, t.RatedMts);
        Assert.Equal(cl, t.Cl);
        Assert.Equal(trcd, t.Trcd);
    }

    [Theory]
    [InlineData(180, 6000, 540)]
    [InlineData(160, 6000, 480)]
    [InlineData(280, 3600, 504)]
    [InlineData(160, 6400, 512)]
    public void Refresh_nanoseconds_become_clocks(double ns, int mts, int clocks) => Assert.Equal(clocks, RamIcCatalog.NsToClocks(ns, mts));

    [Fact]
    public void Trfc_values_only_exist_for_well_known_chips_and_stay_below_jedec()
    {
        Assert.NotNull(RamIcCatalog.TrfcNs(DramIc.HynixDdr5Gen16));
        Assert.NotNull(RamIcCatalog.TrfcNs(DramIc.SamsungBDie));
        Assert.Null(RamIcCatalog.TrfcNs(DramIc.Unknown));
        Assert.Null(RamIcCatalog.TrfcNs(DramIc.MicronDdr5));
        Assert.Null(RamIcCatalog.TrfcNs(DramIc.HynixDdr5Gen24));
        var (start, tight) = RamIcCatalog.TrfcNs(DramIc.HynixDdr5Gen16)!.Value;
        Assert.True(start > tight);
        Assert.True(start < 295, "JEDEC 16 Gbit tRFC is 295 ns");
    }
}

public class RamLayoutTests
{
    [Theory]
    [InlineData("DIMM_A2", null, 'A', 2)]
    [InlineData("DDR5_B1", "BANK 1", 'B', 1)]
    [InlineData("Controller1-ChannelA-DIMM1", "BANK 0", 'B', 2)]
    [InlineData("Controller0-ChannelA-DIMM0", "BANK 0", 'A', 1)]
    [InlineData("DIMM 1", "P0 CHANNEL B", 'B', 2)]
    [InlineData("ChannelA-DIMM0", "BANK 0", 'A', 1)]
    public void Slot_names_give_channel_and_slot(string locator, string? bank, char channel, int slot)
    {
        var (c, s) = RamLayoutRules.ParseSlot(locator, bank);
        Assert.Equal(channel, c);
        Assert.Equal(slot, s);
    }

    [Fact]
    public void Unreadable_slot_names_stay_unknown()
    {
        Assert.Equal(((char?)null, (int?)null), RamLayoutRules.ParseSlot("DIMM1", "BANK 0"));
        Assert.Equal(((char?)null, (int?)null), RamLayoutRules.ParseSlot(null, null));
    }

    private static RamModule M(string slot, char? ch, int? n, string part = "F5-6000J3038F16G", long size = 16 * RamTuningSamples.Gb) =>
        new(slot, ch, n, size, 1, part, "G.Skill", 6000, 6000, "DDR5", RamIcGuess.Unknown("test"));

    [Fact]
    public void Two_modules_in_a2_and_b2_are_dual_channel_in_the_right_slots()
    {
        var layout = RamLayoutRules.Evaluate([M("A2", 'A', 2), M("B2", 'B', 2)], 4);
        Assert.Equal(ChannelMode.Dual, layout.Mode);
        Assert.True(layout.RecommendedSlots);
        Assert.False(layout.MixedKits);
    }

    [Fact]
    public void Two_modules_on_one_channel_are_single_channel()
    {
        var layout = RamLayoutRules.Evaluate([M("A1", 'A', 1), M("A2", 'A', 2)], 4);
        Assert.Equal(ChannelMode.Single, layout.Mode);
        Assert.False(layout.RecommendedSlots);
        Assert.Contains("A2 and B2", layout.SlotAdvice);
    }

    [Fact]
    public void Two_modules_in_the_first_slots_get_slot_advice_and_one_module_is_single()
    {
        var first = RamLayoutRules.Evaluate([M("A1", 'A', 1), M("B1", 'B', 1)], 4);
        Assert.Equal(ChannelMode.Dual, first.Mode);
        Assert.False(first.RecommendedSlots);

        var one = RamLayoutRules.Evaluate([M("A2", 'A', 2)], 4);
        Assert.Equal(ChannelMode.Single, one.Mode);
        Assert.False(one.RecommendedSlots);
    }

    [Fact]
    public void Different_part_numbers_or_sizes_are_mixed_kits()
    {
        Assert.True(RamLayoutRules.Evaluate([M("A2", 'A', 2), M("B2", 'B', 2, part: "CMK32GX5M2B6000C30")], 4).MixedKits);
        Assert.True(RamLayoutRules.IsMixed([M("A2", 'A', 2), M("B2", 'B', 2, size: 32 * RamTuningSamples.Gb)]));
        Assert.False(RamLayoutRules.IsMixed([M("A2", 'A', 2), M("B2", 'B', 2)]));
    }

    [Fact]
    public void Two_slot_boards_need_no_slot_advice() =>
        Assert.True(RamLayoutRules.Evaluate([M("A1", 'A', 1), M("B1", 'B', 1)], 2).RecommendedSlots);
}

public class RamTuningRulesTests
{
    [Fact]
    public void Am5_targets_6000_one_to_one_with_fclk_2000()
    {
        var t = RamTuningRules.TargetFor(RamPlatformDetector.Detect("AMD Ryzen 7 7800X3D 8-Core Processor", "DDR5"), 2, false, 4)!;
        Assert.Equal(6000, t.SafeMts);
        Assert.Equal(6400, t.StretchMts);
        Assert.Equal(2000, t.FclkMhz);
        Assert.Equal(2100, t.FclkStretchMhz);
        Assert.Equal(5200, t.OfficialMts);
        Assert.Equal(5200, RamTuningRules.TargetFor(RamPlatformDetector.Detect("AMD Ryzen 7 7800X3D", "DDR5"), 4, true, 4)!.SafeMts);
        Assert.Equal(5600, RamTuningRules.TargetFor(RamPlatformDetector.Detect("AMD Ryzen 9 9950X", "DDR5"), 2, false, 4)!.OfficialMts);
    }

    [Fact]
    public void Am4_targets_3600_with_fclk_1800_and_zen3_stretches_further()
    {
        var zen3 = RamTuningRules.TargetFor(RamPlatformDetector.Detect("AMD Ryzen 7 5800X", "DDR4"), 2, false, 4)!;
        Assert.Equal(3600, zen3.SafeMts);
        Assert.Equal(1800, zen3.FclkMhz);
        Assert.Equal(3800, zen3.StretchMts);
        Assert.Equal(1900, zen3.FclkStretchMhz);
        var zen2 = RamTuningRules.TargetFor(RamPlatformDetector.Detect("AMD Ryzen 5 3600", "DDR4"), 2, false, 4)!;
        Assert.Equal(3733, zen2.StretchMts);
        Assert.Equal(3200, RamTuningRules.TargetFor(RamPlatformDetector.Detect("AMD Ryzen 5 3600", "DDR4"), 4, true, 4)!.SafeMts);
    }

    [Fact]
    public void Intel_targets_depend_on_generation_slots_and_modules()
    {
        var raptor = RamPlatformDetector.Detect("Intel Core i7-13700K", "DDR5");
        Assert.Equal(6000, RamTuningRules.TargetFor(raptor, 2, false, 4)!.SafeMts);
        Assert.Equal(6800, RamTuningRules.TargetFor(raptor, 2, false, 4)!.StretchMts);
        Assert.Equal(7200, RamTuningRules.TargetFor(raptor, 2, false, 2)!.StretchMts);
        Assert.Equal(5200, RamTuningRules.TargetFor(raptor, 4, false, 4)!.SafeMts);
        Assert.Equal(5600, RamTuningRules.TargetFor(RamPlatformDetector.Detect("Intel Core i5-12600K", "DDR5"), 2, false, 4)!.SafeMts);
        Assert.Equal("Gear 1", RamTuningRules.TargetFor(RamPlatformDetector.Detect("Intel Core i7-13700K", "DDR4"), 2, false, 4)!.RatioText);
        Assert.Equal(8000, RamTuningRules.TargetFor(RamPlatformDetector.Detect("Intel(R) Core(TM) Ultra 9 285K", "DDR5"), 2, false, 4)!.StretchMts);
        Assert.Null(RamTuningRules.TargetFor(RamPlatformDetector.Detect("Intel(R) Core(TM) Ultra 7 155H", "DDR5"), 2, false, 2));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D", "DDR5")]
    [InlineData("AMD Ryzen 9 9950X", "DDR5")]
    [InlineData("AMD Ryzen 7 5800X", "DDR4")]
    [InlineData("AMD Ryzen 7 2700X", "DDR4")]
    [InlineData("Intel Core i7-13700K", "DDR5")]
    [InlineData("Intel Core i7-13700K", "DDR4")]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", "DDR5")]
    public void Targets_are_ordered_and_one_to_one_on_am5(string cpu, string type)
    {
        var platform = RamPlatformDetector.Detect(cpu, type);
        foreach (int modules in new[] { 1, 2, 4 })
        {
            var t = RamTuningRules.TargetFor(platform, modules, modules == 4, 4)!;
            Assert.True(t.StretchMts >= t.SafeMts);
            if (modules <= 2)
            {
                // The official speeds are for one module per channel; four modules may sit below them.
                Assert.True(t.SafeMts >= t.OfficialMts);
            }

            if (platform.Kind == RamPlatformKind.Am5)
            {
                Assert.True(t.StretchMts <= 6400, "AM5 1:1 tops out around 6400");
                Assert.True(t.FclkStretchMhz <= 2100);
            }
        }
    }

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D", "DDR5", DramIc.HynixDdr5Gen16)]
    [InlineData("AMD Ryzen 7 5800X", "DDR4", DramIc.SamsungBDie)]
    [InlineData("AMD Ryzen 7 5800X", "DDR4", DramIc.Unknown)]
    [InlineData("Intel Core i7-13700K", "DDR5", DramIc.HynixDdr5Gen16)]
    [InlineData("Intel Core i7-13700K", "DDR4", DramIc.Unknown)]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", "DDR5", DramIc.MicronDdr5)]
    public void Voltage_ceilings_never_exceed_the_vendor_limits(string cpu, string type, DramIc ic)
    {
        var platform = RamPlatformDetector.Detect(cpu, type);
        var limits = RamTuningRules.VoltageLimits(platform, ic);
        Assert.NotEmpty(limits);
        foreach (var limit in limits)
        {
            Assert.InRange(limit.DailyMaxVolts, 0.8, 1.50);
            if (limit.Name.StartsWith("VSOC", StringComparison.Ordinal))
            {
                Assert.True(limit.DailyMaxVolts <= (platform.Kind == RamPlatformKind.Am5 ? 1.30 : 1.20));
            }

            if (limit.Name.Contains("DRAM", StringComparison.Ordinal) && type == "DDR5")
            {
                Assert.True(limit.DailyMaxVolts <= 1.40, "DDR5 daily ceiling is 1.40 V; 1.45 V for hynix is only a note");
            }

            if (limit.Name.StartsWith("VCCSA", StringComparison.Ordinal))
            {
                Assert.True(limit.DailyMaxVolts <= (platform.Kind == RamPlatformKind.IntelArrowLake ? 1.20 : 1.25));
            }

            if (limit.Name.StartsWith("VDIMM", StringComparison.Ordinal))
            {
                Assert.True(limit.DailyMaxVolts <= (ic == DramIc.SamsungBDie ? 1.50 : 1.45));
            }
        }
    }

    [Fact]
    public void Am5_vsoc_limit_is_amds_1_30_volts() => Assert.Equal(1.30, RamTuningRules.Am5VsocMax);
}

public class BiosMenuPathTests
{
    [Fact]
    public void Profile_paths_follow_the_board_maker_and_platform()
    {
        Assert.Equal("Ai Tweaker > Ai Overclock Tuner > XMP I",
            BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Asus, "ROG STRIX Z790-E GAMING WIFI", RamPlatformKind.IntelLga1700Ddr5));
        Assert.Equal("Extreme Tweaker > Ai Overclock Tuner > EXPO I",
            BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Asus, "ROG CROSSHAIR X670E HERO", RamPlatformKind.Am5));
        Assert.Equal("Ai Tweaker > Ai Overclock Tuner > D.O.C.P.",
            BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Asus, "TUF GAMING B550-PLUS", RamPlatformKind.Am4));
        Assert.Equal("OC > Extreme Memory Profile (XMP/EXPO)", BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Msi, "MAG X870 TOMAHAWK WIFI", RamPlatformKind.Am5));
        Assert.Equal("OC > Extreme Memory Profile (XMP)", BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Msi, "PRO Z790-A", RamPlatformKind.IntelLga1700Ddr5));
        Assert.StartsWith("Tweaker > Extreme Memory Profile (X.M.P.)", BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Gigabyte, "B650 AORUS ELITE AX", RamPlatformKind.Am5));
        Assert.Equal("OC Tweaker > DRAM Configuration > Load EXPO Setting", BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.AsRock, "B650E Steel Legend", RamPlatformKind.Am5));
        Assert.Contains("XMP, EXPO", BiosMenuPaths.For(BiosSetting.MemoryProfile, PcMaker.Unknown, null, RamPlatformKind.Am5));
    }

    [Fact]
    public void Other_settings_name_the_overclocking_tab_or_explain_where_to_look()
    {
        Assert.Equal("OC > FCLK Frequency", BiosMenuPaths.For(BiosSetting.Fclk, PcMaker.Msi, null, RamPlatformKind.Am5));
        Assert.Equal("Ai Tweaker > Performance Preferences > Intel Default Settings",
            BiosMenuPaths.For(BiosSetting.IntelDefaults, PcMaker.Asus, "PRIME Z790-P", RamPlatformKind.IntelLga1700Ddr5));
        Assert.Contains("Memory Context Restore", BiosMenuPaths.For(BiosSetting.MemoryContextRestore, PcMaker.Gigabyte, null, RamPlatformKind.Am5));
        Assert.Contains("\"FCLK Frequency\"", BiosMenuPaths.For(BiosSetting.Fclk, PcMaker.Unknown, null, RamPlatformKind.Am5));
        Assert.Equal("M-Flash", BiosMenuPaths.For(BiosSetting.BiosUpdate, PcMaker.Msi, null, RamPlatformKind.Am5));
        Assert.Contains("F7", BiosMenuPaths.EnterBios(PcMaker.Asus));
    }
}
