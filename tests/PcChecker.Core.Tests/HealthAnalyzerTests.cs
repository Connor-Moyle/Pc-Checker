using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class SmartBlobParserTests
{
    [Fact]
    public void Parses_two_attributes_from_a_synthetic_vendor_blob()
    {
        var values = SmartBlobParser.Build(
        [
            (5, 0x0033, 199, 199, 8),                   // reallocated sectors: pre-fail flag set, raw 8
            (194, 0x0022, 114, 98, 36 | (41L << 16)),   // temperature: raw low byte 36, upper bytes carry min/max
        ]);
        var thresholds = SmartBlobParser.BuildThresholds([(5, 140), (194, 0)]);
        Assert.Equal(SmartBlobParser.HeaderBytes + SmartBlobParser.MaxEntries * SmartBlobParser.EntryBytes, values.Length);

        var attrs = SmartBlobParser.Parse(values, thresholds);
        Assert.Equal(2, attrs.Count);

        var realloc = attrs[0];
        Assert.Equal(5, realloc.Id);
        Assert.Equal("Reallocated Sectors Count", realloc.Name);
        Assert.Equal(199, realloc.Current);
        Assert.Equal(199, realloc.Worst);
        Assert.Equal(140, realloc.Threshold);
        Assert.Equal(8, realloc.RawValue);
        Assert.False(realloc.Failed);

        var temp = attrs[1];
        Assert.Equal(194, temp.Id);
        Assert.Equal(36, SmartBlobParser.TemperatureFromRaw(temp.RawValue));
        Assert.Equal(0, temp.Threshold);
    }

    [Fact]
    public void Prefail_attribute_at_or_below_threshold_is_marked_failed()
    {
        var values = SmartBlobParser.Build([(5, 0x0033, 36, 36, 1200)]);
        var thresholds = SmartBlobParser.BuildThresholds([(5, 36)]);
        var attr = Assert.Single(SmartBlobParser.Parse(values, thresholds));
        Assert.True(attr.Failed);

        // Old-age attribute (no pre-fail flag) never counts as failed even below threshold.
        var oldAge = SmartBlobParser.Build([(9, 0x0032, 10, 10, 99_999)]);
        Assert.False(Assert.Single(SmartBlobParser.Parse(oldAge, SmartBlobParser.BuildThresholds([(9, 50)]))).Failed);
    }

    [Fact]
    public void Empty_and_short_blobs_yield_no_attributes()
    {
        Assert.Empty(SmartBlobParser.Parse([]));
        Assert.Empty(SmartBlobParser.Parse(new byte[5]));
        Assert.Empty(SmartBlobParser.Parse(new byte[512]));
        Assert.Empty(SmartBlobParser.ParseThresholds(default));
    }

    [Fact]
    public void Raw_value_is_six_bytes_little_endian()
    {
        var values = SmartBlobParser.Build([(241, 0x0032, 100, 100, 0x0000AABBCCDDEEFF)]);
        var attr = Assert.Single(SmartBlobParser.Parse(values));
        Assert.Equal(0x0000AABBCCDDEEFF, attr.RawValue);
        Assert.Equal("Total LBAs Written", attr.Name);
        Assert.Null(attr.Threshold);
    }

    [Fact]
    public void Extract_facts_reads_well_known_ids()
    {
        var attrs = new List<SmartAttribute>
        {
            new(5, "Reallocated", 100, 100, 10, 3, false),
            new(9, "Hours", 90, 90, 0, 12_345, false),
            new(194, "Temperature", 60, 50, 0, 40 | (25L << 16), false),
            new(197, "Pending", 100, 100, 0, 2, false),
            new(198, "Uncorrectable", 100, 100, 0, 1, false),
            new(231, "Life left", 88, 88, 0, 0, false),
            new(241, "LBAs written", 100, 100, 0, 2_097_152, false), // 2,097,152 × 512 B = 1 GB
        };
        var facts = SmartBlobParser.ExtractFacts(attrs);
        Assert.Equal(40, facts.TemperatureC);
        Assert.Equal(12_345, facts.PowerOnHours);
        Assert.Equal(3, facts.ReallocatedSectors);
        Assert.Equal(2, facts.PendingSectors);
        Assert.Equal(1, facts.UncorrectableErrors);
        Assert.Equal(12, facts.WearPercent);
        Assert.Equal(1, facts.HostWritesGb);
    }

    [Fact]
    public void Temperature_outside_plausible_range_is_ignored()
    {
        Assert.Null(SmartBlobParser.TemperatureFromRaw(0));
        Assert.Null(SmartBlobParser.TemperatureFromRaw(200));
        Assert.Null(SmartBlobParser.TemperatureFromRaw(null));
        Assert.Equal(55, SmartBlobParser.TemperatureFromRaw(55));
    }

    [Fact]
    public void Unknown_attribute_ids_get_a_vendor_specific_name()
    {
        Assert.Equal("Vendor Specific (99)", SmartAttributeNames.Name(99));
        Assert.True(SmartAttributeNames.IsKnown(5));
        Assert.False(SmartAttributeNames.IsKnown(99));
    }
}

