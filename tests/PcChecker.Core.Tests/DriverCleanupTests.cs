using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using Xunit;

namespace PcChecker.Core.Tests;

public class DriverCleanupRulesTests
{
    private const string DisplayGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string MediaGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";
    private const string SystemGuid = "{4d36e97d-e325-11ce-bfc1-08002be10318}";

    private static DriverPackage Pkg(string published, string version, DateTime? date, string original = "nv_dispi.inf",
        string provider = "NVIDIA Corporation", string cls = "Display adapters", string? guid = DisplayGuid) =>
        new(published, original, provider, cls, guid, version, date, "Microsoft Windows Hardware Compatibility Publisher");

    private static DriverInfo Device(string name, string inf, bool present = true, string cls = "Display") =>
        new(name, @"PCI\VEN_" + name.GetHashCode().ToString("X"), cls, "Vendor", "1.0", DateTime.Today, inf, true, "Vendor",
            !DriverInfoBuilder.IsOemInf(inf), [], "OK", 0, null) { IsPresent = present };

    // ---- grouping -------------------------------------------------------------------------------------------------

    [Fact]
    public void Versions_of_the_same_driver_are_grouped_by_original_name_provider_and_class()
    {
        var groups = DriverCleanupRules.GroupPackages(
        [
            Pkg("oem1.inf", "1.0", new DateTime(2022, 1, 1)),
            Pkg("oem2.inf", "2.0", new DateTime(2023, 1, 1), original: "NV_DISPI.INF", provider: "nvidia corporation"),
            Pkg("oem3.inf", "3.0", new DateTime(2024, 1, 1), provider: "Someone else"),
            Pkg("oem4.inf", "1.0", new DateTime(2024, 1, 1), original: "hdxrt.inf", provider: "Realtek", cls: "Sound", guid: MediaGuid),
        ]);

        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Count == 2 && g.All(p => p.PublishedName is "oem1.inf" or "oem2.inf"));
    }

    [Fact]
    public void The_class_guid_groups_packages_even_when_pnputil_printed_a_translated_class_name()
    {
        var a = Pkg("oem1.inf", "1.0", null, cls: "Display adapters");
        var b = Pkg("oem2.inf", "2.0", null, cls: "Grafikkarten");
        Assert.Equal(DriverCleanupRules.GroupKey(a), DriverCleanupRules.GroupKey(b));

        var noGuidA = Pkg("oem3.inf", "1.0", null, cls: "Display adapters", guid: null);
        var noGuidB = Pkg("oem4.inf", "1.0", null, cls: "Grafikkarten", guid: null);
        Assert.NotEqual(DriverCleanupRules.GroupKey(noGuidA), DriverCleanupRules.GroupKey(noGuidB));
    }

    [Fact]
    public void Packages_without_an_original_name_are_never_grouped()
    {
        var a = new DriverPackage("oem1.inf", null, "X", "System devices", SystemGuid, "1.0", null, null);
        var b = new DriverPackage("oem2.inf", null, "X", "System devices", SystemGuid, "2.0", null, null);
        Assert.Equal(2, DriverCleanupRules.GroupPackages([a, b]).Count);
        Assert.Empty(DriverCleanupRules.FindSuperseded([a, b]));
    }

    // ---- superseded detection ---------------------------------------------------------------------------------------

    [Fact]
    public void A_later_driver_date_supersedes_an_earlier_one()
    {
        var superseded = DriverCleanupRules.FindSuperseded(
        [
            Pkg("oem19.inf", "30.0.15.1179", new DateTime(2022, 2, 2)),
            Pkg("oem27.inf", "31.0.15.3623", new DateTime(2023, 6, 21)),
            Pkg("oem12.inf", "32.0.15.6094", new DateTime(2025, 2, 10)),
        ]);

        Assert.Equal(2, superseded.Count);
        Assert.Equal("oem12.inf", superseded["oem19.inf"].PublishedName);
        Assert.Equal("oem12.inf", superseded["oem27.inf"].PublishedName);
        Assert.False(superseded.ContainsKey("oem12.inf"));
    }

    [Fact]
    public void On_the_same_date_the_higher_version_wins()
    {
        var day = new DateTime(2024, 5, 1);
        var superseded = DriverCleanupRules.FindSuperseded([Pkg("oem1.inf", "10.1.9.2", day), Pkg("oem2.inf", "10.1.10.0", day)]);
        Assert.Equal("oem2.inf", Assert.Single(superseded).Value.PublishedName);
        Assert.True(superseded.ContainsKey("oem1.inf"));
    }

    [Fact]
    public void The_driver_date_decides_before_the_version_like_windows_ranking()
    {
        var older = Pkg("oem1.inf", "9.9.9.9", new DateTime(2021, 1, 1));
        var newer = Pkg("oem2.inf", "1.0.0.0", new DateTime(2024, 1, 1));
        Assert.True(DriverCleanupRules.CompareRelease(newer, older) > 0);
        Assert.True(DriverCleanupRules.FindSuperseded([older, newer]).ContainsKey("oem1.inf"));
    }

    [Fact]
    public void Exact_ties_supersede_neither_package()
    {
        var day = new DateTime(2024, 5, 1);
        var a = Pkg("oem1.inf", "2.0.0.0", day);
        var b = Pkg("oem2.inf", "2.0", day.AddHours(5));
        Assert.Equal(0, DriverCleanupRules.CompareRelease(a, b));
        Assert.Empty(DriverCleanupRules.FindSuperseded([a, b]));

        // A third, newer version supersedes both tied ones.
        var c = Pkg("oem3.inf", "3.0", day.AddDays(1));
        Assert.Equal(2, DriverCleanupRules.FindSuperseded([a, b, c]).Count);
    }

    [Fact]
    public void A_missing_date_counts_as_older()
    {
        var undated = Pkg("oem1.inf", "5.0", null);
        var dated = Pkg("oem2.inf", "1.0", new DateTime(2020, 1, 1));
        Assert.True(DriverCleanupRules.FindSuperseded([undated, dated]).ContainsKey("oem1.inf"));
        Assert.True(DriverCleanupRules.CompareRelease(Pkg("oem3.inf", "2.0", null), Pkg("oem4.inf", "1.0", null)) > 0);
    }

    // ---- classification and protection -------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false, true, DriverCleanupStatus.SafeToRemove)]
    [InlineData(false, false, false, false, DriverCleanupStatus.ProbablySafe)]
    [InlineData(false, true, false, true, DriverCleanupStatus.Keep)]
    [InlineData(false, false, true, true, DriverCleanupStatus.Keep)]
    [InlineData(true, false, false, true, DriverCleanupStatus.Keep)]
    public void Classification_matrix(bool inbox, bool inUse, bool protectedClass, bool superseded, DriverCleanupStatus expected) =>
        Assert.Equal(expected, DriverCleanupRules.Classify(inbox, inUse, protectedClass, superseded));

    [Theory]
    [InlineData("System", null, true)]
    [InlineData("system devices", null, true)]
    [InlineData("SCSIAdapter", null, true)]
    [InlineData("Storage controllers", null, true)]
    [InlineData("HDC", null, true)]
    [InlineData("DiskDrive", null, true)]
    [InlineData("Volume", null, true)]
    [InlineData("Keyboard", null, true)]
    [InlineData("Mouse", null, true)]
    [InlineData("Processor", null, true)]
    [InlineData("Firmware", null, true)]
    [InlineData("Systemgeräte", "{4D36E97D-E325-11CE-BFC1-08002BE10318}", true)]
    [InlineData("Unknown", "4d36e96b-e325-11ce-bfc1-08002be10318", true)]
    [InlineData("Display adapters", DisplayGuid, false)]
    [InlineData("Net", "{4d36e972-e325-11ce-bfc1-08002be10318}", false)]
    [InlineData(null, null, false)]
    public void Boot_critical_classes_are_protected(string? name, string? guid, bool expected) =>
        Assert.Equal(expected, DriverCleanupRules.IsProtectedClass(name, guid));

    [Fact]
    public void Inbox_packages_are_never_findings_never_selectable_and_never_planned()
    {
        var inbox = Pkg("nv_dispi.inf", "1.0", new DateTime(2020, 1, 1));
        var disk = Pkg("disk.inf", "10.0", new DateTime(2006, 6, 21), original: "disk.inf", provider: "Microsoft", cls: "Disk drives", guid: null);
        var items = DriverCleanupRules.Analyze([inbox, disk], [], []);
        Assert.Empty(items);
        Assert.False(DriverCleanupRules.IsRemovablePackageName("disk.inf"));
        Assert.True(DriverCleanupRules.IsRemovablePackageName("OEM42.INF"));

        var forged = new DriverCleanupItem(DriverCleanupKind.Package, "disk.inf", "Disk", DriverCleanupStatus.SafeToRemove, "x");
        Assert.False(DriverCleanupRules.CanSelect(forged, advancedMode: true));
        Assert.Empty(DriverCleanupRules.BuildRemovalPlan([forged], [forged], advancedMode: true));
        var flagged = new DriverCleanupItem(DriverCleanupKind.Package, "oem5.inf", "X", DriverCleanupStatus.SafeToRemove, "x") { IsInbox = true };
        Assert.False(DriverCleanupRules.CanSelect(flagged, advancedMode: true));
    }

    [Fact]
    public void Keep_items_are_selectable_only_in_advanced_mode()
    {
        var keep = new DriverCleanupItem(DriverCleanupKind.Package, "oem5.inf", "X", DriverCleanupStatus.Keep, "x");
        var safe = keep with { Status = DriverCleanupStatus.SafeToRemove };
        Assert.False(DriverCleanupRules.CanSelect(keep, advancedMode: false));
        Assert.True(DriverCleanupRules.CanSelect(keep, advancedMode: true));
        Assert.True(DriverCleanupRules.CanSelect(safe, advancedMode: false));
        Assert.True(DriverCleanupRules.IsSelectedByDefault(safe));
        Assert.False(DriverCleanupRules.IsSelectedByDefault(keep with { Status = DriverCleanupStatus.ProbablySafe }));
    }

    // ---- analysis ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Analysis_lists_superseded_and_unused_packages_and_skips_current_drivers_in_use()
    {
        var packages = new[]
        {
            Pkg("oem12.inf", "32.0.15.6094", new DateTime(2025, 2, 10)),
            Pkg("oem27.inf", "31.0.15.3623", new DateTime(2023, 6, 21)),
            Pkg("oem33.inf", "1.2", new DateTime(2021, 5, 4), original: "cap.inf", provider: "Elgato", cls: "Media", guid: MediaGuid),
        };
        var drivers = new[] { Device("GeForce RTX 4070", "oem12.inf") };

        var items = DriverCleanupRules.Analyze(packages, drivers, []);

        Assert.Equal(2, items.Count);
        var old = Assert.Single(items, i => i.Id == "oem27.inf");
        Assert.Equal(DriverCleanupStatus.SafeToRemove, old.Status);
        Assert.True(old.IsSuperseded);
        Assert.Equal("oem12.inf", old.NewerPackage!.PublishedName);
        Assert.Contains("32.0.15.6094", old.Reason);
        var unused = Assert.Single(items, i => i.Id == "oem33.inf");
        Assert.Equal(DriverCleanupStatus.ProbablySafe, unused.Status);
        Assert.False(unused.InUse);
        Assert.Equal(DriverCleanupStatus.SafeToRemove, items[0].Status);
    }

    [Fact]
    public void An_old_package_still_in_use_is_kept_and_named_after_its_device()
    {
        var packages = new[]
        {
            Pkg("oem2.inf", "10.1.47.8", new DateTime(2023, 3, 1), original: "chipset.inf", provider: "INTEL", cls: "System devices", guid: SystemGuid),
            Pkg("oem40.inf", "10.1.49.10", new DateTime(2024, 8, 12), original: "chipset.inf", provider: "INTEL", cls: "System devices", guid: SystemGuid),
        };
        var items = DriverCleanupRules.Analyze(packages, [Device("Intel(R) SMBus", "oem2.inf", cls: "System")], []);

        var used = Assert.Single(items, i => i.Id == "oem2.inf");
        Assert.Equal(DriverCleanupStatus.Keep, used.Status);
        Assert.True(used.InUse);
        Assert.Equal("Intel(R) SMBus", used.Title);
        Assert.StartsWith("In use by Intel(R) SMBus", used.Reason);
        var staged = Assert.Single(items, i => i.Id == "oem40.inf");
        Assert.Equal(DriverCleanupStatus.Keep, staged.Status);
        Assert.True(staged.IsProtectedClass);
    }

    [Fact]
    public void Only_connected_devices_count_as_in_use()
    {
        var packages = new[] { Pkg("oem7.inf", "1.0", new DateTime(2022, 1, 1), original: "usbcap.inf", provider: "Elgato", cls: "MEDIA", guid: MediaGuid) };
        var items = DriverCleanupRules.Analyze(packages, [Device("Capture card", "oem7.inf", present: false, cls: "MEDIA")], []);

        var pkg = Assert.Single(items, i => i.Kind == DriverCleanupKind.Package);
        Assert.False(pkg.InUse);
        Assert.Equal(DriverCleanupStatus.ProbablySafe, pkg.Status);
        Assert.Single(pkg.DisconnectedDevices);
        var dev = Assert.Single(items, i => i.Kind == DriverCleanupKind.DisconnectedDevice);
        Assert.Equal(DriverCleanupStatus.ProbablySafe, dev.Status);
        Assert.Equal("oem7.inf", dev.PackageName);
        Assert.Contains("removed with it", dev.Reason);
    }

    [Fact]
    public void Disconnected_devices_with_inbox_drivers_are_not_listed_and_protected_ones_are_kept()
    {
        var ghosts = new[]
        {
            new DisconnectedDevice(@"USBSTOR\DISK&VEN_SANDISK\1", "SanDisk Ultra", "DiskDrive", "{4d36e967-e325-11ce-bfc1-08002be10318}", null, "Disconnected", "disk.inf"),
            new DisconnectedDevice(@"SCSI\DISK&VEN_X\2", "Old RAID disk", "DiskDrive", "{4d36e967-e325-11ce-bfc1-08002be10318}", null, "Disconnected", "oem60.inf"),
        };
        var items = DriverCleanupRules.Analyze([], [], ghosts);
        var item = Assert.Single(items);
        Assert.Equal(DriverCleanupKind.DisconnectedDevice, item.Kind);
        Assert.Equal(DriverCleanupStatus.Keep, item.Status);
    }

    [Fact]
    public void Disconnected_devices_from_pnputil_and_wmi_are_merged_by_instance_id()
    {
        var fromPnp = new[] { new DisconnectedDevice(@"USB\VID_1\A", "From pnputil", "MEDIA", MediaGuid, null, "Disconnected", "oem1.inf") };
        var drivers = new[]
        {
            Device("Present", "oem2.inf") with { DeviceId = @"USB\VID_2\B" },
            Device("Duplicate", "oem1.inf", present: false) with { DeviceId = @"usb\vid_1\a" },
            Device("WMI only", "oem3.inf", present: false) with { DeviceId = @"USB\VID_3\C" },
        };
        var merged = DriverCleanupRules.MergeDisconnected(fromPnp, drivers);
        Assert.Equal(2, merged.Count);
        Assert.Equal("From pnputil", merged[0].Description);
        Assert.Equal("oem3.inf", merged[1].DriverName);
    }

    [Fact]
    public void When_device_usage_is_unknown_every_package_is_kept()
    {
        var packages = new[] { Pkg("oem19.inf", "1.0", new DateTime(2020, 1, 1)), Pkg("oem12.inf", "2.0", new DateTime(2024, 1, 1)) };
        var items = DriverCleanupRules.Analyze(packages, [], [], usageKnown: false);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(DriverCleanupStatus.Keep, i.Status));
        Assert.All(items, i => Assert.Contains("could not read", i.Reason));
    }

    // ---- removal plan -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Removal_plan_forgets_devices_first_and_adds_the_package_of_a_selected_device()
    {
        var packages = new[]
        {
            Pkg("oem12.inf", "2.0", new DateTime(2024, 1, 1)),
            Pkg("oem11.inf", "1.0", new DateTime(2022, 1, 1)),
            Pkg("oem33.inf", "1.0", new DateTime(2021, 1, 1), original: "cap.inf", provider: "Elgato", cls: "MEDIA", guid: MediaGuid),
        };
        var ghosts = new[] { new DisconnectedDevice(@"USB\VID_0FD9\1", "Capture card", "MEDIA", MediaGuid, null, "Disconnected", "oem33.inf") };
        var all = DriverCleanupRules.Analyze(packages, [Device("GPU", "oem12.inf")], ghosts, new Dictionary<string, long> { ["oem33.inf"] = 100 });

        var device = all.Single(i => i.Kind == DriverCleanupKind.DisconnectedDevice);
        var old = all.Single(i => i.Id == "oem11.inf");
        var plan = DriverCleanupRules.BuildRemovalPlan([old, device], all, advancedMode: false);

        Assert.Equal(3, plan.Count);
        Assert.Equal(DriverCleanupStepKind.RemoveDevice, plan[0].Kind);
        Assert.False(plan[0].AddedAutomatically);
        Assert.All(plan.Skip(1), s => Assert.Equal(DriverCleanupStepKind.DeletePackage, s.Kind));
        var auto = Assert.Single(plan, s => s.Target == "oem33.inf");
        Assert.True(auto.AddedAutomatically);
        Assert.Equal(100, auto.SizeBytes);
    }

    [Fact]
    public void Selecting_a_package_also_forgets_the_disconnected_devices_that_point_at_it()
    {
        var packages = new[] { Pkg("oem33.inf", "1.0", new DateTime(2021, 1, 1), original: "cap.inf", provider: "Elgato", cls: "MEDIA", guid: MediaGuid) };
        var ghosts = new[] { new DisconnectedDevice(@"USB\VID_0FD9\1", "Capture card", "MEDIA", MediaGuid, null, "Disconnected", "oem33.inf") };
        var all = DriverCleanupRules.Analyze(packages, [], ghosts);

        var plan = DriverCleanupRules.BuildRemovalPlan([all.Single(i => i.Kind == DriverCleanupKind.Package)], all, advancedMode: false);
        Assert.Equal([DriverCleanupStepKind.RemoveDevice, DriverCleanupStepKind.DeletePackage], plan.Select(s => s.Kind));
        Assert.True(plan[0].AddedAutomatically);
        Assert.Equal(@"USB\VID_0FD9\1", plan[0].Target);
    }

    [Fact]
    public void Removal_plan_skips_keep_items_unless_advanced_and_never_adds_a_package_in_use()
    {
        var packages = new[]
        {
            Pkg("oem2.inf", "1.0", new DateTime(2022, 1, 1), original: "c.inf", provider: "INTEL", cls: "System devices", guid: SystemGuid),
            Pkg("oem40.inf", "2.0", new DateTime(2024, 1, 1), original: "c.inf", provider: "INTEL", cls: "System devices", guid: SystemGuid),
            Pkg("oem50.inf", "1.0", new DateTime(2022, 1, 1), original: "hs.inf", provider: "Acme", cls: "MEDIA", guid: MediaGuid),
            Pkg("oem51.inf", "2.0", new DateTime(2024, 1, 1), original: "hs.inf", provider: "Acme", cls: "MEDIA", guid: MediaGuid),
        };
        var ghosts = new[] { new DisconnectedDevice(@"USB\HS\1", "Old headset", "MEDIA", MediaGuid, null, "Disconnected", "oem51.inf") };
        var drivers = new[] { Device("SMBus", "oem2.inf", cls: "System"), Device("Headset", "oem51.inf", cls: "MEDIA") };
        var all = DriverCleanupRules.Analyze(packages, drivers, ghosts);

        Assert.Empty(DriverCleanupRules.BuildRemovalPlan(all.Where(i => i.Status == DriverCleanupStatus.Keep), all, advancedMode: false));
        var advanced = DriverCleanupRules.BuildRemovalPlan(all.Where(i => i.Status == DriverCleanupStatus.Keep), all, advancedMode: true);
        Assert.Contains(advanced, s => s.Target == "oem40.inf");

        var ghostOnly = DriverCleanupRules.BuildRemovalPlan(all.Where(i => i.Kind == DriverCleanupKind.DisconnectedDevice), all, advancedMode: true);
        var step = Assert.Single(ghostOnly);
        Assert.Equal(DriverCleanupStepKind.RemoveDevice, step.Kind);
    }

    // ---- sizes ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Size_totals_count_known_package_sizes_once_and_ignore_devices()
    {
        var a = new DriverCleanupItem(DriverCleanupKind.Package, "oem1.inf", "A", DriverCleanupStatus.SafeToRemove, "x") { SizeBytes = 1_000 };
        var dup = a with { Title = "A again" };
        var b = new DriverCleanupItem(DriverCleanupKind.Package, "oem2.inf", "B", DriverCleanupStatus.ProbablySafe, "x") { SizeBytes = null };
        var keep = new DriverCleanupItem(DriverCleanupKind.Package, "oem3.inf", "C", DriverCleanupStatus.Keep, "x") { SizeBytes = 50_000 };
        var dev = new DriverCleanupItem(DriverCleanupKind.DisconnectedDevice, @"USB\1", "D", DriverCleanupStatus.ProbablySafe, "x") { SizeBytes = 7 };

        Assert.Equal(51_000, DriverCleanupRules.TotalSize([a, dup, b, keep, dev]));
        var summary = DriverCleanupRules.Summarize([a, b, keep, dev]);
        Assert.Equal(1_000, summary.FreeableBytes);
        Assert.True(summary.SizesIncomplete);
        Assert.Equal(3, summary.PackagesFound);
        Assert.Equal(1, summary.SafeToRemove);
        Assert.Equal(2, summary.ProbablySafe);
        Assert.Equal(1, summary.Keep);
        Assert.Equal(1, summary.DisconnectedDevices);
        Assert.False(DriverCleanupRules.Summarize([a]).SizesIncomplete);
        Assert.Equal(DriverCleanupSummary.Empty, DriverCleanupRules.Summarize([]));
    }

    [Fact]
    public void Sizes_are_applied_to_packages_only()
    {
        var pkg = new DriverCleanupItem(DriverCleanupKind.Package, "oem1.inf", "A", DriverCleanupStatus.SafeToRemove, "x");
        var dev = new DriverCleanupItem(DriverCleanupKind.DisconnectedDevice, "oem1.inf", "D", DriverCleanupStatus.ProbablySafe, "x");
        var sized = DriverCleanupRules.ApplySizes([pkg, dev], new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["OEM1.INF"] = 42 });
        Assert.Equal(42, sized[0].SizeBytes);
        Assert.Null(sized[1].SizeBytes);
    }

    [Fact]
    public void Removal_result_summarises_steps_and_freed_space()
    {
        var step1 = new DriverCleanupStep(DriverCleanupStepKind.DeletePackage, "oem1.inf", "A", 2 * ByteSize.Megabyte, false);
        var step2 = new DriverCleanupStep(DriverCleanupStepKind.DeletePackage, "oem2.inf", "B", 5 * ByteSize.Megabyte, false);
        var result = new DriverCleanupRemovalResult(
            [new(step1, OperationResult.Ok("ok"), "C:\\b\\oem1"), new(step2, OperationResult.Fail("in use"), null)], "C:\\b", true);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2 * ByteSize.Megabyte, result.FreedBytes);
        Assert.Equal("Removed 1 of 2 items; 1 could not be removed. About 2.00 MB freed. Restart Windows to finish.", result.Summary);
        Assert.Equal("Nothing was removed.", new DriverCleanupRemovalResult([], null, false).Summary);
    }

    [Fact]
    public void Backup_folders_are_timestamped_under_the_root()
    {
        var folder = DriverCleanupRules.BackupFolderFor(Path.Combine("root", "driver-backups"), new DateTime(2026, 10, 27, 14, 5, 9));
        Assert.Equal(Path.Combine("root", "driver-backups", "20261027-140509"), folder);
        Assert.EndsWith(Path.Combine("PcChecker", DriverCleanupRules.BackupFolderName), DriverCleanupRules.DefaultBackupRoot);
    }
}

