using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Memory;
using PcChecker.Core.Overclocking;
using Xunit;

namespace PcChecker.Core.Tests;

public class AmdMemorySpeedTests
{
    [Theory]
    [InlineData("F5-6000J3038F16G", 6000)]
    [InlineData("F4-3600C16-16GTZNC", 3600)]
    [InlineData("KF560C36-16", 6000)]
    [InlineData("KF436C16BB/16", 3600)]
    [InlineData("CMK32GX5M2B6000C36", 6000)]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("CP16G56C46U5", 5600)]
    [InlineData("BL2K16G36C16U4B", 3600)]
    [InlineData("M425R2GA3BB0-CQKOL", null)]
    [InlineData(null, null)]
    public void Rating_is_decoded_from_the_part_number(string? part, int? expected) =>
        Assert.Equal(expected, MemorySpeedRules.RatedFromPartNumber(part));

    [Fact]
    public void Jedec_speed_reported_as_rated_is_not_called_enabled()
    {
        // EXPO off on an AM5 board that reports the SPD base speed: configured 4800 = Speed 4800, part number says 6000.
        var withPart = MemorySpeedRules.Evaluate([new ModuleSpeed(4800, 4800, "F5-6000J3038F16G")], "DDR5");
        Assert.Equal(MemorySpeedVerdict.Disabled, withPart.State);
        Assert.Equal(6000, withPart.RatedMhz);
        Assert.True(withPart.RatedFromPartNumber);

        var unknownPart = MemorySpeedRules.Evaluate([new ModuleSpeed(4800, 4800, "ABC123")], "DDR5");
        Assert.Equal(MemorySpeedVerdict.Unknown, unknownPart.State);
        Assert.True(unknownPart.JedecOnly);

        Assert.Equal(MemorySpeedVerdict.Enabled, MemorySpeedRules.Evaluate([new ModuleSpeed(6000, 6000, null)], "DDR5").State);
        Assert.Equal(MemorySpeedVerdict.Enabled, MemorySpeedRules.Evaluate([new ModuleSpeed(4800, 4800, "CT16G48C40U5")], "DDR5").State);

        // The Memory and Overclock pages agree.
        var memory = MemoryRules.EvaluateXmp([Module(4800, 4800, "ABC123")], "AMD", "AMD Ryzen 7 7700X");
        Assert.Equal("Unknown", memory.State);
        Assert.Contains("check the BIOS", memory.Title);
        Assert.Equal(Overclocking.XmpEvaluation.Unknown, XmpStateEvaluator.Evaluate([new ModuleSpeed(4800, 4800, "ABC123")], "DDR5").State);
    }

    [Fact]
    public void Am5_expo_advice_mentions_memory_training_and_fabric_ratio()
    {
        var off = MemoryRules.EvaluateXmp([Module(4800, 6000, "F5-6000J3038F16G")], "AMD", "AMD Ryzen 7 7800X3D 8-Core Processor");
        Assert.Equal("Disabled", off.State);
        Assert.Contains("1–3 minutes", off.Guidance);
        Assert.DoesNotContain("fails to boot", off.Guidance);

        var on = MemoryRules.EvaluateXmp([Module(6000, 6000, "F5-6000J3038F16G")], "AMD", "AMD Ryzen 7 7800X3D 8-Core Processor");
        Assert.Contains("sweet spot", on.Guidance);

        Assert.Contains("1:2", MemorySpeedRules.AmdFabricAdvice("AMD Ryzen 9 9950X", "DDR5", 6400));
        Assert.Contains("decouples", MemorySpeedRules.AmdFabricAdvice("AMD Ryzen 7 5800X3D", "DDR4", 4000));
        Assert.Contains("FCLK = 1800", MemorySpeedRules.AmdFabricAdvice("AMD Ryzen 5 5600X", "DDR4", 3600));
        Assert.Null(MemorySpeedRules.AmdFabricAdvice("Intel(R) Core(TM) i7-13700K", "DDR5", 6000));
        Assert.Null(MemorySpeedRules.TrainingNote("Intel(R) Core(TM) i7-13700K", "DDR5"));
    }

    private static MemoryModuleInfo Module(int configured, int rated, string part) =>
        new("DIMM", "G.Skill", null, "OK", "BANK 0", "DIMM_A2", 16L * 1024 * 1024 * 1024, rated, configured, "DDR5", "DIMM", part, "0001", 1350, 64, 64,
            new Dictionary<string, string>());
}

public class AmdDriverRulesTests
{
    private static DriverPackage Package(string published, string original, DateTime date, string version) =>
        new(published, original, "Advanced Micro Devices, Inc.", "Display", "{4D36E968-E325-11CE-BFC1-08002BE10318}", version, date, "Microsoft Windows Hardware Compatibility Publisher");

    [Fact]
    public void Older_adrenalin_display_packages_are_superseded_despite_versioned_inf_names()
    {
        var older = Package("oem41.inf", "u0401234.inf", new DateTime(2024, 5, 1), "31.0.24033.1003");
        var newer = Package("oem77.inf", "u0412345.inf", new DateTime(2025, 2, 10), "32.0.13031.3015");
        Assert.True(DriverCleanupRules.IsAmdVersionedDisplayPackage(older));
        Assert.Equal(DriverCleanupRules.GroupKey(older), DriverCleanupRules.GroupKey(newer));
        var superseded = DriverCleanupRules.FindSuperseded([older, newer]);
        Assert.Equal("oem77.inf", superseded["oem41.inf"].PublishedName);
        Assert.False(superseded.ContainsKey("oem77.inf"));

        // Other vendors still group by INF name.
        var nv = older with { Provider = "NVIDIA", OriginalName = "nv_dispig.inf" };
        Assert.False(DriverCleanupRules.IsAmdVersionedDisplayPackage(nv));
    }