public class SecurityRulesTests
{
    [Theory]
    [InlineData(0x061000, true, true)]   // Defender on, up to date
    [InlineData(0x061010, true, false)]  // on, out of date
    [InlineData(0x060000, false, true)]  // off
    [InlineData(0x041000, true, true)]
    public void Product_state_decodes_enabled_and_up_to_date_bits(int state, bool enabled, bool upToDate)
    {
        Assert.Equal(enabled, SecurityRules.IsEnabled(state));
        Assert.Equal(upToDate, SecurityRules.IsUpToDate(state));
    }

    [Fact]
    public void Antivirus_item_reflects_the_worst_product_state()
    {
        Assert.Equal(HealthStatus.Warning, SecurityRules.AntivirusItem([]).Status);
        Assert.Equal(HealthStatus.Critical, SecurityRules.AntivirusItem([new("Defender", 0x060000)]).Status);
        Assert.Equal(HealthStatus.Warning, SecurityRules.AntivirusItem([new("Defender", 0x061010)]).Status);
        var good = SecurityRules.AntivirusItem([new("Defender", 0x061000), new("Other AV", 0x060000)]);
        Assert.Equal(HealthStatus.Good, good.Status);
        Assert.Equal("On, up to date", good.Metrics["Defender"]);
    }

    [Fact]
    public void Antivirus_item_survives_two_products_with_the_same_name()
    {
        var item = SecurityRules.AntivirusItem([new("Avast Antivirus", 0x060000), new("Avast Antivirus", 0x061000)]);
        Assert.Equal(HealthStatus.Good, item.Status);
        Assert.Equal("Off, up to date", item.Metrics["Avast Antivirus"]);
        Assert.Equal("On, up to date", item.Metrics["Avast Antivirus (2)"]);
    }

    [Fact]
    public void Netsh_output_parses_three_profiles()
    {
        const string output = "\nDomain Profile Settings:\n----------------------------------------------------------------------\nState                                 ON\nFirewall Policy                       BlockInbound,AllowOutbound\n\n"
                              + "Private Profile Settings:\n----------------------------------------------------------------------\nState                                 ON\n\n"
                              + "Public Profile Settings:\n----------------------------------------------------------------------\nState                                 OFF\n";
        var profiles = SecurityRules.ParseNetshProfiles(output);
        Assert.Equal(3, profiles.Count);
        Assert.True(profiles["Domain"]);
        Assert.True(profiles["Private"]);
        Assert.False(profiles["Public"]);
        Assert.Empty(SecurityRules.ParseNetshProfiles(null));
    }

