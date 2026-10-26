using System.Text.Json;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Performance;
using PcChecker.Core.Reports;
using PcChecker.Core.Sensors;
using PcChecker.Core.Software;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class ReportsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", Guid.NewGuid().ToString("N"));

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

    private static async Task<ReportBundle> DemoBundleAsync()
    {
        var hardware = await new DemoHardwareInventoryService().ScanAsync(null, CancellationToken.None);
        var health = await new DemoHealthService().AnalyzeAsync(null, CancellationToken.None);
        var performance = await new DemoPerformanceAnalyzer(new DemoStartupService()).AnalyzeAsync(null, CancellationToken.None);
        var software = await new DemoSoftwareInventoryService().ScanAsync(null, CancellationToken.None);
        var drivers = await new DemoDriverService().ScanInstalledAsync(null, CancellationToken.None);
        using var sensors = new DemoSensorService();
        return new ReportBundle(hardware, health, performance, software, drivers, sensors.Latest, new DateTimeOffset(2026, 10, 26, 14, 12, 0, TimeSpan.Zero), "0.1.0");
    }

    [Fact]
    public void Html_encodes_user_data_so_scripts_cannot_run()
    {
        var evil = InstalledProgram.Create("1", "<script>alert('x')</script>", publisher: "Pub & Co \"quoted\"");
        var bundle = new ReportBundle(null, null, null, new SoftwareInventory([evil], [], DateTimeOffset.Now, []), null, null, DateTimeOffset.Now, "1.0");
        var html = HtmlReportBuilder.Build(bundle);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Pub &amp; Co &quot;quoted&quot;", html);
    }

    [Fact]
    public async Task Html_includes_every_section_present_in_the_bundle()
    {
        var bundle = await DemoBundleAsync();
        var html = HtmlReportBuilder.Build(bundle);
        foreach (var id in new[] { "summary", "health", "hardware", "performance", "drivers", "software", "sensors" })
        {
            Assert.Contains($"<section id=\"{id}\">", html);
        }

        Assert.Contains(DemoData.CpuName, html);
        Assert.Contains(DemoData.NvmeModel, html);
        Assert.Contains("Health score", html);
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("@media print", html);
        Assert.Equal(7, HtmlReportBuilder.SectionsIn(bundle).Count);
    }

    [Fact]
    public void Html_omits_sections_whose_data_is_null()
    {
        var bundle = new ReportBundle(null, HealthReport.Empty(), null, null, null, null, DateTimeOffset.Now, "1.0");
        var html = HtmlReportBuilder.Build(bundle);
        Assert.Contains("<section id=\"health\">", html);
        Assert.DoesNotContain("<section id=\"hardware\">", html);
        Assert.DoesNotContain("<section id=\"software\">", html);
        Assert.DoesNotContain("<section id=\"sensors\">", html);
        Assert.Equal(["summary", "health"], HtmlReportBuilder.SectionsIn(bundle).Select(s => s.Id));
    }

    [Fact]
    public async Task Html_puts_raw_properties_in_details_and_marks_problems()
    {
        var bundle = await DemoBundleAsync();
        var html = HtmlReportBuilder.Build(bundle);
        Assert.Contains("<details><summary>All properties", html);
        Assert.Contains("class=\"warn\"", html);
        Assert.Contains("Reallocated", html);
    }

    [Fact]
    public async Task Markdown_has_headings_tables_and_escapes_pipes()
    {
        var bundle = await DemoBundleAsync();
        var md = MarkdownReportBuilder.Build(bundle);
        Assert.StartsWith("# PC Checker report", md);
        Assert.Contains("## Health", md);
        Assert.Contains("## Hardware", md);
        Assert.Contains("## Installed software", md);
        Assert.Contains("| Drive | Type | Size |", md);
        Assert.Contains(DemoData.GpuName, md);

        var pipe = InstalledProgram.Create("1", "A | B", version: "1|2");
        var small = new ReportBundle(null, null, null, new SoftwareInventory([pipe], [], DateTimeOffset.Now, []), null, null, DateTimeOffset.Now, "1.0");
        Assert.Contains(@"A \| B", MarkdownReportBuilder.Build(small));
    }

    [Fact]
    public void Markdown_marks_empty_tables()
    {
        var bundle = new ReportBundle(null, HealthReport.Empty(), null, null, null, null, DateTimeOffset.Now, "1.0");
        var md = MarkdownReportBuilder.Build(bundle);
        Assert.Contains("_Nothing to show._", md);
        Assert.DoesNotContain("## Hardware", md);
    }

    [Fact]
    public async Task Json_export_serializes_tuples_and_enums_as_text()
    {
        var bundle = await DemoBundleAsync();
        var json = HtmlReportService.BuildJson(bundle);
        using var doc = JsonDocument.Parse(json);
        var perf = doc.RootElement.GetProperty("Performance");
        Assert.True(perf.GetProperty("Findings").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.String, perf.GetProperty("Findings")[0].GetProperty("Severity").ValueKind);
        var drives = perf.GetProperty("Drives");
        Assert.True(drives.GetArrayLength() > 0);
        Assert.True(drives[0].TryGetProperty("Item1", out _));
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("AppVersion").GetString());
    }

    [Fact]
    public async Task ExportAsync_writes_each_format_and_creates_the_folder()
    {
        var bundle = await DemoBundleAsync();
        var svc = new HtmlReportService();
        var html = await svc.ExportAsync(bundle, ReportFormat.Html, Path.Combine(_dir, "nested", "report.html"), CancellationToken.None);
        var md = await svc.ExportAsync(bundle, ReportFormat.Markdown, Path.Combine(_dir, "report.md"), CancellationToken.None);
        var json = await svc.ExportAsync(bundle, ReportFormat.Json, Path.Combine(_dir, "report.json"), CancellationToken.None);
        Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(html));
        Assert.StartsWith("# PC Checker report", await File.ReadAllTextAsync(md));
        Assert.StartsWith("{", await File.ReadAllTextAsync(json));
        Assert.Equal(ReportFormat.Json, HtmlReportService.FormatFromPath("x.JSON"));
        Assert.Equal(ReportFormat.Markdown, HtmlReportService.FormatFromPath("x.md"));
        Assert.Equal(ReportFormat.Html, HtmlReportService.FormatFromPath("x.htm"));
        Assert.Equal(".md", HtmlReportService.ExtensionFor(ReportFormat.Markdown));
    }

    [Fact]
    public void SnapshotSummary_picks_package_temperature_and_sums_power()
    {
        var readings = new List<SensorReading>
        {
            new("cpu.core1", "Intel CPU", HardwareKind.Cpu, SensorKind.Temperature, "Core #1", 70, 40, 75, "°C"),
            new("cpu.pkg", "Intel CPU", HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", 62, 40, 70, "°C"),
            new("cpu.load", "Intel CPU", HardwareKind.Cpu, SensorKind.Load, "CPU Total", 23, 0, 100, "%"),
            new("cpu.pow", "Intel CPU", HardwareKind.Cpu, SensorKind.Power, "CPU Package", 45, 0, 200, "W"),
            new("gpu.hot", "GeForce", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", 71, 0, 0, "°C"),
            new("gpu.core", "GeForce", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 58, 0, 0, "°C"),
            new("gpu.pow", "GeForce", HardwareKind.Gpu, SensorKind.Power, "GPU Package", 120, 0, 0, "W"),
            new("ram.load", "Memory", HardwareKind.Memory, SensorKind.Load, "Memory", 41, 0, 0, "%"),
            new("ram.virt", "Memory", HardwareKind.Memory, SensorKind.Load, "Virtual Memory", 60, 0, 0, "%"),
            new("ram.used", "Memory", HardwareKind.Memory, SensorKind.Data, "Memory Used", 13.1, 0, 0, "GB"),
            new("ssd", "SSD", HardwareKind.Storage, SensorKind.Temperature, "Temperature", 39, 0, 0, "°C"),
            new("hdd", "HDD", HardwareKind.Storage, SensorKind.Temperature, "Temperature", 44, 0, 0, "°C"),
        };
        var head = SnapshotSummary.Summarize(new SensorSnapshot(DateTimeOffset.Now, readings));
        Assert.Equal(62, head.CpuTempC);
        Assert.Equal(58, head.GpuTempC);
        Assert.Equal(23, head.CpuLoadPercent);
        Assert.Equal(41, head.MemoryUsedPercent);
        Assert.Equal(13.1, head.MemoryUsedGb);
        Assert.Equal(44, head.HottestDriveC);
        Assert.Equal(165, head.TotalPowerW);
        Assert.Equal("Intel CPU", head.CpuName);
        Assert.Equal(HeadlineReadings.Empty, SnapshotSummary.Summarize(null));
        Assert.Equal(HeadlineReadings.Empty, SnapshotSummary.Summarize(SensorSnapshot.Empty));
    }

    [Fact]
    public void SnapshotSummary_falls_back_to_hottest_reading_without_a_preferred_name()
    {
        var readings = new List<SensorReading>
        {
            new("a", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Sensor A", 50, null, null, "°C"),
            new("b", "CPU", HardwareKind.Cpu, SensorKind.Temperature, "Sensor B", 66, null, null, "°C"),
        };
        Assert.Equal(66, SnapshotSummary.Summarize(new SensorSnapshot(DateTimeOffset.Now, readings)).CpuTempC);
    }

    [Theory]
    [InlineData(95, "Excellent")]
    [InlineData(80, "Good")]
    [InlineData(60, "Needs attention")]
    [InlineData(10, "Problems found")]
    [InlineData(0, "Not checked")]
    public void Score_labels_are_plain_english(int score, string label) => Assert.Equal(label, ReportText.ScoreLabel(score));

    [Fact]
    public void ReportText_formats_durations_sizes_and_file_names()
    {
        Assert.Equal("3 days 4 h", ReportText.Duration(TimeSpan.FromHours(76)));
        Assert.Equal("5 h 12 min", ReportText.Duration(new TimeSpan(5, 12, 0)));
        Assert.Equal("9 min", ReportText.Duration(TimeSpan.FromMinutes(9)));
        Assert.Equal("0 min", ReportText.Duration(TimeSpan.FromMinutes(-5)));
        Assert.Equal("1.00 GB", ReportText.Bytes(1L << 30));
        Assert.Equal("—", ReportText.Bytes(null));
        Assert.Equal("pc-checker-report-GAMING-DESKTOP-2026-10-26-1412", ReportText.SuggestedFileName("GAMING DESKTOP", new DateTimeOffset(2026, 10, 26, 14, 12, 0, TimeSpan.Zero)));
        Assert.Equal("A B", ReportText.Join(" A ", null, "", "B"));
        Assert.Null(ReportText.Join(null, " "));
        Assert.Equal("x", ReportText.Or(null, "x"));
        Assert.Equal("—", ReportText.Or(null, null));
        Assert.Equal("a \\| b", ReportText.Markdown("a | b"));
    }
}