    [Fact]
    public void Amd_chipset_components_are_not_old_drivers()
    {
        var smbus = new DriverInfo("AMD SMBus", @"PCI\VEN_1022&DEV_790B", "System", "Advanced Micro Devices, Inc.", "5.12.0.38",
            new DateTime(2019, 1, 1), "oem12.inf", true, "Microsoft", false, [], "OK", 0, null);
        Assert.True(DriverRules.IsAmdChipsetComponent(smbus));
        Assert.False(DriverRules.IsOld(smbus));
        Assert.DoesNotContain(DriverRules.Evaluate(smbus), f => f.Title == "Old driver");

        var other = smbus with { DeviceName = "Some sound card", DeviceClass = "MEDIA", Provider = "Contoso" };
        Assert.True(DriverRules.IsOld(other));
    }

    [Fact]
    public void Ryzen_with_geforce_gets_a_chipset_link_not_a_radeon_note()
    {
        var inventory = new HardwareInventory();
        inventory.Processors.Add(new CpuInfo("AMD Ryzen 7 7800X3D 8-Core Processor", "AuthenticAMD", null, null, null, null, "AM5", 8, 16, 4200, null, null, null, null, null, null, null, null, null, new Dictionary<string, string>()));
        inventory.Graphics.Add(new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", null, null, null, null, null, null, null, null, null, null, @"PCI\VEN_10DE&DEV_2786", "NVIDIA", false, new Dictionary<string, string>()));
        var links = DriverRules.VendorLinksFor(inventory);
        var amd = Assert.Single(links, l => l.Vendor == "AMD");
        Assert.Equal(DriverRules.AmdChipsetComponent, amd.Component);
        Assert.Contains("AM5", amd.Note);
        Assert.DoesNotContain("Radeon", amd.Note);

        inventory.Graphics.Clear();
        inventory.Graphics.Add(new GpuInfo("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc.", null, null, null, null, null, null, null, null, null, null, @"PCI\VEN_1002&DEV_747E", "AMD", false, new Dictionary<string, string>()));
        var both = DriverRules.VendorLinksFor(inventory);
        Assert.Contains(both, l => l.Component == DriverRules.AmdGraphicsComponent);
        Assert.Contains(both, l => l.Component == DriverRules.AmdChipsetComponent);
    }

    [Fact]
    public void Windows_update_display_offers_are_flagged_when_the_vendor_tool_is_installed()
    {
        var amdDisplay = new DriverUpdateInfo("1", "Advanced Micro Devices, Inc. - Display - 31.0.21921.1000", null, null, null, 0, DriverUpdateSource.WindowsUpdate, null, null, null, false, false, false)
        {
            Provider = "Advanced Micro Devices, Inc.",
        };
        Assert.Equal("Display", DriverUpdateRules.ClassOf(amdDisplay));
        Assert.Contains("AMD Software", DriverUpdateRules.VendorWarning(amdDisplay, amdSoftware: true, nvidiaSoftware: false));
        Assert.Null(DriverUpdateRules.VendorWarning(amdDisplay, amdSoftware: false, nvidiaSoftware: false));

        var chipset = amdDisplay with { Title = "Advanced Micro Devices, Inc. - System - 1.2.0.120", DriverClass = "System" };
        Assert.Contains("Chipset", DriverUpdateRules.VendorWarning(chipset, true, false));

        var nvidia = amdDisplay with { Title = "NVIDIA - Display - 32.0.15.6094", Provider = "NVIDIA" };
        Assert.NotNull(DriverUpdateRules.VendorWarning(nvidia, false, true));
        var realtek = amdDisplay with { Title = "Realtek Semiconductor Corp. - MEDIA - 6.0.9728.1", Provider = "Realtek Semiconductor Corp." };
        Assert.Null(DriverUpdateRules.VendorWarning(realtek, true, true));
        Assert.Single(DriverUpdateRules.Annotate([amdDisplay, realtek], true, true), u => u.VendorWarning is not null);
    }

    [Fact]
    public async Task Demo_service_hides_and_unhides_updates()
    {
        var svc = new DemoDriverService();
        var check = await svc.CheckForUpdatesAsync(null, CancellationToken.None);
        var flagged = check.Updates.Single(u => u.VendorWarning is not null);
        Assert.True((await svc.SetHiddenAsync([flagged], true, CancellationToken.None)).Success);
        Assert.Single(await svc.GetHiddenAsync(CancellationToken.None));
        Assert.DoesNotContain((await svc.CheckForUpdatesAsync(null, CancellationToken.None)).Updates, u => u.Id == flagged.Id);
        Assert.True((await svc.SetHiddenAsync([flagged], false, CancellationToken.None)).Success);
        Assert.Empty(await svc.GetHiddenAsync(CancellationToken.None));
    }
}