    [Fact]
    public void Localized_netsh_output_yields_unknown_rather_than_a_wrong_state()
    {
        const string german = "\nEinstellungen für Domänenprofil:\n----------------------------------------------------------------------\nStatus                                EIN\n\n"
                              + "Einstellungen für öffentliches Profil:\n----------------------------------------------------------------------\nStatus                                AUS\n";
        var profiles = SecurityRules.ParseNetshProfiles(german);
        Assert.Empty(profiles);
        Assert.Equal(HealthStatus.Unknown, SecurityRules.FirewallItem(profiles).Status);
    }

    [Fact]
    public void Firewall_item_grades_by_how_many_profiles_are_off()
    {
        Assert.Equal(HealthStatus.Unknown, SecurityRules.FirewallItem(null).Status);
        Assert.Equal(HealthStatus.Good, SecurityRules.FirewallItem(new Dictionary<string, bool> { ["Domain"] = true, ["Private"] = true, ["Public"] = true }).Status);
        var partial = SecurityRules.FirewallItem(new Dictionary<string, bool> { ["Domain"] = true, ["Private"] = true, ["Public"] = false });
        Assert.Equal(HealthStatus.Warning, partial.Status);
        Assert.Contains("Public", partial.Title);
        Assert.Equal(HealthStatus.Critical, SecurityRules.FirewallItem(new Dictionary<string, bool> { ["Private"] = false, ["Public"] = false }).Status);
    }

    [Fact]
    public void BitLocker_off_is_a_warning_on_laptops_only()
    {
        Assert.Equal(HealthStatus.Unknown, SecurityRules.BitLockerItem([], false).Status);
        Assert.Equal(HealthStatus.Good, SecurityRules.BitLockerItem([("C:", 1)], true).Status);
        Assert.Equal(HealthStatus.Good, SecurityRules.BitLockerItem([("C:", 0)], false).Status);
        Assert.Equal(HealthStatus.Warning, SecurityRules.BitLockerItem([("C:", 0)], true).Status);
    }
}

public class FreeSpaceRulesTests
{
    [Theory]
    [InlineData(50, 100, HealthStatus.Good)]
    [InlineData(10, 100, HealthStatus.Good)]
    [InlineData(9, 100, HealthStatus.Warning)]
    [InlineData(5, 100, HealthStatus.Warning)]
    [InlineData(4, 100, HealthStatus.Critical)]
    [InlineData(0, 0, HealthStatus.Unknown)]
    public void Thresholds_at_ten_and_five_percent(long free, long total, HealthStatus expected)
    {
        Assert.Equal(expected, FreeSpaceRules.Evaluate(free, total));
    }

    [Fact]
    public void Items_list_low_drives_or_a_single_all_clear()
    {
        var allFine = FreeSpaceRules.Items([new("C:", 50, 100), new("D:", 80, 100)]);
        var only = Assert.Single(allFine);
        Assert.Equal(HealthStatus.Good, only.Status);
        Assert.Contains("C: 50% free", only.Detail);

        var low = FreeSpaceRules.Items([new("C:", 3, 100), new("D:", 80, 100)]);
        var item = Assert.Single(low);
        Assert.Equal(HealthStatus.Critical, item.Status);
        Assert.Contains("C:", item.Title);
        Assert.Equal("3.0%", item.Metrics["Free %"]);
    }
}