public class PnpUtilEnumDevicesTests
{
    [Fact]
    public void English_disconnected_device_listing_is_parsed()
    {
        const string output = """
            Microsoft PnP Utility

            Instance ID:                USB\VID_0FD9&PID_004F\000000000001
            Device Description:         Elgato Game Capture HD60 S
            Class Name:                 MEDIA
            Class GUID:                 {4d36e96c-e325-11ce-bfc1-08002be10318}
            Manufacturer Name:          Elgato Systems GmbH
            Status:                     Disconnected
            Driver Name:                oem33.inf

            Instance ID:                USBSTOR\Disk&Ven_SanDisk&Prod_Ultra&Rev_1.00\4C530001\0
            Device Description:         SanDisk Ultra USB Device
            Class Name:                 DiskDrive
            Class GUID:                 {4d36e967-e325-11ce-bfc1-08002be10318}
            Manufacturer Name:          (Standard disk drives)
            Status:                     Disconnected
            Driver Name:                disk.inf

            """;
        var devices = PnpUtilParser.ParseEnumDevices(output.Replace("\n", "\r\n"));

        Assert.Equal(2, devices.Count);
        var d = devices[0];
        Assert.Equal(@"USB\VID_0FD9&PID_004F\000000000001", d.InstanceId);
        Assert.Equal("Elgato Game Capture HD60 S", d.Description);
        Assert.Equal("MEDIA", d.ClassName);
        Assert.Equal("{4d36e96c-e325-11ce-bfc1-08002be10318}", d.ClassGuid);
        Assert.Equal("Elgato Systems GmbH", d.Manufacturer);
        Assert.Equal("Disconnected", d.Status);
        Assert.Equal("oem33.inf", d.DriverName);
        Assert.Equal("disk.inf", devices[1].DriverName);
        Assert.Equal("(Standard disk drives)", devices[1].Manufacturer);
    }

