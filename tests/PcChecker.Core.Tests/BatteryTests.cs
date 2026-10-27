using System.Globalization;
using PcChecker.Core.Battery;
using PcChecker.Core.Common;
using PcChecker.Core.Power;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Battery report parsing, ISO 8601 durations, wear and life maths, the capacity chart and the page rules.</summary>
public class BatteryTests
{
    /// <summary>
    /// Trimmed from a real <c>powercfg /batteryreport /xml</c> of a two-year-old laptop: weekly history rows, then
    /// daily ones, one row without capacity data, three days of recent use and the report's own marker at the end.
    /// </summary>
    private const string SampleReport = """
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <ReportInformation>
            <ReportVersion>1</ReportVersion>
            <ReportGuid>{6b1f0c9e-6f5d-4c35-9a3e-0b8d6f9b3c21}</ReportGuid>
            <ScanTime>2026-10-27T13:30:12Z</ScanTime>
            <LocalScanTime>2026-10-27T14:30:12</LocalScanTime>
            <UtcOffset>PT1H</UtcOffset>
          </ReportInformation>
          <SystemInformation>
            <ComputerName>LAPTOP-7Q2K</ComputerName>
            <SystemManufacturer>LENOVO</SystemManufacturer>
            <SystemProductName>21CB</SystemProductName>
            <BIOSDate>2024-05-10</BIOSDate>
            <BIOSVersion>N3AET80W (1.45 )</BIOSVersion>
            <OSBuild>26100.1.amd64fre.ge_release.240331-1435</OSBuild>
            <PlatformRole>Mobile</PlatformRole>
            <ConnectedStandby>1</ConnectedStandby>
          </SystemInformation>
          <Batteries>
            <Battery>
              <Id>5B10W51867</Id>
              <Manufacturer>SMP</Manufacturer>
              <SerialNumber>2841</SerialNumber>
              <ManufactureDate />
              <Chemistry>LiP</Chemistry>
              <LongTerm>1</LongTerm>
              <RelativeCapacity>0</RelativeCapacity>
              <DesignCapacity>57000</DesignCapacity>
              <FullChargeCapacity>49240</FullChargeCapacity>
              <CycleCount>318</CycleCount>
            </Battery>
          </Batteries>
          <RuntimeEstimates>
            <FullChargeCapacity>
              <ActiveRuntime>PT5H43M</ActiveRuntime>
              <ConnectedStandbyRuntime>P9DT7H</ConnectedStandbyRuntime>
            </FullChargeCapacity>
            <DesignCapacity>
              <ActiveRuntime>PT6H37M30S</ActiveRuntime>
              <ConnectedStandbyRuntime>P10DT19H</ConnectedStandbyRuntime>
            </DesignCapacity>
          </RuntimeEstimates>
          <RecentUsage>
            <UsageEntry Timestamp="2026-10-27T06:05:00Z" LocalTimestamp="2026-10-27T07:05:00" Ac="0" EntryType="Active" ChargeCapacity="40000" FullChargeCapacity="49240" IsNextOnBattery="0" />
            <UsageEntry Timestamp="2026-10-27T07:35:00Z" LocalTimestamp="2026-10-27T08:35:00" Ac="0" EntryType="ConnectedStandby" ChargeCapacity="32000" FullChargeCapacity="49240" IsNextOnBattery="0" />
            <UsageEntry Timestamp="2026-10-27T08:05:00Z" LocalTimestamp="2026-10-27T09:05:00" Ac="1" EntryType="Active" ChargeCapacity="31900" FullChargeCapacity="49240" IsNextOnBattery="0" />
            <UsageEntry Timestamp="2026-10-27T13:30:12Z" LocalTimestamp="2026-10-27T14:30:12" Ac="1" EntryType="ReportGenerated" ChargeCapacity="49240" FullChargeCapacity="49240" IsNextOnBattery="0" />
          </RecentUsage>
          <History>
            <HistoryEntry StartDate="2024-10-28" EndDate="2024-11-04" LocalStartDate="2024-10-28" LocalEndDate="2024-11-04" DesignCapacity="57000" FullChargeCapacity="57820" CycleCount="2" ActiveAcTime="PT20H10M" CsAcTime="P2DT10H" ActiveDcTime="PT9H5M" CsDcTime="P1DT14H" ActiveDcEnergy="80000" CsDcEnergy="8000" />
            <HistoryEntry StartDate="2025-10-27" EndDate="2025-11-03" LocalStartDate="2025-10-27" LocalEndDate="2025-11-03" DesignCapacity="57000" FullChargeCapacity="53100" CycleCount="160" ActiveAcTime="PT18H" CsAcTime="P2DT2H" ActiveDcTime="PT8H40M" CsDcTime="P1DT20H" ActiveDcEnergy="76000" CsDcEnergy="9000" />
            <HistoryEntry StartDate="2026-09-01" EndDate="2026-09-08" LocalStartDate="2026-09-01" LocalEndDate="2026-09-08" DesignCapacity="0" FullChargeCapacity="0" CycleCount="0" ActiveAcTime="PT1H" CsAcTime="PT0S" ActiveDcTime="PT0S" CsDcTime="PT0S" ActiveDcEnergy="0" CsDcEnergy="0" />
            <HistoryEntry StartDate="2026-10-25" EndDate="2026-10-26" LocalStartDate="2026-10-25" LocalEndDate="2026-10-26" DesignCapacity="57000" FullChargeCapacity="49300" CycleCount="317" ActiveAcTime="PT3H" CsAcTime="PT9H" ActiveDcTime="PT1H20M" CsDcTime="PT5H" ActiveDcEnergy="11000" CsDcEnergy="1200" />
            <HistoryEntry StartDate="2026-10-26" EndDate="2026-10-27" LocalStartDate="2026-10-26" LocalEndDate="2026-10-27" DesignCapacity="57000" FullChargeCapacity="49240" CycleCount="318" ActiveAcTime="PT2H30M" CsAcTime="PT10H" ActiveDcTime="PT1H30M" CsDcTime="PT6H" ActiveDcEnergy="12500" CsDcEnergy="1300" />
          </History>
          <EnergyDrains />
          <Estimates>
            <EstimateEntry StartDate="2026-10-26" EndDate="2026-10-27" LocalStartDate="2026-10-26" LocalEndDate="2026-10-27" FullChargeCapacity="49240" FullChargeActiveRuntime="PT5H40M" FullChargeConnectedStandbyRuntime="P9DT2H" DesignCapacity="57000" DesignActiveRuntime="PT6H33M" DesignConnectedStandbyRuntime="P10DT12H" ActiveDcTime="PT1H30M" CsDcTime="PT6H" ActiveDcEnergy="12500" CsDcEnergy="1300" />
          </Estimates>
        </BatteryReport>
        """;