public class HealthItemRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Crash_dumps_in_the_last_30_days_warn_and_three_or_more_are_critical()
    {
        Assert.Equal(HealthStatus.Good, HealthItemRules.CrashDumps([], Now).Status);
        var old = HealthItemRules.CrashDumps([new("x.dmp", Now.AddDays(-45), 1)], Now);
        Assert.Equal(HealthStatus.Good, old.Status);
        Assert.Contains("older", old.Detail);
        Assert.Equal(HealthStatus.Warning, HealthItemRules.CrashDumps([new("x.dmp", Now.AddDays(-2), 1)], Now).Status);
        var many = HealthItemRules.CrashDumps([new("a", Now.AddDays(-1), 1), new("b", Now.AddDays(-3), 1), new("c", Now.AddDays(-9), 1)], Now);
        Assert.Equal(HealthStatus.Critical, many.Status);
        Assert.Contains("3 blue screens", many.Title);
    }

    [Fact]
    public void Windows_update_pending_reboot_and_staleness()
    {
        Assert.Equal(HealthStatus.Warning, HealthItemRules.WindowsUpdate(Now.AddDays(-1), true, Now).Status);
        Assert.Equal(HealthStatus.Unknown, HealthItemRules.WindowsUpdate(null, false, Now).Status);
        Assert.Equal(HealthStatus.Warning, HealthItemRules.WindowsUpdate(Now.AddDays(-60), false, Now).Status);
        var good = HealthItemRules.WindowsUpdate(Now.AddDays(-3), false, Now);
        Assert.Equal(HealthStatus.Good, good.Status);
        Assert.Equal("No", good.Metrics["Restart pending"]);
    }

    [Fact]
    public void Memory_diagnostic_maps_event_ids()
    {
        Assert.Equal(HealthStatus.Unknown, HealthItemRules.MemoryDiagnostic(null, null, null, Now).Status);
        Assert.Equal(HealthStatus.Good, HealthItemRules.MemoryDiagnostic(1201, Now.AddDays(-2), "no errors", Now).Status);
        Assert.Equal(HealthStatus.Critical, HealthItemRules.MemoryDiagnostic(1202, Now.AddDays(-2), "detected hardware problems", Now).Status);
    }

    [Fact]
    public void Device_problems_ignore_disabled_and_unplugged_devices()
    {
        Assert.Equal(HealthStatus.Good, HealthItemRules.DeviceProblems([]).Status);
        var disabledOnly = HealthItemRules.DeviceProblems([new("Webcam", "USB\\1", 22, "disabled", "enable")]);
        Assert.Equal(HealthStatus.Good, disabledOnly.Status);
        var real = HealthItemRules.DeviceProblems([new("ACPI thing", "ACPI\\1", 28, "no driver", "install driver"), new("Webcam", "USB\\1", 45, "unplugged", "plug in")]);
        Assert.Equal(HealthStatus.Warning, real.Status);
        Assert.Equal("One device has a problem", real.Title);
        Assert.Equal("install driver", real.Recommendation);
    }

    [Fact]
    public void Device_problems_keep_every_device_when_names_repeat()
    {
        var item = HealthItemRules.DeviceProblems(
        [
            new("Base System Device", "PCI\\1", 28, "no driver", "install driver"),
            new("Base System Device", "PCI\\2", 28, "no driver", "install driver"),
            new("USB Input Device", "HID\\1", 45, "unplugged", "plug in"),
            new("Base System Device", "PCI\\3", 10, "cannot start", "update driver"),
        ]);
        Assert.Equal(HealthStatus.Warning, item.Status);
        Assert.Equal("3 devices have problems", item.Title);
        Assert.Equal(4, item.Metrics.Count);
        Assert.Equal("Code 28", item.Metrics["Base System Device"]);
        Assert.Equal("Code 28", item.Metrics["Base System Device (2)"]);
        Assert.Equal("Code 10", item.Metrics["Base System Device (3)"]);
    }

    [Fact]
    public void System_errors_summarise_counts_and_flag_floods_and_critical_events()
    {
        Assert.Equal(HealthStatus.Good, HealthItemRules.SystemErrors([], 7).Status);
        var few = Enumerable.Range(0, 5).Select(i => new SystemErrorEntry(Now.AddHours(-i), "System", i % 2 == 0 ? "DCOM" : "disk", Severity.High, 10016, "m")).ToList();
        var fewItem = HealthItemRules.SystemErrors(few, 7);
        Assert.Equal(HealthStatus.Good, fewItem.Status);
        Assert.Equal("3", fewItem.Metrics["DCOM"]);

        var flood = Enumerable.Range(0, 200).Select(i => new SystemErrorEntry(Now.AddMinutes(-i), "System", "Service Control Manager", Severity.High, 7000, "m")).ToList();
        var floodItem = HealthItemRules.SystemErrors(flood, 7);
        Assert.Equal(HealthStatus.Warning, floodItem.Status);
        Assert.Contains("Service Control Manager", floodItem.Detail);

        var critical = HealthItemRules.SystemErrors([new(Now, "System", "Kernel-Power", Severity.Critical, 41, "m")], 7);
        Assert.Equal(HealthStatus.Warning, critical.Status);
        Assert.Contains("critical", critical.Title);
    }

    [Fact]
    public void Top_sources_are_ordered_by_count_then_name()
    {
        var errors = new[] { "b", "a", "b", "c", "a", "b" }.Select(s => new SystemErrorEntry(Now, "System", s, Severity.High, 1, "m")).ToList();
        var top = HealthItemRules.TopSources(errors, 2);
        Assert.Equal(("b", 3), top[0]);
        Assert.Equal(("a", 2), top[1]);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Thermal_uses_monitoring_thresholds()
    {
        var settings = new MonitoringSettings { CpuWarnC = 85, CpuCritC = 95, GpuWarnC = 83, GpuCritC = 92 };
        static SensorReading Temp(string id, HardwareKind hw, double v) => new(id, hw.ToString(), hw, SensorKind.Temperature, id, v, v, v, "°C");

        Assert.Empty(HealthItemRules.Thermal(null, settings));
        var fine = Assert.Single(HealthItemRules.Thermal([Temp("cpu", HardwareKind.Cpu, 60), Temp("gpu", HardwareKind.Gpu, 50)], settings));
        Assert.Equal(HealthStatus.Good, fine.Status);
        Assert.Equal("60 °C", fine.Metrics["CPU"]);

        var hot = HealthItemRules.Thermal([Temp("cpu", HardwareKind.Cpu, 96), Temp("gpu", HardwareKind.Gpu, 85)], settings);
        Assert.Equal(2, hot.Count);
        Assert.Equal(HealthStatus.Critical, hot[0].Status);
        Assert.Equal(HealthStatus.Warning, hot[1].Status);
    }

    [Theory]
    [InlineData(null, HealthStatus.Good)]
    [InlineData("OK", HealthStatus.Good)]
    [InlineData("Healthy", HealthStatus.Good)]
    [InlineData("Unknown", HealthStatus.Good)]
    [InlineData("Warning", HealthStatus.Warning)]
    [InlineData("Unhealthy", HealthStatus.Critical)]
    [InlineData("Predicting failure", HealthStatus.Critical)]
    public void Drive_status_text_is_not_mistaken_for_failure(string? statusText, HealthStatus expected)
    {
        var disk = new DiskHealth("Model X", "SN", null, 1_000_000_000_000, "SSD", HealthStatus.Unknown, statusText, 35, 1_000, 0, 0, 0, 5, 1_200, []);
        Assert.Equal(expected, SmartRules.ClassifyStatusText(statusText));
        Assert.Equal(expected, SmartRules.Evaluate(disk).Status);
    }

    [Fact]
    public void Old_age_attribute_below_threshold_is_only_a_warning()
    {
        // Seagate 190 Airflow Temperature: threshold 45, current = 100 - temperature, so 55 C sits at 45.
        var warm = new SmartAttribute(190, "Airflow Temperature", 45, 40, 45, 55, false);
        var disk = new DiskHealth("ST2000DM008", "SN", null, 2_000_000_000_000, "HDD", HealthStatus.Unknown, "OK", null, 9_000, 0, 0, 0, null, null, [warm]);
        var verdict = SmartRules.Evaluate(disk);
        Assert.Equal(HealthStatus.Warning, verdict.Status);
        Assert.Contains(verdict.Reasons, r => r.Contains("190"));

        var failed = disk with { Attributes = [warm with { Failed = true }] };
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(failed).Status);
    }

    [Fact]
    public void Disk_item_wording_follows_the_verdict()
    {
        var disk = new DiskHealth("Model X", "SN", null, 1_000_000_000_000, "HDD", HealthStatus.Warning, "OK", 36, 15_000, 8, 0, 0, null, null, []);
        var verdict = SmartRules.Evaluate(disk);
        var item = HealthItemRules.Disk(disk with { Status = verdict.Status }, verdict);
        Assert.Equal(HealthStatus.Warning, item.Status);
        Assert.Contains("early warning", item.Title);
        Assert.Equal("8", item.Metrics["Reallocated sectors"]);
        Assert.Contains("Storage page", item.Recommendation);
    }

    [Fact]
    public void Battery_item_follows_wear_classification()
    {
        var worn = new BatteryHealth("Battery 1", 50_000, 28_000, 44, 612, "Lithium-ion", "Plugged in", 100);
        Assert.Equal(HealthStatus.Critical, HealthItemRules.Battery(worn).Status);
        var fresh = new BatteryHealth("Battery 1", 50_000, 48_000, 4, 40, "Lithium-ion", "Charging", 80);
        Assert.Equal(HealthStatus.Good, HealthItemRules.Battery(fresh).Status);
        Assert.Equal("40", HealthItemRules.Battery(fresh).Metrics["Charge cycles"]);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "5 minutes ago")]
    [InlineData(180, "3 hours ago")]
    [InlineData(60 * 24 * 12, "12 days ago")]
    public void Age_is_described_in_plain_words(int minutes, string expected)
    {
        Assert.Equal(expected, HealthItemRules.DescribeAge(TimeSpan.FromMinutes(minutes)));
    }
}