    [Fact]
    public void Localized_disconnected_device_listing_is_mapped_by_value_shape_and_print_order()
    {
        const string german = """
            Microsoft-PnP-Dienstprogramm

            Instanz-ID:                 USB\VID_0FD9&PID_004F\000000000001
            Gerätebeschreibung:         Elgato Game Capture HD60 S
            Klassenname:                MEDIA
            Klassen-GUID:               {4d36e96c-e325-11ce-bfc1-08002be10318}
            Herstellername:             Elgato Systems GmbH
            Status:                     Getrennt
            Treibername:                oem33.inf

            Instanz-ID:                 SWD\MMDEVAPI\{0.0.0.00000000}.{8b6c0e3a}
            Gerätebeschreibung:         Lautsprecher (USB Audio)
            Klassenname:                AudioEndpoint
            Klassen-GUID:               {c166523c-fe0c-4a94-a586-f1a80cfbbf3e}
            Herstellername:             Microsoft
            Status:                     Getrennt

            """;
        var devices = PnpUtilParser.ParseEnumDevices(german);

        Assert.Equal(2, devices.Count);
        var d = devices[0];
        Assert.Equal(@"USB\VID_0FD9&PID_004F\000000000001", d.InstanceId);
        Assert.Equal("Elgato Game Capture HD60 S", d.Description);
        Assert.Equal("MEDIA", d.ClassName);
        Assert.Equal("{4d36e96c-e325-11ce-bfc1-08002be10318}", d.ClassGuid);
        Assert.Equal("Elgato Systems GmbH", d.Manufacturer);
        Assert.Equal("Getrennt", d.Status);
        Assert.Equal("oem33.inf", d.DriverName);

        var speaker = devices[1];
        Assert.Equal(@"SWD\MMDEVAPI\{0.0.0.00000000}.{8b6c0e3a}", speaker.InstanceId);
        Assert.Equal("Lautsprecher (USB Audio)", speaker.Description);
        Assert.Equal("Microsoft", speaker.Manufacturer);
        Assert.Equal("Getrennt", speaker.Status);
        Assert.Null(speaker.DriverName);

        const string japanese = "インスタンス ID：   USB\\VID_046D&PID_0A87\\1\r\nデバイスの説明：  ヘッドセット\r\nクラス名： MEDIA\r\nクラス GUID： {4d36e96c-e325-11ce-bfc1-08002be10318}\r\n製造元名： Logitech\r\n状態： 切断済み\r\nドライバー名： oem77.inf\r\n";
        var jp = Assert.Single(PnpUtilParser.ParseEnumDevices(japanese));
        Assert.Equal(@"USB\VID_046D&PID_0A87\1", jp.InstanceId);
        Assert.Equal("ヘッドセット", jp.Description);
        Assert.Equal("Logitech", jp.Manufacturer);
        Assert.Equal("oem77.inf", jp.DriverName);
    }