    private static readonly Func<DateTime, TimeSpan> Utc = _ => TimeSpan.Zero;

    // ----- Battery report XML -----

    [Fact]
    public void Report_batteries_and_overall_estimate_are_read()
    {
        var report = BatteryReportParser.Parse(SampleReport);

        var pack = Assert.Single(report.Batteries);
        Assert.Equal("5B10W51867", pack.Name);
        Assert.Equal("SMP", pack.Manufacturer);
        Assert.Equal("Lithium polymer", pack.Chemistry);
        Assert.Equal(57_000, pack.DesignCapacityMwh);
        Assert.Equal(49_240, pack.FullChargeCapacityMwh);
        Assert.Equal(318, pack.CycleCount);
        Assert.Equal("Mobile", report.PlatformRole);
        Assert.Equal(new DateTime(2026, 10, 27, 14, 30, 12), report.ScanTime);

        var estimate = Assert.IsType<BatteryRuntimeEstimate>(report.CurrentEstimate);
        Assert.Equal(new TimeSpan(5, 43, 0), estimate.ActiveAtFullCharge);
        Assert.Equal(new TimeSpan(6, 37, 30), estimate.ActiveAtDesign);
        Assert.Equal(TimeSpan.FromDays(9) + TimeSpan.FromHours(7), estimate.StandbyAtFullCharge);
    }