public class ReliabilityClassifierTests
{
    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, ReliabilityClassifier.UnexpectedShutdown)]
    [InlineData("EventLog", 6008, ReliabilityClassifier.UnexpectedShutdown)]
    [InlineData("Application Error", 1000, ReliabilityClassifier.ApplicationFailure)]
    [InlineData("Application Hang", 1002, ReliabilityClassifier.ApplicationFailure)]
    [InlineData("BugCheck", 1001, ReliabilityClassifier.WindowsFailure)]
    [InlineData("Microsoft-Windows-WindowsUpdateClient", 19, ReliabilityClassifier.WindowsUpdate)]
    [InlineData("MsiInstaller", 11707, ReliabilityClassifier.ApplicationInstall)]
    [InlineData("MsiInstaller", 11724, ReliabilityClassifier.ApplicationUninstall)]
    [InlineData("Microsoft-Windows-UserPnp", 20001, ReliabilityClassifier.DriverInstall)]
    [InlineData("Something", 5, ReliabilityClassifier.Information)]
    public void Classifies_known_sources(string source, long id, string expected)
    {
        Assert.Equal(expected, ReliabilityClassifier.Classify(source, id));
    }

    [Fact]
    public void Failures_are_recognised()
    {
        Assert.True(ReliabilityClassifier.IsFailure(ReliabilityClassifier.ApplicationFailure));
        Assert.True(ReliabilityClassifier.IsFailure(ReliabilityClassifier.UnexpectedShutdown));
        Assert.False(ReliabilityClassifier.IsFailure(ReliabilityClassifier.WindowsUpdate));
    }
}