    [Fact]
    public void Empty_or_unrecognised_device_listings_give_no_devices()
    {
        Assert.Empty(PnpUtilParser.ParseEnumDevices(null));
        Assert.Empty(PnpUtilParser.ParseEnumDevices("Microsoft PnP Utility\r\n\r\nNo devices were found on the system.\r\n"));
        Assert.Empty(PnpUtilParser.ParseEnumDevices("PNPUTIL /enum-devices [/disconnected]\r\nUnknown option: /disconnected\r\n"));
    }

    [Fact]
    public void Pnputil_runs_map_to_operation_results()
    {
        Assert.True(PnpUtilParser.ToOperationResult(new PnpUtilResult(0, "", false, null), "Removed.").Success);
        var reboot = PnpUtilParser.ToOperationResult(new PnpUtilResult(PnpUtilParser.RebootRequiredExitCode, "", false, null), "Removed.");
        Assert.True(reboot.Success);
        Assert.True(reboot.RebootRequired);
        Assert.Contains("Restart Windows", reboot.Message);

        var inUse = PnpUtilParser.ToOperationResult(new PnpUtilResult(PnpUtilParser.DriverStoreDeleteFailedExitCode,
            "Microsoft PnP Utility\r\n\r\nFailed to delete driver package: One or more devices are presently installed using the specified INF.", false, null), "Removed.");
        Assert.False(inUse.Success);
        Assert.Contains("in use", inUse.Message);
        Assert.EndsWith("using the specified INF.", inUse.Message);

        Assert.Contains("could not start", PnpUtilParser.ToOperationResult(new PnpUtilResult(-1, "", false, "missing"), "x").Message);
        Assert.Contains("too long", PnpUtilParser.ToOperationResult(new PnpUtilResult(-2, "", true, null), "x").Message);
    }
}

