using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class HistoryCodecTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Format_and_parse_round_trip_with_invariant_numbers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var sample = new HistorySample(T0, 60, new Dictionary<string, double>
            {
                [HistoryMetrics.CpuTemp] = 71.456,
                [HistoryMetrics.Power] = 143,
                [HistoryMetrics.DiskFree("C:")] = 512.25,
            });

            string line = HistoryLineCodec.Format(sample);
            Assert.DoesNotContain('\n', line);
            Assert.Contains("71.46", line);
            Assert.True(HistoryLineCodec.TryParse(line, out var parsed));
            Assert.Equal(T0, parsed.Time);
            Assert.Equal(60, parsed.SpanSeconds);
            Assert.Equal(71.46, parsed.Get(HistoryMetrics.CpuTemp));
            Assert.Equal(512.25, parsed.Get("disk.free:C:"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"t\":1761559200,\"s\":60,\"v\":{\"cpu.temp\":6")]
    [InlineData("not json at all")]
    [InlineData("{\"s\":60,\"v\":{\"cpu.temp\":60}}")]
    [InlineData("{\"t\":1761559200,\"v\":{}}")]
    [InlineData("{\"t\":1761559200,\"v\":{\"cpu.temp\":\"hot\"}}")]
    [InlineData("{\"t\":-5,\"v\":{\"cpu.temp\":60}}")]
    [InlineData("[1,2,3]")]
    public void Corrupt_partial_and_empty_lines_are_rejected(string line)
    {
        Assert.False(HistoryLineCodec.TryParse(line, out _));
    }

    [Fact]
    public void Parsing_a_file_skips_bad_lines_counts_them_and_keeps_the_rest()
    {
        string[] lines =
        [
            "{\"t\":1761559200,\"s\":60,\"v\":{\"cpu.temp\":60}}",
            "",
            "{\"t\":1761559260,\"s\":60,\"v\":{\"cpu.te",
            "\u0000\u0000\u0000",
            "{\"t\":1761559320,\"v\":{\"cpu.temp\":62,\"bad\":\"x\"}}",
        ];

        var (samples, corrupt) = HistoryLineCodec.ParseLines(lines);
        Assert.Equal(2, samples.Count);
        Assert.Equal(2, corrupt);
        Assert.Equal(60, samples[1].SpanSeconds); // missing span defaults to one minute
        Assert.Single(samples[1].Values);
    }

    [Fact]
    public void Metric_ids_and_drive_names_are_normalized()
    {
        Assert.Equal("disk.free:C:", HistoryMetrics.DiskFree(@"c:\"));
        Assert.Equal("disk.free:D:", HistoryMetrics.DiskFree("d"));
        Assert.Equal("C:", HistoryMetrics.DriveOf("disk.free:C:"));
        Assert.Null(HistoryMetrics.DriveOf(HistoryMetrics.CpuTemp));
        Assert.Equal(HistoryAggregation.Max, HistoryMetrics.AggregationOf(HistoryMetrics.GpuTemp));
        Assert.Equal(HistoryAggregation.Min, HistoryMetrics.AggregationOf(HistoryMetrics.Health));
        Assert.Equal(HistoryAggregation.Last, HistoryMetrics.AggregationOf("disk.free:C:"));
        Assert.Equal(HistoryAggregation.Average, HistoryMetrics.AggregationOf(HistoryMetrics.CpuLoad));
        Assert.Equal(
            [HistoryMetrics.CpuTemp, HistoryMetrics.Power, HistoryMetrics.Health, "disk.free:C:", "disk.free:D:"],
            HistoryMetrics.Order(["disk.free:D:", HistoryMetrics.Health, "disk.free:C:", HistoryMetrics.Power, HistoryMetrics.CpuTemp]));
        Assert.False(HistoryMetrics.IsValidId("bad\"id"));
    }
}

public class HistoryRollupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static HistorySample Minute(int minute, double temp, double load, double? health = null, double? diskFree = null)
    {
        var values = new Dictionary<string, double> { [HistoryMetrics.CpuTemp] = temp, [HistoryMetrics.CpuLoad] = load };
        if (health is { } h)
        {
            values[HistoryMetrics.Health] = h;
        }

        if (diskFree is { } d)
        {
            values["disk.free:C:"] = d;
        }

        return new HistorySample(T0.AddMinutes(minute), 60, values);
    }

    [Fact]
    public void Compact_combines_each_metric_by_its_rule_in_aligned_buckets()
    {
        var samples = Enumerable.Range(0, 30)
            .Select(i => Minute(i, temp: i == 7 ? 91 : 60, load: i < 15 ? 10 : 50, health: i == 3 ? 80 : i == 20 ? 95 : null, diskFree: 500 - i))
            .ToList();

        var compact = HistoryRollup.Compact(samples, HistoryRollup.FineBucket);

        Assert.Equal(2, compact.Count);
        Assert.Equal(T0, compact[0].Time);
        Assert.Equal(T0.AddMinutes(15), compact[1].Time);
        Assert.All(compact, s => Assert.Equal(900, s.SpanSeconds));
        Assert.Equal(91, compact[0].Get(HistoryMetrics.CpuTemp)); // the peak survives
        Assert.Equal(10, compact[0].Get(HistoryMetrics.CpuLoad));
        Assert.Equal(50, compact[1].Get(HistoryMetrics.CpuLoad));
        Assert.Equal(80, compact[0].Get(HistoryMetrics.Health));
        Assert.Equal(486, compact[0].Get("disk.free:C:")); // last reading of the bucket
        Assert.Equal(471, compact[1].Get("disk.free:C:"));
    }

    [Fact]
    public void Rolling_up_twice_matches_rolling_up_once()
    {
        var random = new Random(7);
        var samples = Enumerable.Range(0, 180).Select(i => Minute(i, 40 + random.Next(50), random.Next(100))).ToList();

        var direct = HistoryRollup.Compact(samples, HistoryRollup.CoarseBucket);
        var staged = HistoryRollup.Compact(HistoryRollup.Compact(samples, HistoryRollup.FineBucket), HistoryRollup.CoarseBucket);
        var again = HistoryRollup.Compact(staged, HistoryRollup.CoarseBucket);

        Assert.Equal(direct.Count, staged.Count);
        for (int i = 0; i < direct.Count; i++)
        {
            Assert.Equal(direct[i].Time, staged[i].Time);
            Assert.Equal(direct[i].Get(HistoryMetrics.CpuTemp), staged[i].Get(HistoryMetrics.CpuTemp));
            Assert.Equal(direct[i].Get(HistoryMetrics.CpuLoad)!.Value, staged[i].Get(HistoryMetrics.CpuLoad)!.Value, 6);
            Assert.Equal(staged[i].Get(HistoryMetrics.CpuLoad)!.Value, again[i].Get(HistoryMetrics.CpuLoad)!.Value, 6);
        }

        Assert.True(HistoryRollup.NeedsCompaction(samples, HistoryRollup.FineBucket));
        Assert.False(HistoryRollup.NeedsCompaction(staged, HistoryRollup.CoarseBucket));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(7, null)]
    [InlineData(8, 15)]
    [InlineData(30, 15)]
    [InlineData(31, 60)]
    [InlineData(89, 60)]
    public void Storage_resolution_follows_the_age_of_the_day(int daysAgo, int? minutes)
    {
        var today = new DateOnly(2026, 10, 27);
        var bucket = HistoryRollup.StorageBucketFor(today.AddDays(-daysAgo), today);
        Assert.Equal(minutes, bucket is { } b ? (int)b.TotalMinutes : null);
    }

    [Fact]
    public void Display_buckets_keep_every_range_under_the_point_limit()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), HistoryRollup.DisplayBucketFor(HistoryRange.LastHour.Span));
        Assert.Equal(TimeSpan.FromMinutes(5), HistoryRollup.DisplayBucketFor(HistoryRange.Last24Hours.Span));
        Assert.Equal(TimeSpan.FromMinutes(30), HistoryRollup.DisplayBucketFor(HistoryRange.Last7Days.Span));
        Assert.Equal(TimeSpan.FromHours(2), HistoryRollup.DisplayBucketFor(HistoryRange.Last30Days.Span));
        Assert.Equal(TimeSpan.FromHours(6), HistoryRollup.DisplayBucketFor(HistoryRange.Last90Days.Span));
        Assert.All(HistoryRange.All, r => Assert.True(r.Span / HistoryRollup.DisplayBucketFor(r.Span) <= HistoryRollup.MaxChartPoints));
    }
}