    [Fact]
    public void Report_history_gives_capacity_points_and_usage_periods()
    {
        var report = BatteryReportParser.Parse(SampleReport);

        // The row without capacity data has no capacity point, but its plugged-in hour still counts as usage.
        Assert.Equal(4, report.CapacityHistory.Count);
        Assert.DoesNotContain(report.CapacityHistory, p => p.Start == new DateTime(2026, 9, 1));
        Assert.Equal(5, report.UsageHistory.Count);

        var first = report.CapacityHistory[0];
        Assert.Equal(new DateTime(2024, 10, 28), first.Start);
        Assert.Equal(new DateTime(2024, 11, 4), first.End);
        Assert.Equal(57_820, first.FullChargeCapacityMwh);
        Assert.Equal(2, first.CycleCount);

        var week = report.UsageHistory[0];
        Assert.Equal(new TimeSpan(9, 5, 0), week.ActiveOnBattery);
        Assert.Equal(TimeSpan.FromHours(38), week.StandbyOnBattery);
        Assert.Equal(new TimeSpan(20, 10, 0), week.ActiveOnAc);
        Assert.Equal(TimeSpan.FromHours(58), week.StandbyOnAc);

        var life = Assert.Single(report.LifeEstimates);
        Assert.Equal(new TimeSpan(5, 40, 0), life.ActiveAtFullCharge);
        Assert.Equal(new TimeSpan(6, 33, 0), life.ActiveAtDesign);
        Assert.Equal(57_000, life.DesignCapacityMwh);
    }

    [Fact]
    public void Recent_usage_is_closed_by_the_next_entry_and_drops_the_report_marker()
    {
        var usage = BatteryReportParser.Parse(SampleReport).RecentUsage;

        Assert.Equal(3, usage.Count);
        Assert.DoesNotContain(usage, u => u.State == BatteryUsageState.ReportGenerated);

        var active = usage[0];
        Assert.Equal(BatteryUsageState.Active, active.State);
        Assert.False(active.OnAc);
        Assert.Equal(TimeSpan.FromMinutes(90), active.Duration);
        Assert.Equal(40_000, active.StartChargeMwh);
        Assert.Equal(32_000, active.EndChargeMwh);
        Assert.Equal(16.2, active.DrainPercent!.Value, 1);       // 8 000 of 49 240 mWh
        Assert.Equal(10.8, active.DrainPercentPerHour!.Value, 1);

        Assert.Equal(BatteryUsageState.Standby, usage[1].State);
        var plugged = usage[2];
        Assert.True(plugged.OnAc);
        Assert.Equal(new TimeSpan(5, 25, 12), plugged.Duration); // until the report was generated
        Assert.True(plugged.DrainPercent < 0);                   // charging
    }