public class HealthCodesTests
{
    [Fact]
    public void Wmi_codes_map_to_text()
    {
        Assert.Equal("Lithium-ion", HealthCodes.BatteryChemistry(6));
        Assert.Equal("Unknown", HealthCodes.BatteryChemistry(null));
        Assert.Equal("Fully charged", HealthCodes.BatteryStatus(3));
        Assert.Equal("SSD", HealthCodes.MediaType(4));
        Assert.Equal("NVMe", HealthCodes.BusType(17));
        Assert.Equal("Unhealthy", HealthCodes.PhysicalDiskHealth(2));
        Assert.Null(HealthCodes.PhysicalDiskHealth(5));
        Assert.Null(HealthCodes.PhysicalDiskHealth(null));
        Assert.Equal("SSD", HealthCodes.GuessMediaType("Samsung SSD 990 PRO 2TB", "SCSI", null));
        Assert.Equal("HDD", HealthCodes.GuessMediaType("WDC WD40EZAZ", "SCSI", 5400));
        Assert.Equal("External", HealthCodes.GuessMediaType("Seagate Expansion", "USB", null));
    }

    [Fact]
    public void Update_time_parses_the_registry_format()
    {
        Assert.True(WindowsHealthService.TryParseUpdateTime("2026-10-20 03:15:42", out var when));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 3, 15, 42, TimeSpan.Zero), when.ToUniversalTime());
        Assert.False(WindowsHealthService.TryParseUpdateTime("", out _));
        Assert.False(WindowsHealthService.TryParseUpdateTime("not a date", out _));
    }

    [Fact]
    public void Model_and_serial_normalisation_ignore_punctuation_and_case()
    {
        Assert.Equal("SAMSUNGSSD990PRO2TB", WindowsHealthService.NormalizeModel("Samsung SSD 990 PRO 2TB"));
        Assert.Equal("SAMSUNGSSD990PRO2TB", WindowsHealthService.NormalizeModel("Samsung_SSD_990_PRO_2TB"));
        Assert.Equal("S6Z2NJ0R", WindowsHealthService.NormalizeSerial("  s6z2-nj0r "));
        Assert.Equal(string.Empty, WindowsHealthService.NormalizeSerial(null));
    }
}