public class HistoryRetentionTests
{
    [Fact]
    public void Expired_days_are_those_before_the_retention_window_and_never_today()
    {
        var today = new DateOnly(2026, 10, 27);
        var days = Enumerable.Range(0, 120).Select(i => today.AddDays(-i)).ToList();

        var expired = HistoryRetention.Expired(days, today, 90);

        Assert.Equal(30, expired.Count);
        Assert.Equal(today.AddDays(-119), expired[0]);
        Assert.Equal(today.AddDays(-90), expired[^1]);
        Assert.DoesNotContain(today.AddDays(-89), expired);
        // A nonsense setting is clamped to the minimum of a week instead of deleting everything.
        Assert.DoesNotContain(today, HistoryRetention.Expired(days, today, 0));
        Assert.Equal(113, HistoryRetention.Expired(days, today, 0).Count);
    }

    [Fact]
    public void Day_file_names_round_trip_and_other_files_are_ignored()
    {
        var day = new DateOnly(2026, 2, 3);
        Assert.Equal("2026-02-03.jsonl", HistoryRetention.FileName(day));
        Assert.True(HistoryRetention.TryParseFileName("2026-02-03.jsonl", out var parsed));
        Assert.Equal(day, parsed);
        Assert.False(HistoryRetention.TryParseFileName("2026-13-01.jsonl", out _));
        Assert.False(HistoryRetention.TryParseFileName("2026-02-03.jsonl.tmp", out _));
        Assert.False(HistoryRetention.TryParseFileName("notes.txt", out _));
        Assert.Equal(3, HistoryRetention.DaysBetween(new DateTimeOffset(2026, 2, 3, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 2, 5, 1, 0, 0, TimeSpan.Zero)).Count);
    }