public class DemoDriverCleanupServiceTests
{
    [Fact]
    public async Task Demo_scan_finds_the_leftover_nvidia_realtek_capture_and_chipset_packages()
    {
        var service = new DemoDriverCleanupService();
        var scan = await service.ScanAsync(null, CancellationToken.None);

        var safe = scan.Items.Where(i => i.Status == DriverCleanupStatus.SafeToRemove).Select(i => i.Id).ToHashSet();
        Assert.Equal(["oem14.inf", "oem19.inf", "oem27.inf", "oem31.inf"], safe.Order());
        Assert.Contains(scan.Items, i => i.Kind == DriverCleanupKind.DisconnectedDevice && i.Status == DriverCleanupStatus.ProbablySafe);
        Assert.Contains(scan.Items, i => i.Id == "oem33.inf" && i.Status == DriverCleanupStatus.ProbablySafe);
        Assert.Contains(scan.Items, i => i.Id == "oem2.inf" && i.Status == DriverCleanupStatus.Keep && i.InUse);
        Assert.DoesNotContain(scan.Items, i => i.Id == "oem12.inf");
        Assert.Equal(1, scan.Summary.DisconnectedDevices);
        Assert.True(scan.Summary.FreeableBytes > 3 * ByteSize.Gigabyte);
        Assert.False(scan.Summary.SizesIncomplete);
    }

    [Fact]
    public async Task Demo_removal_reports_each_step_and_the_next_scan_reflects_it()
    {
        var service = new DemoDriverCleanupService();
        var scan = await service.ScanAsync(null, CancellationToken.None);
        var selected = scan.Items.Where(DriverCleanupRules.IsSelectedByDefault).ToList();
        var plan = DriverCleanupRules.BuildRemovalPlan(selected, scan.Items, advancedMode: false);

        var result = await service.RemoveAsync(plan, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(4, result.Value!.Succeeded);
        Assert.All(result.Value.Results, r => Assert.NotNull(r.BackupFolder));
        var rescan = await service.ScanAsync(null, CancellationToken.None);
        Assert.Equal(0, rescan.Summary.SafeToRemove);
    }
}