public class HealthReportFormatterTests
{
    [Fact]
    public void Text_export_contains_score_findings_drives_and_errors()
    {
        var report = DemoHealthService.Build(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var text = HealthReportFormatter.ToText(report);
        Assert.Contains("PC Checker — Health report", text);
        Assert.Contains($"Score: {report.Score}/100", text);
        Assert.Contains("[Warning] Storage:", text);
        Assert.Contains("== Drives ==", text);
        Assert.Contains(DemoData.NvmeModel, text);
        Assert.Contains("== Recent system errors (11) ==", text);
        Assert.Contains("DistributedCOM", text);
        Assert.DoesNotContain("== Battery ==", text);
    }

    [Fact]
    public void Empty_report_still_formats()
    {
        var text = HealthReportFormatter.ToText(HealthReport.Empty("nothing ran"));
        Assert.Contains("(no findings)", text);
        Assert.Contains("- nothing ran", text);
    }
}

public class DemoHealthServiceTests
{
    [Fact]
    public async Task Demo_report_is_realistic_and_consistent()
    {
        var svc = new DemoHealthService();
        var progress = new List<ProgressUpdate>();
        var report = await svc.AnalyzeAsync(new SyncProgress<ProgressUpdate>(progress.Add), CancellationToken.None);
        Assert.Same(report, svc.Last);
        Assert.Contains(progress, p => p.Stage == "Done");
        Assert.InRange(report.Score, 80, 99);
        Assert.Equal(HealthStatus.Good, report.OverallStatus);
        Assert.Equal(2, report.Disks.Count);
        Assert.Contains(report.Disks, d => d.Status == HealthStatus.Warning && d.ReallocatedSectors == 8);
        Assert.Contains(report.Disks, d => d.Status == HealthStatus.Good && d.WearPercent == 3);
        Assert.Empty(report.Batteries);
        Assert.Single(report.DeviceProblems);
        Assert.True(report.SystemErrors.Count >= 5);
        Assert.Equal(report.SystemErrors.OrderByDescending(e => e.Time).Select(e => e.EventId), report.SystemErrors.Select(e => e.EventId));
        Assert.NotEmpty(report.Reliability);
        Assert.Single(report.CrashDumps);
        Assert.Equal(HealthScoring.Score(report.Items), report.Score);
        Assert.Contains(report.Items, i => i.Component == "Security" && i.Status == HealthStatus.Good);
        Assert.Contains(report.Items, i => i.Component == "Stability" && i.Status == HealthStatus.Good);
        Assert.Empty(report.Warnings);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