    [Fact]
    public void Settings_values_are_clamped_and_old_settings_files_get_defaults()
    {
        Assert.Equal(30, HistorySettings.ClampInterval(5));
        Assert.Equal(900, HistorySettings.ClampInterval(10_000));
        Assert.Equal(60, HistorySettings.ClampInterval(double.NaN));
        Assert.Equal(7, HistorySettings.ClampRetention(1));
        Assert.Equal(365, HistorySettings.ClampRetention(1000));

        var defaults = AppSettings.CreateDefault().History;
        Assert.True(defaults.Enabled);
        Assert.Equal(60, defaults.SampleIntervalSeconds);
        Assert.Equal(90, defaults.RetentionDays);
    }
}

public class HistoryMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    private static List<HistoryPoint> Points(params (int Minute, double Value)[] values) =>
        values.Select(v => new HistoryPoint(T0.AddMinutes(v.Minute), v.Value)).ToList();

    [Fact]
    public void Summarize_reports_extremes_with_their_times_average_and_latest()
    {
        var stats = HistoryMath.Summarize(Points((0, 50), (1, 70), (2, double.NaN), (3, 40), (4, 60)));

        Assert.NotNull(stats);
        Assert.Equal(40, stats!.Min);
        Assert.Equal(T0.AddMinutes(3), stats.MinTime);
        Assert.Equal(70, stats.Max);
        Assert.Equal(T0.AddMinutes(1), stats.MaxTime);
        Assert.Equal(55, stats.Average);
        Assert.Equal(60, stats.Last);
        Assert.Equal(4, stats.Count);
        Assert.Null(HistoryMath.Summarize([]));
    }

    [Fact]
    public void Nearest_finds_the_closest_point_for_the_hover_value()
    {
        var points = Points((0, 1), (10, 2), (20, 3));
        Assert.Equal(1, HistoryMath.Nearest(points, T0.AddMinutes(-30))!.Value.Value);
        Assert.Equal(2, HistoryMath.Nearest(points, T0.AddMinutes(9))!.Value.Value);
        Assert.Equal(2, HistoryMath.Nearest(points, T0.AddMinutes(14))!.Value.Value);
        Assert.Equal(3, HistoryMath.Nearest(points, T0.AddMinutes(16))!.Value.Value);
        Assert.Equal(3, HistoryMath.Nearest(points, T0.AddHours(5))!.Value.Value);
        Assert.Null(HistoryMath.Nearest([], T0));
    }

    [Fact]
    public void Series_breaks_where_the_pc_was_off_and_energy_uses_the_covered_seconds()
    {
        var segments = HistoryMath.SplitAtGaps(Points((0, 1), (1, 1), (2, 1), (60, 1), (61, 1), (300, 1)), TimeSpan.FromMinutes(5));
        Assert.Equal([3, 2, 1], segments.Select(s => s.Count));

        var samples = Enumerable.Range(0, 60)
            .Select(i => new HistorySample(T0.AddMinutes(i), 60, new Dictionary<string, double> { [HistoryMetrics.Power] = 300 }))
            .Append(new HistorySample(T0, 0, new Dictionary<string, double> { [HistoryMetrics.Power] = 5000 }))
            .ToList();
        Assert.Equal(0.3, HistoryMath.EnergyKwh(samples), 6);
        Assert.Equal(0.3, HistoryMath.EnergyKwh(HistoryRollup.Compact(samples, HistoryRollup.FineBucket)), 6);
    }

    [Fact]
    public void Axis_range_is_fixed_for_percentages_and_padded_otherwise()
    {
        Assert.Equal((0d, 100d), HistoryMath.AxisRange(HistoryMetrics.CpuLoad, null));
        Assert.Equal((0d, 100d), HistoryMath.AxisRange(HistoryMetrics.Health, null));
        var stats = HistoryMath.Summarize(Points((0, 40), (1, 80)))!;
        var (min, max) = HistoryMath.AxisRange(HistoryMetrics.CpuTemp, stats);
        Assert.True(min < 40 && min >= 0);
        Assert.True(max > 80);
    }

    [Fact]
    public void Accumulator_keeps_peak_temperatures_and_averages_loads()
    {
        var acc = new HistorySampleAccumulator();
        Assert.Null(acc.Flush(T0, 60));

        acc.Add(HistoryMetrics.CpuTemp, 60);
        acc.Add(HistoryMetrics.CpuTemp, 88);
        acc.Add(HistoryMetrics.CpuTemp, 61);
        acc.Add(HistoryMetrics.CpuLoad, 10);
        acc.Add(HistoryMetrics.CpuLoad, 30);
        acc.Add(HistoryMetrics.GpuLoad, null);
        acc.Add(HistoryMetrics.GpuLoad, double.NaN);
        acc.Add("disk.free:C:", 500);
        acc.Add("disk.free:C:", 499);

        var sample = acc.Flush(T0, 60);
        Assert.NotNull(sample);
        Assert.Equal(88, sample!.Get(HistoryMetrics.CpuTemp));
        Assert.Equal(20, sample.Get(HistoryMetrics.CpuLoad));
        Assert.Equal(499, sample.Get("disk.free:C:"));
        Assert.Null(sample.Get(HistoryMetrics.GpuLoad));
        Assert.True(acc.IsEmpty);
        Assert.Null(acc.Flush(T0.AddMinutes(1), 60));
    }
}

