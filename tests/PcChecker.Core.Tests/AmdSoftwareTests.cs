using PcChecker.Core.Cleanup;
using PcChecker.Core.Debloat;
using PcChecker.Core.Gaming;
using PcChecker.Core.Optimization;
using PcChecker.Core.Processes;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class AmdCleanupTests
{
    private static string? Env(string name) => name switch
    {
        "SYSTEMDRIVE" => "C:",
        "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
        "PROGRAMDATA" => @"C:\ProgramData",
        _ => null,
    };

    [Fact]
    public void Installer_folders_are_cleanable_with_safe_roots()
    {
        var amd = CleanupCatalog.Find("amd.installer")!;
        Assert.Equal(RiskLevel.Safe, amd.Risk);
        Assert.True(amd.EnabledByDefault);
        Assert.Equal([@"C:\AMD"], CleanupCatalog.ExpandRoots(amd, Env));
        Assert.Contains(@"C:\Users\me\AppData\Local\AMD\Dx9Cache", CleanupCatalog.ExpandRoots(CleanupCatalog.Find("amd.shader.cache")!, Env));
        Assert.Equal([@"C:\NVIDIA"], CleanupCatalog.ExpandRoots(CleanupCatalog.Find("nvidia.installer")!, Env));
        Assert.Equal(RiskLevel.Moderate, CleanupCatalog.Find("nvidia.downloader")!.Risk);
        Assert.False(CleanupCatalog.Find("nvidia.downloader")!.EnabledByDefault);
        Assert.Equal(["*.log", "*.txt"], CleanupCatalog.Find("amd.logs")!.IncludePatterns);

        // Files inside C:\AMD may go; the drive root and siblings never.
        Assert.True(CleanupSafety.IsPathAllowed(@"C:\AMD\AMD-Software-Installer\Packages\Drivers\u0412345.inf", [@"C:\AMD"]));
        Assert.False(CleanupSafety.IsPathAllowed(@"C:\AMDTools\x.txt", [@"C:\AMD"]));
        Assert.False(CleanupSafety.IsPathAllowed(@"C:\pagefile.sys", [@"C:\"]));
    }

    [Fact]
    public void Installer_categories_are_skipped_while_an_installer_runs()
    {
        Assert.Contains("AMD installer", CleanupRules.BrowserRunningReason("amd.installer", ["AMDSoftwareInstaller.exe"]));
        Assert.Null(CleanupRules.BrowserRunningReason("amd.installer", ["explorer"]));
        Assert.Contains("NVIDIA installer", CleanupRules.BrowserRunningReason("nvidia.installer", ["setup"]));
        Assert.Contains("AMD Software", CleanupRules.BrowserRunningReason("amd.logs", ["RadeonSoftware"]));
        Assert.Contains("clean its cache", CleanupRules.BrowserRunningReason("browser.chrome.cache", ["chrome"]));
        Assert.Contains("amd.installer", CleanupRules.PruneAnywhereCategories);
    }
}

public class AmdDebloatTests
{
    [Fact]
    public void Vendor_tweaks_are_offered_only_with_the_vendor_software()
    {
        var vendor = DebloatCatalog.All.Where(t => t.Category == DebloatCatalog.VendorCategory).ToList();
        Assert.Contains(vendor, t => t.Id == "amd.uep");
        Assert.Contains(vendor, t => t.Id == "nvidia.telemetry");
        Assert.All(vendor, t => Assert.True(t.Precondition is DebloatPrecondition.AmdSoftwareInstalled or DebloatPrecondition.NvidiaSoftwareInstalled, t.Id));
        Assert.All(vendor, t => Assert.True(t.Reversible, t.Id));

        Assert.DoesNotContain(DebloatCatalog.Visible(DebloatVendorSoftware.None), t => t.Category == DebloatCatalog.VendorCategory);
        Assert.Contains(DebloatCatalog.Visible(DebloatVendorSoftware.Amd), t => t.Id == "amd.uep");
        Assert.DoesNotContain(DebloatCatalog.Visible(DebloatVendorSoftware.Amd), t => t.Id == "nvidia.telemetry");
        Assert.Equal(DebloatCatalog.All.Count, DebloatCatalog.Visible(DebloatVendorSoftware.All).Count);

        Assert.NotNull(DebloatRules.PreconditionFailure(DebloatCatalog.Find("amd.uep")!, [], DebloatVendorSoftware.Nvidia));
        Assert.Null(DebloatRules.PreconditionFailure(DebloatCatalog.Find("amd.uep")!, [], DebloatVendorSoftware.Amd));
    }

    [Fact]
    public void Vendor_tweaks_never_touch_freesync_x3d_or_driver_services_and_stay_out_of_presets()
    {
        var services = DebloatCatalog.All.SelectMany(t => t.Operations).OfType<ServiceOp>().Select(s => s.Name).ToList();
        Assert.All(DebloatCatalog.NeverDisableVendorServices, name => Assert.DoesNotContain(name, services, StringComparer.OrdinalIgnoreCase));
        var tasks = DebloatCatalog.All.SelectMany(t => t.Operations).OfType<ScheduledTaskOp>().Select(t => t.Path).ToList();
        Assert.DoesNotContain(tasks, p => p.Contains("3dvcache", StringComparison.OrdinalIgnoreCase));

        var presetIds = DebloatPresets.All.SelectMany(p => p.TweakIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("amd.crashdefender", presetIds);
        Assert.True(DebloatCatalog.Find("amd.crashdefender")!.AdvancedOnly);
        Assert.Contains(DebloatSources.VendorApp, DebloatSources.All);
    }

    [Fact]
    public async Task Engine_skips_a_vendor_tweak_when_the_software_is_missing()
    {
        var system = new InMemoryDebloatSystem(DebloatCatalog.Windows11_24H2);
        var engine = new DebloatEngine(system, new DebloatCaptureBook()) { InstalledVendors = DebloatVendorSoftware.None };
        var result = await engine.ApplyAsync(DebloatCatalog.Find("nvidia.telemetry")!, CancellationToken.None);
        Assert.Equal(TweakOutcome.Skipped, result.Outcome);
        Assert.Contains("not installed", result.Message);
    }
}

public class AmdProcessAndStartupTests
{
    [Theory]
    [InlineData("AMDRSSrcExt.exe", "AMD Software tray")]
    [InlineData(@"C:\Program Files\AMD\CNext\CNext\amdow.exe", "AMD Software overlay")]
    [InlineData("amd3dvcacheSvc.exe", "AMD 3D V-Cache Performance Optimizer")]
    [InlineData("AMDNoiseSuppression.exe", "AMD Noise Suppression")]
    [InlineData("AUEPMaster.exe", "AMD User Experience Program")]
    public void Amd_processes_are_explained(string exe, string title) => Assert.Equal(title, ProcessCatalog.Lookup(exe)!.Title);

    [Fact]
    public void Amd_publisher_needs_a_word_or_path_segment()
    {
        Assert.Equal("Advanced Micro Devices, Inc.", StartupPublisherRules.Resolve(null, "AMD Noise Suppression", null));
        Assert.Equal("Advanced Micro Devices, Inc.", StartupPublisherRules.Resolve(null, "Tray", @"C:\Program Files\AMD\CNext\CNext\AMDRSSrcExt.exe"));
        Assert.Equal("Advanced Micro Devices, Inc.", StartupPublisherRules.Resolve(null, "x", @"C:\Tools\amdrsserv.exe"));
        Assert.NotEqual("Advanced Micro Devices, Inc.", StartupPublisherRules.Resolve(null, "StreamDeck", @"C:\Users\me\AppData\Local\Elgato\StreamDeck.exe"));
        Assert.NotEqual("Advanced Micro Devices, Inc.", StartupPublisherRules.Resolve(null, "Camden", @"D:\Camden\camden.exe"));
    }

    [Fact]
    public void Frame_generation_note_only_with_amd_software()
    {
        Assert.Contains("Fluid Motion Frames", FrameRateNotes.FrameGeneration(true));
        Assert.Null(FrameRateNotes.FrameGeneration(false));
    }
}
