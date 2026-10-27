using PcChecker.Core.Cleanup;
using PcChecker.Core.Debloat;
using PcChecker.Core.Drivers;
using PcChecker.Core.Fans;
using PcChecker.Core.Hardware;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Processes;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class AmdReviewFixTests
{
    private const string Ryzen7950X = "AMD Ryzen 9 7950X 16-Core Processor";

    private static SensorReading Temp(string name, HardwareKind kind, double value, string hardware) =>
        new($"{hardware}/{name}", hardware, kind, SensorKind.Temperature, name, value, value, value, "°C");

    [Fact]
    public void Crash_defender_tweak_targets_the_user_mode_service_and_never_its_kernel_drivers()
    {
        var tweak = DebloatCatalog.Find("amd.crashdefender")!;
        var service = Assert.IsType<ServiceOp>(Assert.Single(tweak.Operations));
        Assert.Equal("AMD Crash Defender Service", service.Name);
        Assert.Contains("amdfendr", DebloatCatalog.NeverDisableVendorServices);
        Assert.Contains("amdfendrmgr", DebloatCatalog.NeverDisableVendorServices);
        Assert.DoesNotContain(ProcessCatalog.All, e => e.ExeName.Equals("amdfendrmgr", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ProcessCatalog.All, e => e.ExeName.Equals("amdfendrsr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Game_bar_recording_tweak_warns_on_dual_ccd_x3d_like_the_optimize_page()
    {
        const string x3d = "AMD Ryzen 9 9950X3D 16-Core Processor";
        var gameDvr = DebloatCatalog.Find("gaming.gamedvr")!;
        var gameMode = DebloatCatalog.Find("gaming.gamemode")!;
        Assert.True(DebloatConfirmationText.X3dGameBarConflict([gameDvr], x3d));
        Assert.False(DebloatConfirmationText.X3dGameBarConflict([gameMode], x3d));
        Assert.False(DebloatConfirmationText.X3dGameBarConflict([gameDvr], "AMD Ryzen 7 9800X3D 8-Core Processor"));
        Assert.StartsWith("Warning:", DebloatConfirmationText.Build([gameDvr], revert: false, cpuName: x3d));
        Assert.False(DebloatConfirmationText.Build([gameDvr], revert: true, cpuName: x3d).StartsWith("Warning:", StringComparison.Ordinal));
    }

    [Fact]
    public void Integrated_radeon_sensors_get_the_ryzen_limits_of_the_cpu_in_the_same_snapshot()
    {
        var settings = new MonitoringSettings();
        var cpu = Temp("Core (Tctl/Tdie)", HardwareKind.Cpu, 70, Ryzen7950X);
        var igpu = Temp("GPU Core", HardwareKind.Gpu, 96, "AMD Radeon(TM) Graphics");

        Assert.Equal(Ryzen7950X, TemperatureRoles.CpuNameOf([igpu, cpu]));
        Assert.Null(TemperatureRoles.CpuNameOf([igpu]));
        Assert.Equal((TemperatureRoles.BoostToTjMaxWarnC, TemperatureRoles.BoostToTjMaxCritC), TemperatureRoles.ThresholdsFor(igpu, settings, Ryzen7950X));
        Assert.Equal((settings.CpuWarnC, settings.CpuCritC), TemperatureRoles.ThresholdsFor(igpu, settings));

        Assert.Null(FanSafetyRules.FindCriticalReading([igpu, cpu], settings));
        Assert.NotNull(FanSafetyRules.FindCriticalReading([igpu], settings));
    }

    [Fact]
    public void Alerts_use_the_cpu_name_for_integrated_gpu_sensors()
    {
        var settings = new MonitoringSettings();
        var igpu = Temp("GPU Core", HardwareKind.Gpu, 88, "AMD Radeon(TM) Graphics");
        Assert.Null(new AlertTracker().Evaluate(igpu, settings, DateTimeOffset.UtcNow, Ryzen7950X));
        Assert.NotNull(new AlertTracker().Evaluate(igpu, settings, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", true)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", true)]
    [InlineData("AMD Ryzen 7 9700X 8-Core Processor", true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 9 9955HX3D 16-Core Processor", false)]
    public void Ryzen_9000x3d_boosts_to_tjmax_and_keeps_the_ryzen_limits(string cpu, bool boosts)
    {
        Assert.Equal(boosts, CpuTraits.Parse(cpu).BoostsToTjMax);
    }

    [Theory]
    [InlineData("AMD Ryzen 9 9850HX", true)]
    [InlineData("AMD Ryzen 9 9955HX3D 16-Core Processor", true)]
    [InlineData("AMD Ryzen 9 8945HX with Radeon Graphics", true)]
    [InlineData("AMD Ryzen 9 7945HX with Radeon Graphics", true)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", false)]
    [InlineData("AMD Ryzen 7 8845HS w/ Radeon 780M Graphics", false)]
    public void Dragon_and_fire_range_hx_chips_are_not_offered_ryzenadj(string cpu, bool dragonRange)
    {
        var traits = CpuTraits.Parse(cpu);
        Assert.Equal(dragonRange, traits.IsDragonRange);
        Assert.Equal(!dragonRange, traits.IsRyzenMobileOrApu);
    }

    [Theory]
    [InlineData("amd.installer", "amd-software-adrenalin-edition-25.9.1-win10-win11-sep-rdna")]
    [InlineData("amd.installer", "amd_chipset_software_7.06.02.123")]
    [InlineData("nvidia.installer", "581.42-desktop-win10-win11-64bit-international-dch-whql")]
    [InlineData("nvidia.downloader", "NVIDIA_app_v11.0.5.266")]
    [InlineData("nvidia.installer", "581.42-notebook-win10-win11-64bit-international-dch-whql.exe")]
    public void Versioned_vendor_self_extractors_pause_installer_folder_cleanup(string category, string process)
    {
        Assert.True(CleanupRules.IsVersionedInstallerName(process.EndsWith(".exe", StringComparison.Ordinal) ? process[..^4] : process));
        Assert.NotNull(CleanupRules.BrowserRunningReason(category, [process]));
        Assert.Null(CleanupRules.BrowserRunningReason(category, ["chrome", "explorer"]));
        Assert.Null(CleanupRules.BrowserRunningReason("browser.chrome.cache", [process]));
    }

    [Fact]
    public void Amd_chipset_offers_are_flagged_with_chipset_software_alone()
    {
        var chipset = new DriverUpdateInfo("1", "Advanced Micro Devices, Inc. - System - 1.2.0.120", null, null, null, 0, DriverUpdateSource.WindowsUpdate, null, null, null, false, false, false)
        {
            Provider = "Advanced Micro Devices, Inc.",
            DriverClass = "System",
        };
        var display = chipset with { Title = "Advanced Micro Devices, Inc. - Display - 31.0.21921.1000", DriverClass = "Display" };

        Assert.Contains("Chipset", DriverUpdateRules.VendorWarning(chipset, amdSoftware: false, nvidiaSoftware: true, amdChipsetSoftware: true));
        Assert.Null(DriverUpdateRules.VendorWarning(chipset, amdSoftware: false, nvidiaSoftware: true, amdChipsetSoftware: false));
        Assert.Null(DriverUpdateRules.VendorWarning(display, amdSoftware: false, nvidiaSoftware: true, amdChipsetSoftware: true));
        Assert.Single(DriverUpdateRules.Annotate([chipset, display], false, true, true), u => u.VendorWarning is not null);
    }

    [Fact]
    public void Vendor_detection_is_one_rule_for_drivers_and_debloat()
    {
        string controlPanel = VendorSoftware.Expand(VendorSoftwareCatalog.NvidiaControlPanelFolders[0]);
        Assert.True(VendorSoftwareCatalog.NvidiaDriverToolsInstalled(_ => false, d => d == controlPanel));
        Assert.False(VendorSoftwareCatalog.NvidiaDriverToolsInstalled(_ => false, _ => false));

        string chipset = VendorSoftware.Expand(VendorSoftwareCatalog.AmdChipsetSoftwareFolders[0]);
        Assert.True(VendorSoftwareCatalog.AmdChipsetSoftwareInstalled(d => d == chipset));
        Assert.False(VendorSoftwareCatalog.AmdChipsetSoftwareInstalled(_ => false));
    }

    [Fact]
    public void Gpu_state_is_stock_only_when_the_voltage_offset_is_back_to_zero()
    {
        var stock = new GpuTunerState(0, 0, 100, null, null, null, null, null, null);
        Assert.True((stock with { VoltageOffsetMv = 0 }).IsStock(expectVoltageOffset: true));
        Assert.False((stock with { VoltageOffsetMv = -50 }).IsStock(expectVoltageOffset: true));
        Assert.False((stock with { VoltageOffsetMv = -50 }).IsStock(expectVoltageOffset: false));
        Assert.False(stock.IsStock(expectVoltageOffset: true));
        Assert.True(stock.IsStock(expectVoltageOffset: false));
        Assert.False((stock with { CoreOffsetMhz = 100, VoltageOffsetMv = 0 }).IsStock(expectVoltageOffset: false));
    }
}