public class HistoryEventTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly HistoryThresholds Limits = new(CpuWarnC: 85, CpuCritC: 95, GpuWarnC: 83, GpuCritC: 92);

    private static HistorySample At(int minute, string metric, double value) =>
        new(T0.AddMinutes(minute), 60, new Dictionary<string, double> { [metric] = value });

    [Fact]
    public void One_event_per_hot_spell_at_its_peak()
    {
        double[] temps = [70, 86, 90, 88, 70, 72, 84, 97, 85, 60];
        var samples = temps.Select((t, i) => At(i, HistoryMetrics.CpuTemp, t)).ToList();

        var events = HistoryEventDetector.Detect(samples, Limits);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(HistoryEventKind.TemperaturePeak, e.Kind));
        Assert.Equal(T0.AddMinutes(7), events[0].Time); // newest first
        Assert.Equal(Severity.Critical, events[0].Severity);
        Assert.Contains("97 °C", events[0].Title);
        Assert.Equal(T0.AddMinutes(2), events[1].Time);
        Assert.Equal(Severity.High, events[1].Severity);
        Assert.Contains("90 °C", events[1].Title);
    }

    [Fact]
    public void Readings_far_apart_start_a_new_spell_and_cool_series_have_none()
    {
        var samples = new[] { At(0, HistoryMetrics.GpuTemp, 85), At(60, HistoryMetrics.GpuTemp, 86) };
        Assert.Equal(2, HistoryEventDetector.Detect(samples, Limits).Count);
        Assert.Empty(HistoryEventDetector.Detect(Enumerable.Range(0, 50).Select(i => At(i, HistoryMetrics.GpuTemp, 82.9)).ToList(), Limits));
    }

    [Fact]
    public void Sudden_free_space_drops_are_found_but_slow_use_and_long_gaps_are_not()
    {
        const string c = "disk.free:C:";
        var samples = new List<HistorySample>
        {
            At(0, c, 600), At(60, c, 598), At(120, c, 597), // slow use
            At(180, c, 520),                               // 77 GB gone in an hour
            At(180 + 48 * 60, c, 400),                     // two days later: not "sudden"
        };

        var events = HistoryEventDetector.Detect(samples, Limits);

        var drop = Assert.Single(events);
        Assert.Equal(HistoryEventKind.FreeSpaceDrop, drop.Kind);
        Assert.Equal(T0.AddMinutes(180), drop.Time);
        Assert.Contains("C:", drop.Title);
        Assert.Contains("77 GB", drop.Title);

        // On a big, empty drive 50 GB is under the 5 % relative minimum.
        Assert.Empty(HistoryEventDetector.Detect([At(0, "disk.free:D:", 2400), At(60, "disk.free:D:", 2350)], Limits));
    }

    [Fact]
    public void Health_score_drops_of_five_or_more_points_are_events()
    {
        var samples = new[]
        {
            At(0, HistoryMetrics.Health, 94), At(1000, HistoryMetrics.Health, 92), At(2000, HistoryMetrics.Health, 84),
            At(3000, HistoryMetrics.Health, 90), At(4000, HistoryMetrics.Health, 55),
        };

        var events = HistoryEventDetector.Detect(samples, Limits);

        Assert.Equal(2, events.Count);
        Assert.Equal(Severity.High, events[0].Severity); // 90 → 55
        Assert.Equal(Severity.Medium, events[1].Severity); // 92 → 84
        Assert.Contains("from 92 to 84", events[1].Title);
    }
}