    [Fact]
    public void Report_parsing_ignores_namespace_case_and_missing_sections()
    {
        const string xml = """
            <batteryreport>
              <batteries><battery><id>DELL 0F7HD8E</id><designcapacity>54000</designcapacity><fullchargecapacity>51000</fullchargecapacity><cyclecount>0</cyclecount><chemistry>LION</chemistry></battery></batteries>
            </batteryreport>
            """;
        var report = BatteryReportParser.Parse(xml);

        var pack = Assert.Single(report.Batteries);
        Assert.Equal("DELL 0F7HD8E", pack.Name);
        Assert.Equal("Lithium-ion", pack.Chemistry);
        Assert.Null(pack.CycleCount); // 0 means the battery does not count cycles
        Assert.Empty(report.CapacityHistory);
        Assert.Empty(report.RecentUsage);
        Assert.Null(report.CurrentEstimate);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<html><body>Battery report</body></html>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE BatteryReport [<!ENTITY x \"y\">]><BatteryReport>&x;</BatteryReport>")]
    public void Anything_but_a_battery_report_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => BatteryReportParser.Parse(text));
    }

    [Fact]
    public void Report_parsing_does_not_depend_on_the_display_language()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var german = BatteryReportParser.Parse(SampleReport);
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var arabic = BatteryReportParser.Parse(SampleReport);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = BatteryReportParser.Parse(SampleReport);

            Assert.Equal(invariant.CapacityHistory, german.CapacityHistory);
            Assert.Equal(invariant.CapacityHistory, arabic.CapacityHistory);
            Assert.Equal(invariant.RecentUsage, german.RecentUsage);
            Assert.Equal(new DateTime(2025, 10, 27), arabic.CapacityHistory[1].Start);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("Active", BatteryUsageState.Active)]
    [InlineData("ConnectedStandby", BatteryUsageState.Standby)]
    [InlineData("Suspend", BatteryUsageState.Standby)]
    [InlineData("Hibernate", BatteryUsageState.Off)]
    [InlineData("ReportGenerated", BatteryUsageState.ReportGenerated)]
    [InlineData("Something new", BatteryUsageState.Unknown)]
    [InlineData(null, BatteryUsageState.Unknown)]
    public void Entry_types_map_to_usage_states(string? entryType, BatteryUsageState expected)
    {
        Assert.Equal(expected, BatteryReportParser.MapEntryType(entryType));
    }

    // ----- ISO 8601 durations -----

    [Theory]
    [InlineData("PT1H30M", 90 * 60)]
    [InlineData("PT45S", 45)]
    [InlineData("PT45.5S", 45.5)]
    [InlineData("P1DT2H", 26 * 3600)]
    [InlineData("P2D", 2 * 86400)]
    [InlineData("P1W", 7 * 86400)]
    [InlineData("PT0S", 0)]
    [InlineData("pt1h", 3600)]
    [InlineData("-PT5M", -300)]
    [InlineData("PT1,5H", 5400)]
    public void Iso_durations_parse(string text, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), IsoDuration.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1DT")]
    [InlineData("1H30M")]
    [InlineData("PT5")]
    [InlineData("PTM")]
    [InlineData("P1Y")]
    [InlineData("P1M")]
    [InlineData("PT1H1H")]
    [InlineData("PT30M1H")]
    [InlineData("01:30:00")]
    public void Invalid_or_ambiguous_durations_are_refused(string? text)
    {
        Assert.Null(IsoDuration.Parse(text));
    }

    [Theory]
    [InlineData(5 * 3600 + 43 * 60, "PT5H43M")]
    [InlineData(2 * 86400 + 30, "P2DT30S")]
    [InlineData(86400, "P1D")]
    [InlineData(0, "PT0S")]
    public void Iso_durations_format_and_round_trip(int seconds, string expected)
    {
        var value = TimeSpan.FromSeconds(seconds);
        Assert.Equal(expected, IsoDuration.Format(value));
        Assert.Equal(value, IsoDuration.Parse(IsoDuration.Format(value)));
    }

    // ----- Wear, drain and life maths -----

    [Fact]
    public void Wear_and_health_come_from_design_and_full_charge_capacity()
    {
        Assert.Equal(13.6, BatteryMath.WearPercent(57_000, 49_240)!.Value, 1);
        Assert.Equal(86.4, BatteryMath.HealthPercent(57_000, 49_240)!.Value, 1);

        // New packs often hold a little more than designed: health caps at 100 and wear at 0.
        Assert.Equal(100d, BatteryMath.HealthPercent(57_000, 57_820));
        Assert.Equal(0d, BatteryMath.WearPercent(57_000, 57_820));
        Assert.Null(BatteryMath.HealthPercent(0, 49_240));
        Assert.Null(BatteryMath.HealthPercent(57_000, null));

        Assert.Equal(HealthStatus.Good, BatteryMath.Status(13.6));
        Assert.Equal(HealthStatus.Warning, BatteryMath.Status(25));
        Assert.Equal(HealthStatus.Critical, BatteryMath.Status(41));
        Assert.Equal(HealthStatus.Unknown, BatteryMath.Status(null));
    }

    [Fact]
    public void Life_estimate_prefers_the_report_then_scales_then_uses_the_drain()
    {
        var five = TimeSpan.FromHours(5);
        var six = TimeSpan.FromHours(6);
        var report = new BatteryRuntimeEstimate(five, null, six, null);
        AssertLife(five, six, BatteryMath.EstimateLife(report, 60_000, 50_000, 10));

        // Only the design figure: scaled down by the capacity that is left.
        AssertLife(five, six, BatteryMath.EstimateLife(new BatteryRuntimeEstimate(null, null, six, null), 60_000, 50_000, null));

        // Only the full-charge figure: scaled up for "when new".
        AssertLife(five, six, BatteryMath.EstimateLife(new BatteryRuntimeEstimate(five, null, null, null), 60_000, 50_000, null));

        // No report: 50 Wh at 10 W lasts 5 hours, 60 Wh lasted 6.
        AssertLife(five, six, BatteryMath.EstimateLife(null, 60_000, 50_000, 10));
        AssertLife(null, null, BatteryMath.EstimateLife(null, 60_000, 50_000, null));
        Assert.Null(BatteryMath.RuntimeFromDrain(50_000, 0.1)); // too little drain to trust
    }

    [Fact]
    public void Drain_is_measured_only_over_long_enough_stretches()
    {
        var start = new DateTime(2026, 10, 27, 9, 0, 0);
        var hour = new BatteryUsageEntry(start, TimeSpan.FromHours(2), BatteryUsageState.Active, false, 40_000, 30_000, 50_000);
        Assert.Equal(20d, hour.DrainPercent);
        Assert.Equal(10d, hour.DrainPercentPerHour);

        var blip = hour with { Duration = TimeSpan.FromMinutes(2) };
        Assert.Equal(20d, blip.DrainPercent);
        Assert.Null(blip.DrainPercentPerHour);

        var unknown = hour with { EndChargeMwh = null };
        Assert.Null(unknown.DrainPercent);

        // Average power while in use on battery: 10 Wh over 2 hours; charging and standby stretches are ignored.
        var charging = new BatteryUsageEntry(start, TimeSpan.FromHours(1), BatteryUsageState.Active, true, 30_000, 45_000, 50_000);
        var asleep = new BatteryUsageEntry(start, TimeSpan.FromHours(8), BatteryUsageState.Standby, false, 30_000, 29_000, 50_000);
        Assert.Equal(5d, BatteryMath.AverageActiveDrainW([hour, charging, asleep]));
        Assert.Null(BatteryMath.AverageActiveDrainW([charging, asleep]));
    }

    [Fact]
    public void Monthly_capacity_loss_needs_two_months_of_falling_capacity()
    {
        var history = new List<BatteryCapacityPoint>
        {
            new(new DateTime(2025, 1, 1), new DateTime(2025, 1, 8), 50_000, 50_000, 1),
            new(new DateTime(2025, 12, 31), new DateTime(2026, 1, 1), 50_000, 44_000, 200),
        };
        // 12 % of the design capacity over 365 days.
        Assert.Equal(12.0 / 365 * 30, BatteryMath.MonthlyLossPercent(history)!.Value, 3);

        Assert.Null(BatteryMath.MonthlyLossPercent(history.Take(1).ToList()));
        Assert.Null(BatteryMath.MonthlyLossPercent([history[0], history[0] with { Start = new DateTime(2025, 1, 20), End = new DateTime(2025, 1, 21), FullChargeCapacityMwh = 49_000 }]));
        Assert.Null(BatteryMath.MonthlyLossPercent([history[0], history[1] with { FullChargeCapacityMwh = 51_000 }])); // recalibrated upwards
    }

    [Fact]
    public void Summary_adds_up_two_batteries()
    {
        var packs = new List<BatteryPackInfo>
        {
            new("Front", "LGC", "Lithium-ion", 24_000, 20_000, 300),
            new("Rear", "LGC", "Lithium-ion", 48_000, 43_000, 280),
        };
        var overview = new BatteryOverview(true, PcFormFactor.Laptop, packs, null, BatteryReportData.Empty, DateTimeOffset.Now, []);
        var summary = BatteryMath.Summarize(overview);

        Assert.Equal(72_000, summary.DesignCapacityMwh);
        Assert.Equal(63_000, summary.FullChargeCapacityMwh);
        Assert.Equal(12.5d, summary.WearPercent);
        Assert.Equal(300, summary.CycleCount);

        var partial = overview with { Packs = [packs[0], packs[1] with { DesignCapacityMwh = null }] };
        Assert.Null(BatteryMath.Summarize(partial).WearPercent);
    }

    [Theory]
    [InlineData(5 * 3600 + 43 * 60, "5 h 43 min")]
    [InlineData(2 * 3600, "2 h")]
    [InlineData(45 * 60, "45 min")]
    [InlineData(30, "under 1 min")]
    [InlineData(9 * 86400 + 7 * 3600, "9 d 7 h")]
    public void Durations_are_short_and_readable(int seconds, string expected)
    {
        Assert.Equal(expected, BatteryMath.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    private static void AssertLife(TimeSpan? now, TimeSpan? whenNew, (TimeSpan? Now, TimeSpan? WhenNew) actual)
    {
        Assert.Equal(now, actual.Now);
        Assert.Equal(whenNew, actual.WhenNew);
    }

    // ----- Capacity chart -----

    [Fact]
    public void Capacity_series_is_sorted_merged_by_day_and_in_watt_hours()
    {
        var history = new List<BatteryCapacityPoint>
        {
            new(new DateTime(2026, 10, 26), new DateTime(2026, 10, 27), 57_000, 49_240, 318),
            new(new DateTime(2024, 10, 28), new DateTime(2024, 11, 4), 57_000, 57_820, 2),
            new(new DateTime(2025, 10, 27), new DateTime(2025, 11, 3), 57_000, null, 160),
            new(new DateTime(2026, 10, 26, 12, 0, 0), new DateTime(2026, 10, 27), 57_000, 49_200, 318),
        };
        var series = BatteryChartBuilder.CapacitySeries(history, Utc);

        Assert.Equal(2, series.Points.Count);
        Assert.Equal(57.82, series.Points[0].Value, 3);
        Assert.Equal(49.2, series.Points[1].Value, 3); // the later reading of the same day wins
        Assert.Equal(new DateTimeOffset(2024, 10, 28, 0, 0, 0, TimeSpan.Zero), series.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), series.To);
        Assert.Equal(57d, series.DesignWh);
        Assert.True(series.Maximum > series.DesignWh, "The design line must sit inside the chart.");
        Assert.True(series.Minimum < 49.2);
        Assert.True(series.HasData);
    }

    [Fact]
    public void Chart_range_uses_whole_steps_and_never_goes_below_zero()
    {
        Assert.Equal((40d, 65d), BatteryChartBuilder.Range([49.2, 57.8], 57));
        Assert.Equal((0d, 4d), BatteryChartBuilder.Range([0.5, 3.5], double.NaN));
        Assert.Equal((0d, 100d), BatteryChartBuilder.Range([], double.NaN));

        var empty = BatteryChartBuilder.CapacitySeries([], Utc);
        Assert.False(empty.HasData);
        Assert.Empty(empty.Points);
    }

    [Fact]
    public void Gap_threshold_follows_the_row_length()
    {
        var weekly = new List<BatteryCapacityPoint> { new(new DateTime(2026, 1, 1), new DateTime(2026, 1, 8), 1, 1, null) };
        var daily = new List<BatteryCapacityPoint> { new(new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), 1, 1, null) };
        Assert.Equal(TimeSpan.FromDays(14), BatteryChartBuilder.GapThreshold(weekly));
        Assert.Equal(TimeSpan.FromDays(2), BatteryChartBuilder.GapThreshold(daily));
        Assert.Equal(TimeSpan.FromDays(2), BatteryChartBuilder.GapThreshold([]));
    }

    // ----- Page rules -----

    [Fact]
    public void Desktops_get_the_empty_state_and_laptops_the_page()
    {
        var laptopPack = new BatteryPackInfo("5B10W51867", "SMP", "Lithium polymer", 57_000, 49_240, 318);
        var laptop = new BatteryOverview(true, PcFormFactor.Laptop, [laptopPack], null, BatteryReportData.Empty, DateTimeOffset.Now, []);

        Assert.Equal(BatteryPageState.NotLoaded, BatteryPageRules.Classify(null));
        Assert.Equal(BatteryPageState.NoBattery, BatteryPageRules.Classify(BatteryOverview.NoBattery(PcFormFactor.Desktop, DateTimeOffset.Now)));
        Assert.Equal(BatteryPageState.Ready, BatteryPageRules.Classify(laptop));
        Assert.Equal(BatteryPageState.Ready, BatteryPageRules.Classify(laptop with { FormFactor = PcFormFactor.Unknown }));

        // A UPS on USB shows up as a battery: by its name anywhere, and any battery on a desktop.
        var ups = new BatteryPackInfo("Back-UPS ES 700G", "American Power Conversion", null, null, null, null);
        Assert.Equal(BatteryPageState.UpsOnly, BatteryPageRules.Classify(laptop with { Packs = [ups], FormFactor = PcFormFactor.Unknown }));
        Assert.Equal(BatteryPageState.UpsOnly, BatteryPageRules.Classify(laptop with { FormFactor = PcFormFactor.Desktop }));
        Assert.True(BatteryPageRules.LooksLikeUps(new BatteryPackInfo("CP1500PFCLCD", "CyberPower", null, null, null, null)));
        Assert.False(BatteryPageRules.LooksLikeUps(laptopPack));
        Assert.False(BatteryPageRules.LooksLikeUps(new BatteryPackInfo("Battery", "Dell", "Lithium-ion", null, null, null)));
    }

    [Theory]
    [InlineData(1, PcFormFactor.Desktop)]
    [InlineData(3, PcFormFactor.Desktop)]
    [InlineData(2, PcFormFactor.Laptop)]
    [InlineData(8, PcFormFactor.Laptop)]
    [InlineData(4, PcFormFactor.Server)]
    [InlineData(0, PcFormFactor.Unknown)]
    [InlineData(null, PcFormFactor.Unknown)]
    public void Pc_system_type_maps_to_a_form_factor(int? code, PcFormFactor expected)
    {
        Assert.Equal(expected, BatteryPageRules.FormFactor(code));
    }

    [Fact]
    public void Chart_needs_two_days_and_recent_rows_skip_blips_newest_first()
    {
        var day = new BatteryCapacityPoint(new DateTime(2026, 10, 26), new DateTime(2026, 10, 27), 57_000, 49_240, 318);
        Assert.False(BatteryPageRules.CanDrawCapacityChart([day]));
        Assert.False(BatteryPageRules.CanDrawCapacityChart([day, day with { Start = day.Start.AddHours(6) }]));
        Assert.False(BatteryPageRules.CanDrawCapacityChart([day, day with { Start = day.Start.AddDays(1), FullChargeCapacityMwh = null }]));
        Assert.True(BatteryPageRules.CanDrawCapacityChart([day, day with { Start = day.Start.AddDays(1) }]));

        var t = new DateTime(2026, 10, 27, 8, 0, 0);
        var rows = BatteryPageRules.RecentRows(
        [
            new(t, TimeSpan.FromHours(1), BatteryUsageState.Active, false, 40_000, 35_000, 49_000),
            new(t.AddHours(1), TimeSpan.FromSeconds(20), BatteryUsageState.Standby, false, 35_000, 35_000, 49_000),
            new(t.AddHours(2), TimeSpan.FromHours(3), BatteryUsageState.Active, true, 35_000, 49_000, 49_000),
        ], max: 5);
        Assert.Equal(new[] { t.AddHours(2), t }, rows.Select(r => r.Start));
        Assert.Single(BatteryPageRules.RecentRows(rows, max: 1));
    }

    [Fact]
    public void Tips_put_replacement_and_charge_limits_first_when_they_apply()
    {
        var worn = new BatterySummary(57_000, 30_000, 52.6, 47.4, 900, null, null, null);
        var fine = worn with { WearPercent = 10, HealthPercent = 90 };
        var end = new DateTime(2026, 10, 27);
        var desk = new List<BatteryUsagePeriod> { new(end.AddDays(-7), end, TimeSpan.FromHours(1), TimeSpan.Zero, TimeSpan.FromHours(30), TimeSpan.FromHours(40)) };
        var travel = new List<BatteryUsagePeriod> { new(end.AddDays(-7), end, TimeSpan.FromHours(20), TimeSpan.FromHours(30), TimeSpan.FromHours(5), TimeSpan.Zero) };

        Assert.Equal(BatteryPageRules.TipIds.Replace, BatteryPageRules.Tips(worn, travel)[0]);
        Assert.Equal(BatteryPageRules.TipIds.ChargeLimit, BatteryPageRules.Tips(fine, desk)[0]);
        Assert.Equal(BatteryPageRules.TipIds.BatterySaver, BatteryPageRules.Tips(fine, travel)[0]);
        Assert.Contains(BatteryPageRules.TipIds.ChargeLimit, BatteryPageRules.Tips(fine, travel));
        Assert.True(BatteryPageRules.MostlyPluggedIn(desk));
        Assert.False(BatteryPageRules.MostlyPluggedIn(travel));
        Assert.False(BatteryPageRules.MostlyPluggedIn([]));
    }

    // ----- Sources -----

    [Fact]
    public void Wmi_chemistry_codes_and_live_readings_are_decoded()
    {
        Assert.Equal("Lithium-ion", BatterySources.DecodeChemistry(0x4E4F494C));   // "LION"
        Assert.Equal("Lithium polymer", BatterySources.DecodeChemistry(0x0050694C)); // "LiP"
        Assert.Null(BatterySources.DecodeChemistry(0));
        Assert.Null(BatterySources.DecodeChemistry(0x01020304));

        var discharging = new BatteryReading(true, true, false, 76, 7.8, null, 37_000, 49_000, TimeSpan.FromHours(4.7), null);
        var live = BatterySources.Live(discharging)!;
        Assert.Equal(BatteryPowerState.Discharging, live.State);
        Assert.Equal(7.8d, live.RateW);
        Assert.Equal(TimeSpan.FromHours(4.7), live.TimeRemaining);

        var charging = BatterySources.Live(discharging with { OnBattery = false, Charging = true, ChargeW = 45 })!;
        Assert.Equal(BatteryPowerState.Charging, charging.State);
        Assert.Equal(45d, charging.RateW);
        Assert.Null(charging.TimeRemaining);
        Assert.Equal(BatteryPowerState.PluggedInNotCharging, BatterySources.Live(discharging with { OnBattery = false })!.State);
        Assert.Null(BatterySources.Live(BatteryReading.None()));
    }

    [Fact]
    public void Wmi_packs_are_completed_from_the_report()
    {
        var wmi = new List<BatteryPackInfo> { new("Battery 1", null, "Unknown", 57_000, 49_240, null) };
        var report = new List<BatteryPackInfo> { new("5B10W51867", "SMP", "Lithium polymer", 57_000, 49_100, 318) };

        var merged = Assert.Single(BatterySources.MergePacks(wmi, report));
        Assert.Equal("5B10W51867", merged.Name);
        Assert.Equal("SMP", merged.Manufacturer);
        Assert.Equal("Lithium polymer", merged.Chemistry);
        Assert.Equal(49_240, merged.FullChargeCapacityMwh); // WMI is newer than the report
        Assert.Equal(318, merged.CycleCount);
        Assert.Same(report, BatterySources.MergePacks([], report));
        Assert.Equal("battery-report-2026-10-27-0905.html", BatterySources.ReportFileName(new DateTime(2026, 10, 27, 9, 5, 0)));
    }

    // ----- Demo -----

    [Fact]
    public async Task Demo_describes_a_two_year_old_laptop_battery()
    {
        var now = new DateTimeOffset(2026, 10, 27, 14, 30, 0, TimeSpan.Zero);
        var overview = DemoBatteryService.Build(now);
        var summary = BatteryMath.Summarize(overview);

        Assert.Equal(BatteryPageState.Ready, BatteryPageRules.Classify(overview));
        Assert.Equal(13.6, summary.WearPercent!.Value, 1);
        Assert.Equal(318, summary.CycleCount);
        Assert.True(summary.LifeNow < summary.LifeWhenNew);
        Assert.InRange(summary.MonthlyLossPercent!.Value, 0.3, 1.0);
        Assert.True(BatteryPageRules.CanDrawCapacityChart(overview.Report.CapacityHistory));
        Assert.InRange((overview.Report.CapacityHistory[^1].End - overview.Report.CapacityHistory[0].Start).TotalDays, 700, 740);
        Assert.NotEmpty(BatteryPageRules.RecentRows(overview.Report.RecentUsage));
        Assert.All(overview.Report.RecentUsage, u => Assert.True(u.Start < now.DateTime));
        Assert.Equal(overview.Report.UsageHistory.Count, overview.Report.LifeEstimates.Count);

        var svc = new DemoBatteryService();
        var loaded = await svc.LoadAsync(null, CancellationToken.None);
        Assert.Same(loaded, svc.Last);
        Assert.True(loaded.HasBattery);
    }
}