public sealed class HistoryFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", "history-" + Guid.NewGuid().ToString("N"));

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

    private static HistorySample Sample(DateTimeOffset time, double temp) =>
        new(time, 60, new Dictionary<string, double> { [HistoryMetrics.CpuTemp] = temp, [HistoryMetrics.CpuLoad] = temp / 2 });

    [Fact]
    public void A_line_cut_short_by_a_crash_is_skipped_and_the_next_append_starts_cleanly()
    {
        var store = new HistoryFileStore(_dir);
        var t = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        store.Append(Sample(t, 50));
        File.AppendAllText(store.PathFor(HistoryRetention.DayOf(t)), "{\"t\":1761555660,\"s\":60,\"v\":{\"cpu.te");
        store.Append(Sample(t.AddMinutes(2), 52));

        var data = store.Read(t.AddHours(-1), t.AddHours(1));

        Assert.Equal(2, data.Samples.Count);
        Assert.Equal(1, data.CorruptLines);
        Assert.Equal([50d, 52d], data.Series(HistoryMetrics.CpuTemp).Select(p => p.Value));
        Assert.Single(store.Read(t.AddMinutes(1), t.AddMinutes(3)).Samples);
    }

    [Fact]
    public void Maintenance_prunes_expired_days_rolls_up_old_ones_and_keeps_recent_ones()
    {
        var store = new HistoryFileStore(_dir);
        var today = new DateOnly(2026, 10, 27);
        foreach (int daysAgo in new[] { 0, 3, 10, 40, 120 })
        {
            var start = new DateTimeOffset(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            for (int m = 0; m < 120; m++)
            {
                store.Append(Sample(start.AddMinutes(m), m == 33 ? 95 : 60));
            }
        }

        File.WriteAllText(Path.Combine(_dir, "2026-10-01.jsonl.tmp"), "leftover");
        File.AppendAllText(store.PathFor(today.AddDays(-10)), "garbage\n");

        var result = store.Maintain(today, retentionDays: 90);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(2, result.CompactedFiles);
        Assert.Equal(1, result.DroppedLines);
        Assert.Equal([today.AddDays(-40), today.AddDays(-10), today.AddDays(-3), today], store.Days());
        Assert.False(File.Exists(Path.Combine(_dir, "2026-10-01.jsonl.tmp")));

        var all = store.Read(new DateTimeOffset(today.AddDays(-60).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        Assert.Equal(0, all.CorruptLines);
        Assert.Equal(2, all.Samples.Count(s => HistoryRetention.DayOf(s.Time) == today.AddDays(-40))); // hourly
        Assert.Equal(8, all.Samples.Count(s => HistoryRetention.DayOf(s.Time) == today.AddDays(-10))); // 15-minute
        Assert.Equal(120, all.Samples.Count(s => HistoryRetention.DayOf(s.Time) == today.AddDays(-3))); // as recorded
        Assert.Equal(95, all.Samples.Where(s => HistoryRetention.DayOf(s.Time) == today.AddDays(-40)).Max(s => s.Get(HistoryMetrics.CpuTemp)));

        var second = store.Maintain(today, retentionDays: 90);
        Assert.Equal(0, second.CompactedFiles + second.DeletedFiles);
        Assert.True(store.GetInfo().Bytes > 0);
        Assert.Equal(today.AddDays(-40), store.GetInfo().OldestDay);
    }
}

public class HistoryDemoAndExportTests
{
    [Fact]
    public void Demo_history_is_deterministic_covers_a_month_and_has_every_kind_of_event()
    {
        var now = new DateTimeOffset(2026, 10, 27, 21, 30, 0, TimeSpan.Zero);
        var a = DemoHistoryGenerator.Generate(now, zone: TimeZoneInfo.Utc);
        var b = DemoHistoryGenerator.Generate(now, zone: TimeZoneInfo.Utc);

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(HistoryLineCodec.Format(a[^1]), HistoryLineCodec.Format(b[^1]));
        Assert.Equal(HistoryLineCodec.Format(a[a.Count / 2]), HistoryLineCodec.Format(b[b.Count / 2]));
        Assert.True(a[0].Time >= now.AddDays(-30) && a[^1].Time <= now);
        Assert.True(now - a[0].Time > TimeSpan.FromDays(29));

        // Recent days at full resolution, older days rolled up like the real store.
        Assert.Contains(a, s => s.SpanSeconds == 60 && s.Time > now.AddDays(-2));
        Assert.All(a.Where(s => s.Time < now.AddDays(-9)), s => Assert.True(s.SpanSeconds >= 900));

        var data = new HistoryData(now.AddDays(-30), now, a);
        Assert.Contains(HistoryMetrics.Health, data.Metrics);
        Assert.Contains("disk.free:D:", data.Metrics);
        var events = HistoryEventDetector.Detect(a, HistoryThresholds.From(new MonitoringSettings()));
        Assert.Contains(events, e => e.Kind == HistoryEventKind.TemperaturePeak && e.Severity == Severity.Critical);
        Assert.Contains(events, e => e.Kind == HistoryEventKind.TemperaturePeak && e.Metric == HistoryMetrics.GpuTemp && e.Time > now.AddDays(-1));
        Assert.Contains(events, e => e.Kind == HistoryEventKind.FreeSpaceDrop && e.Metric == "disk.free:C:");
        Assert.Contains(events, e => e.Kind == HistoryEventKind.HealthDrop);
        Assert.InRange(HistoryMath.Summarize(data.Series(HistoryMetrics.Power))!.Average, 100, 400);
    }

    [Fact]
    public void Csv_has_units_in_headers_invariant_numbers_and_blank_missing_values()
    {
        var t = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new HistorySample(t, 60, new Dictionary<string, double> { [HistoryMetrics.CpuTemp] = 61.25, [HistoryMetrics.Power] = 143 }),
            new HistorySample(t.AddMinutes(1), 0, new Dictionary<string, double> { [HistoryMetrics.Health] = 92, ["disk.free:C:"] = 1234.5 }),
        };

        var lines = HistoryCsvExporter.Build(samples, TimeZoneInfo.Utc).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal("Time (UTC),Local time,Seconds covered,CPU temperature (°C),Power draw (W),Health score,Free space on C: (GB)", lines[0]);
        Assert.Equal("2026-10-27 09:00:00,2026-10-27 09:00:00,60,61.25,143,,", lines[1]);
        Assert.Equal("2026-10-27 09:01:00,2026-10-27 09:01:00,0,,,92,1234.5", lines[2]);
    }
}
