using System.Text;
using PcChecker.Core.AppHistory;
using PcChecker.Core.History;
using Xunit;

namespace PcChecker.Core.Tests;

public class AppHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // ---- SRUM id map ----

    private static byte[] Sid(byte authority, params uint[] subs)
    {
        var bytes = new List<byte> { 1, (byte)subs.Length, 0, 0, 0, 0, 0, authority };
        foreach (var sub in subs)
        {
            bytes.AddRange(BitConverter.GetBytes(sub));
        }

        return bytes.ToArray();
    }

    [Fact]
    public void Binary_sids_decode_to_their_text_form()
    {
        Assert.True(SrumIdMapDecoder.TryDecodeSid(Sid(5, 21, 1004336348, 1177238915, 682003330, 1001), out var user));
        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-1001", user);
        Assert.True(SrumIdMapDecoder.TryDecodeSid(Sid(5, 18), out var system));
        Assert.Equal("S-1-5-18", system);

        // Wrong revision, a count that does not match the length, too short.
        var wrongRevision = Sid(5, 18);
        wrongRevision[0] = 2;
        Assert.False(SrumIdMapDecoder.TryDecodeSid(wrongRevision, out _));
        Assert.False(SrumIdMapDecoder.TryDecodeSid(Sid(5, 21, 1).Take(10).ToArray(), out _));
        Assert.False(SrumIdMapDecoder.TryDecodeSid([1, 0, 0], out _));
    }

    [Fact]
    public void Id_map_blobs_decode_by_type_with_utf16_paths_and_binary_sids()
    {
        var path = Encoding.Unicode.GetBytes(@"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe" + "\0");
        var program = SrumIdMapDecoder.Decode(7, 0, path)!;
        Assert.Equal(SrumIdKind.Program, program.Kind);
        Assert.Equal(@"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe", program.Value);
        Assert.Equal(7, program.Index);

        var user = SrumIdMapDecoder.Decode(8, 3, Sid(5, 21, 1, 2, 3, 1001))!;
        Assert.Equal(SrumIdKind.User, user.Kind);
        Assert.Equal("S-1-5-21-1-2-3-1001", user.Value);

        // Some Windows versions store the SID under type 1.
        Assert.Equal(SrumIdKind.User, SrumIdMapDecoder.Decode(9, 1, Sid(5, 19))!.Kind);

        var package = SrumIdMapDecoder.Decode(10, 2, Encoding.Unicode.GetBytes("SpotifyAB.SpotifyMusic_zpdnekdrzrea0"))!;
        Assert.Equal(SrumIdKind.PackageOrService, package.Kind);

        // Type 3 that is not a SID is kept as text instead of being lost.
        var odd = SrumIdMapDecoder.Decode(11, 3, Encoding.Unicode.GetBytes("Network Service"))!;
        Assert.Equal("Network Service", odd.Value);

        Assert.Null(SrumIdMapDecoder.Decode(12, 0, []));
        Assert.Null(SrumIdMapDecoder.Decode(13, 0, null));
        Assert.Null(SrumIdMapDecoder.Decode(14, 0, Encoding.Unicode.GetBytes("\0")));
    }

    // ---- Conversions ----

    [Fact]
    public void Raw_integers_decode_by_length()
    {
        Assert.Equal(200, SrumConvert.ReadInteger([200]));
        Assert.Equal(-2, SrumConvert.ReadInteger(BitConverter.GetBytes((short)-2)));
        Assert.Equal(123456, SrumConvert.ReadInteger(BitConverter.GetBytes(123456)));
        Assert.Equal(9_876_543_210L, SrumConvert.ReadInteger(BitConverter.GetBytes(9_876_543_210L)));
        Assert.Null(SrumConvert.ReadInteger([1, 2, 3]));
        Assert.Null(SrumConvert.ReadInteger(null));
    }

    [Fact]
    public void Ole_dates_become_utc_moments()
    {
        Assert.Equal(new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero), SrumConvert.FromOleDate(2.0));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), SrumConvert.FromOleDate(new DateTime(2026, 10, 20, 12, 0, 0).ToOADate()));
        Assert.Equal(TimeSpan.Zero, SrumConvert.FromOleDate(46000.25)!.Value.Offset);
        Assert.Null(SrumConvert.FromOleDate(0));
        Assert.Null(SrumConvert.FromOleDate(double.NaN));
        Assert.Null(SrumConvert.FromOleDate(3_000_000));

        var bytes = BitConverter.GetBytes(new DateTime(2026, 10, 1, 8, 30, 0).ToOADate());
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), SrumConvert.ReadOleDate(bytes));
        Assert.Null(SrumConvert.ReadOleDate([1, 2, 3, 4]));
    }

    [Fact]
    public void Cycles_become_processor_time_at_the_base_speed()
    {
        // 3.4 GHz: 3.4 billion cycles is one second of processor time.
        Assert.Equal(TimeSpan.FromSeconds(60), SrumConvert.CyclesToCpuTime(3_400_000_000L * 60, 3400));
        Assert.Equal(TimeSpan.FromHours(1), SrumConvert.CyclesToCpuTime(3_000_000_000L * 3600, double.NaN));
        Assert.Equal(TimeSpan.Zero, SrumConvert.CyclesToCpuTime(0, 3400));
        Assert.Equal(TimeSpan.Zero, SrumConvert.CyclesToCpuTime(-5, 3400));

        Assert.Equal(3400, SrumConvert.EffectiveMhz(3400));
        Assert.Equal(SrumConvert.FallbackMhz, SrumConvert.EffectiveMhz(null));
        Assert.Equal(SrumConvert.FallbackMhz, SrumConvert.EffectiveMhz(0));
        Assert.Equal(SrumConvert.FallbackMhz, SrumConvert.EffectiveMhz(1_000_000));
    }

    [Fact]
    public void Mobile_broadband_interfaces_count_as_metered()
    {
        long Luid(int type, int index) => ((long)type << 48) | ((long)index << 24);
        Assert.Equal(71, SrumConvert.InterfaceType(Luid(71, 3)));
        Assert.False(SrumConvert.IsMeteredInterface(Luid(71, 3)));
        Assert.False(SrumConvert.IsMeteredInterface(Luid(6, 1)));
        Assert.True(SrumConvert.IsMeteredInterface(Luid(243, 2)));
        Assert.True(SrumConvert.IsMeteredInterface(Luid(244, 2)));
    }

    // ---- Names ----

    private static readonly Dictionary<string, string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\Device\HarddiskVolume3"] = "C:",
        [@"\Device\HarddiskVolume7"] = "D:",
    };

    [Fact]
    public void Device_paths_become_drive_letter_paths_and_readable_names()
    {
        Assert.Equal(@"C:\program files\steam\steam.exe", AppNames.ToDosPath(@"\device\harddiskvolume3\program files\steam\steam.exe", Devices));
        Assert.Equal(@"D:\games\cs2.exe", AppNames.ToDosPath(@"\Device\HarddiskVolume7\games\cs2.exe", Devices));
        Assert.Null(AppNames.ToDosPath(@"\device\harddiskvolume30\x.exe", Devices));
        Assert.Equal(@"E:\x.exe", AppNames.ToDosPath(@"E:\x.exe", Devices));

        var steam = AppNames.Describe(new SrumIdEntry(1, SrumIdKind.Program, @"\device\harddiskvolume3\Program Files\Steam\steam.exe"), Devices);
        Assert.Equal(@"c:\program files\steam\steam.exe", steam.AppKey);
        Assert.Equal("Steam", steam.Name);
        Assert.Equal(@"C:\Program Files\Steam\steam.exe", steam.Path);

        Assert.Equal("Windows Update", AppNames.Describe(new SrumIdEntry(2, SrumIdKind.PackageOrService, "wuauserv"), Devices).Name);
        Assert.Equal("Spotify Music", AppNames.FriendlyName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0"));
        Assert.Equal("Windows Store", AppNames.FriendlyName("Microsoft.WindowsStore_8wekyb3d8bbwe"));
        Assert.Equal("Xbox Game UI", AppNames.SplitWords("XboxGameUI"));
        Assert.Equal("Chrome", AppNames.Describe(new SrumIdEntry(3, SrumIdKind.Unknown, "!!chrome.exe!2026/01/01:10:00:00!0!"), Devices).Name);
    }

    // ---- Turning SRUM rows into records ----

    [Fact]
    public void Srum_rows_become_records_with_cpu_time_disk_network_and_users()
    {
        var ids = new List<SrumIdEntry>
        {
            new(1, SrumIdKind.Program, @"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe"),
            new(2, SrumIdKind.User, "S-1-5-21-1-2-3-1001"),
        };
        var resources = new List<SrumResourceRow>
        {
            new(Noon, 1, 2, 2_000_000_000, 1_000_000_000, 100, 200, 10, 20),
            new(Noon, 99, null, 3_000_000_000, 0, 0, 0, 0, 0),
        };
        long wifi = 71L << 48, cellular = 243L << 48;
        var network = new List<SrumNetworkRow>
        {
            new(Noon, 1, 2, wifi, 1000, 9000),
            new(Noon, 1, 2, cellular, 50, 450),
        };

        var (records, apps) = SrumRecordBuilder.Build(new SrumRawData(ids, resources, network), 3000, Devices);
        var chromeKey = @"c:\program files\google\chrome\application\chrome.exe";
        Assert.Equal("Chrome", apps[chromeKey].Name);
        Assert.Equal(@"C:\program files\google\chrome\application\chrome.exe", apps[chromeKey].Path);
        Assert.Contains(apps.Values, a => a.Name == "Unknown app (99)");

        var cpu = records.Single(r => r.AppKey == chromeKey && r.CpuSeconds > 0);
        Assert.Equal(1.0, cpu.CpuSeconds, 6);
        Assert.Equal(110, cpu.DiskReadBytes);
        Assert.Equal(220, cpu.DiskWriteBytes);
        Assert.Equal("S-1-5-21-1-2-3-1001", cpu.UserSid);

        var range = new AppHistoryRange(Noon.AddHours(-1), Noon.AddHours(1));
        var chrome = AppUsageAggregator.Summarize(records, apps, range).Single(s => s.AppKey == chromeKey);
        Assert.Equal(10_500, chrome.NetworkBytes);
        Assert.Equal(500, chrome.MeteredBytes);
        Assert.Equal(TimeSpan.FromSeconds(1), chrome.CpuTime);
    }

    // ---- Aggregation ----

    private static AppUsageRecord Rec(string key, DateTimeOffset at, double cpu, long? net = null, long? read = null) =>
        new(key, at, at, cpu, net, net is null ? null : 0, net is null ? null : 0, read, read);

    [Fact]
    public void Summaries_add_up_each_app_inside_the_range_only()
    {
        var records = new[]
        {
            Rec(@"c:\a\game.exe", Noon.AddHours(-2), 600, 1000, 50),
            Rec(@"C:\A\GAME.EXE", Noon.AddHours(-1), 300, 500, 50),
            Rec(@"c:\a\game.exe", Noon.AddDays(-3), 9999, 9999, 9999),
            Rec(@"c:\b\chat.exe", Noon.AddHours(-1), 30, 50_000),
            Rec(@"c:\c\idle.exe", Noon.AddHours(-1), 0),
        };
        var apps = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            [@"c:\a\game.exe"] = new(@"c:\a\game.exe", "Great Game", @"C:\A\game.exe", "Studio"),
        };

        var rows = AppUsageAggregator.Summarize(records, apps, new AppHistoryRange(Noon.AddDays(-1), Noon));
        Assert.Equal(2, rows.Count);
        var game = rows[0];
        Assert.Equal("Great Game", game.Name);
        Assert.Equal(TimeSpan.FromSeconds(900), game.CpuTime);
        Assert.Equal(1500, game.NetworkBytes);
        Assert.Equal(100, game.DiskReadBytes);
        Assert.Equal(Noon.AddHours(-2), game.FirstSeen);
        Assert.Equal(Noon.AddHours(-1), game.LastSeen);
        Assert.Equal("Chat", rows[1].Name);
        Assert.Null(rows[1].DiskReadBytes);

        var totals = AppUsageAggregator.Totals(rows);
        Assert.Equal("Great Game", totals.TopCpu!.Name);
        Assert.Equal("Chat", totals.TopNetwork!.Name);
        Assert.Equal(51_500, totals.TotalNetworkBytes);
        Assert.Equal(2, totals.AppCount);

        Assert.Single(AppUsageAggregator.Filter(rows, "great"));
        Assert.Single(AppUsageAggregator.Filter(rows, @"c:\b"));
        Assert.Equal(2, AppUsageAggregator.Filter(rows, "  ").Count());
    }

    [Fact]
    public void Own_recording_has_no_network_so_the_totals_say_so()
    {
        var rows = AppUsageAggregator.Summarize([Rec("x.exe", Noon, 5, null, 10)], new Dictionary<string, AppIdentity>(), new AppHistoryRange(Noon.AddHours(-1), Noon));
        var totals = AppUsageAggregator.Totals(rows);
        Assert.Null(totals.TotalNetworkBytes);
        Assert.Null(totals.TopNetwork);
        Assert.Equal("—", AppHistoryText.Bytes(rows[0].NetworkBytes));
    }

    [Fact]
    public void Ranges_start_at_local_midnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus10", TimeSpan.FromHours(10), "Plus10", "Plus10");
        var now = new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.Zero); // 06:00 on 21 October there

        var today = AppUsageAggregator.Resolve(AppHistoryRangeKind.Today, null, now, zone);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(10)), today.From);
        Assert.Equal(now, today.To);

        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.FromHours(10)), AppUsageAggregator.Resolve(AppHistoryRangeKind.Last7Days, null, now, zone).From);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.FromHours(10)), AppUsageAggregator.Resolve(AppHistoryRangeKind.Last30Days, null, now, zone).From);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(10)), AppUsageAggregator.Resolve(AppHistoryRangeKind.SinceDate, new DateOnly(2026, 10, 1), now, zone).From);

        // A date in the future falls back to today.
        Assert.Equal(today.From, AppUsageAggregator.Resolve(AppHistoryRangeKind.SinceDate, new DateOnly(2027, 1, 1), now, zone).From);
    }

    // ---- Own recording ----

    private static AppProcessReading P(int pid, DateTimeOffset started, string app, double cpuSeconds, long read = 0, long write = 0) =>
        new(pid, started, app, TimeSpan.FromSeconds(cpuSeconds), read, write);

    [Fact]
    public void Accumulator_counts_differences_and_only_what_it_can_place_in_time()
    {
        var t0 = Noon;
        var acc = new AppUsageAccumulator();
        var oldStart = t0.AddHours(-3);

        // First reading: only starting points.
        acc.Add([P(10, oldStart, "game.exe", 500, 1000, 100)], t0, 8);
        Assert.Equal(0, acc.PendingApps);

        // One minute later: the game used 30 s more; a new browser started 20 s ago with 4 s already used; an old
        // process that could not be read before (started hours ago) only sets its starting point.
        var t1 = t0.AddMinutes(1);
        acc.Add([
            P(10, oldStart, "game.exe", 530, 1500, 300),
            P(20, t1.AddSeconds(-20), "browser.exe", 4, 64, 32),
            P(30, oldStart, "old.exe", 1000, 10, 10),
        ], t1, 8);

        var t2 = t1.AddMinutes(1);
        acc.Add([P(10, oldStart, "game.exe", 560, 1500, 300), P(30, oldStart, "old.exe", 1002, 10, 10)], t2, 8);

        var records = acc.Flush(t2);
        var game = records.Single(r => r.AppKey == "game.exe");
        Assert.Equal(60, game.CpuSeconds, 3);
        Assert.Equal(500, game.DiskReadBytes);
        Assert.Equal(200, game.DiskWriteBytes);
        Assert.Equal(t0, game.Start);
        Assert.Equal(t2, game.End);
        Assert.Equal(4, records.Single(r => r.AppKey == "browser.exe").CpuSeconds, 3);
        Assert.Equal(2, records.Single(r => r.AppKey == "old.exe").CpuSeconds, 3);
        Assert.Null(game.BytesSent);

        // The next period starts where this one ended and is empty until something is used.
        Assert.Empty(acc.Flush(t2.AddMinutes(1)));
        Assert.Equal(t2.AddMinutes(1), acc.PeriodStart);
    }

    [Fact]
    public void Accumulator_ignores_counters_that_go_backwards_and_caps_impossible_cpu()
    {
        var acc = new AppUsageAccumulator();
        var started = Noon.AddHours(-1);
        acc.Add([P(10, started, "a.exe", 100, 1000), P(11, started, "b.exe", 0)], Noon, 2);
        acc.Add([P(10, started, "a.exe", 50, 500), P(11, started, "b.exe", 1000)], Noon.AddMinutes(1), 2);
        var records = acc.Flush(Noon.AddMinutes(1));
        Assert.DoesNotContain(records, r => r.AppKey == "a.exe");

        // Two processors for one minute allow at most about two minutes of processor time.
        Assert.InRange(records.Single(r => r.AppKey == "b.exe").CpuSeconds, 119, 123);

        acc.Reset();
        Assert.Null(acc.PeriodStart);
        acc.Add([P(10, started, "a.exe", 999)], Noon.AddMinutes(2), 2);
        Assert.Equal(0, acc.PendingApps);
    }

    [Fact]
    public void Recording_lines_round_trip_and_bad_lines_are_rejected()
    {
        var record = new AppUsageRecord(@"c:\games\a ""b"".exe", Noon, Noon.AddMinutes(10), 12.3456, null, null, null, 1024, 2048);
        var line = AppUsageLineCodec.Format(record);
        Assert.True(AppUsageLineCodec.TryParse(line, out var back));
        Assert.Equal(record.AppKey, back.AppKey);
        Assert.Equal(Noon, back.Start);
        Assert.Equal(Noon.AddMinutes(10), back.End);
        Assert.Equal(12.346, back.CpuSeconds, 3);
        Assert.Equal(1024, back.DiskReadBytes);
        Assert.Equal(2048, back.DiskWriteBytes);

        Assert.False(AppUsageLineCodec.TryParse("{\"k\":\"x\",\"s\":10,\"e\":5,\"c\":1}", out _));
        Assert.False(AppUsageLineCodec.TryParse("{\"k\":\"x\",\"s\":1,\"e\":5,\"c\":-1}", out _));
        Assert.False(AppUsageLineCodec.TryParse("{\"k\":\"x\",\"s\":1,\"e\":5", out _));
        Assert.False(AppUsageLineCodec.TryParse("[]", out _));
        Assert.False(AppUsageLineCodec.TryParse("", out _));
    }

    [Fact]
    public void Store_keeps_retention_rolls_old_days_up_to_hours_and_deletes_everything_on_request()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcc-apphistory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppUsageFileStore(dir);
            var today = new DateOnly(2026, 10, 20);
            var yesterday = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
            var old = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

            var periods = Enumerable.Range(0, 6).Select(i => new AppUsageRecord("a.exe", yesterday.AddMinutes(10 * i), yesterday.AddMinutes(10 * (i + 1) - 1), 5, null, null, null, 100, 10)).ToList();
            periods.Add(new AppUsageRecord("b.exe", yesterday, yesterday.AddMinutes(9), 1, null, null, null, null, null));
            store.Append(periods);
            store.Append([new AppUsageRecord("a.exe", old, old.AddMinutes(9), 50, null, null, null, 1, 1)]);
            File.AppendAllText(Path.Combine(dir, HistoryRetention.FileName(new DateOnly(2026, 10, 19))), "not json\n");
            Assert.Equal(new DateOnly(2026, 9, 1), store.OldestDay());

            var (deleted, compacted) = store.Maintain(today, 30);
            Assert.Equal(1, deleted);
            Assert.Equal(1, compacted);

            var lines = File.ReadAllLines(Path.Combine(dir, HistoryRetention.FileName(new DateOnly(2026, 10, 19))));
            Assert.Equal(2, lines.Length);

            var records = store.Read(yesterday.AddHours(-1), yesterday.AddHours(2));
            var a = records.Single(r => r.AppKey == "a.exe");
            Assert.Equal(30, a.CpuSeconds, 3);
            Assert.Equal(600, a.DiskReadBytes);
            Assert.Equal(yesterday, a.Start);
            Assert.Empty(store.Read(old.AddHours(-1), old.AddHours(1)));

            Assert.Equal(1, store.DeleteAll());
            Assert.Empty(store.Read(DateTimeOffset.MinValue.AddDays(2), DateTimeOffset.MaxValue.AddDays(-2)));
            Assert.Null(store.OldestDay());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- Failures ----

    [Theory]
    [InlineData(0, SrumFailureKind.None)]
    [InlineData(-1032, SrumFailureKind.Locked)]
    [InlineData(32, SrumFailureKind.Locked)]
    [InlineData(unchecked((int)0x80070020), SrumFailureKind.Locked)]
    [InlineData(-1811, SrumFailureKind.Missing)]
    [InlineData(2, SrumFailureKind.Missing)]
    [InlineData(unchecked((int)0x80070003), SrumFailureKind.Missing)]
    [InlineData(5, SrumFailureKind.AccessDenied)]
    [InlineData(-1809, SrumFailureKind.AccessDenied)]
    [InlineData(unchecked((int)0x80070005), SrumFailureKind.AccessDenied)]
    [InlineData(740, SrumFailureKind.NotAdministrator)]
    [InlineData(-550, SrumFailureKind.Corrupt)]
    [InlineData(-1206, SrumFailureKind.Corrupt)]
    [InlineData(-1018, SrumFailureKind.Corrupt)]
    [InlineData(unchecked((int)0x8004230F), SrumFailureKind.SnapshotFailed)]
    [InlineData(12345, SrumFailureKind.Unknown)]
    public void Failure_codes_are_classified_without_reading_text(int code, SrumFailureKind expected) =>
        Assert.Equal(expected, SrumErrorClassifier.Classify(code));

    [Fact]
    public void Failure_exceptions_are_classified_and_explained()
    {
        Assert.Equal(SrumFailureKind.AccessDenied, SrumErrorClassifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(SrumFailureKind.Missing, SrumErrorClassifier.Classify(new FileNotFoundException()));
        Assert.Equal(SrumFailureKind.Locked, SrumErrorClassifier.Classify(new IOException("busy", unchecked((int)0x80070020))));
        Assert.Equal(SrumFailureKind.Cancelled, SrumErrorClassifier.Classify(new OperationCanceledException()));
        Assert.Equal(SrumFailureKind.TimedOut, SrumErrorClassifier.Classify(new TimeoutException()));
        Assert.Equal(SrumFailureKind.Unsupported, SrumErrorClassifier.Classify(new DllNotFoundException()));
        Assert.Equal(SrumFailureKind.Corrupt, SrumErrorClassifier.Classify(new InvalidOperationException(), engineError: -1206));
        Assert.Equal(SrumFailureKind.AccessDenied, SrumErrorClassifier.Classify(new Exception("outer", new UnauthorizedAccessException())));

        foreach (var kind in Enum.GetValues<SrumFailureKind>())
        {
            var failure = SrumErrorClassifier.Describe(kind);
            Assert.Equal(kind, failure.Kind);
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        }

        Assert.True(SrumErrorClassifier.IsWorthRetrying(SrumFailureKind.Locked));
        Assert.False(SrumErrorClassifier.IsWorthRetrying(SrumFailureKind.Missing));
    }

    // ---- Text and demo data ----

    [Fact]
    public void Cpu_time_reads_like_task_manager()
    {
        Assert.Equal("0:00:00", AppHistoryText.CpuTime(TimeSpan.Zero));
        Assert.Equal("0:01:05", AppHistoryText.CpuTime(TimeSpan.FromSeconds(65)));
        Assert.Equal("27:03:10", AppHistoryText.CpuTime(new TimeSpan(1, 3, 3, 10)));
        Assert.Equal("0 MB", AppHistoryText.Bytes(0));
        Assert.Equal("—", AppHistoryText.Bytes(null));
    }

    [Fact]
    public void Demo_history_covers_thirty_days_of_the_promised_apps()
    {
        var now = new DateTimeOffset(2026, 10, 28, 21, 30, 0, TimeSpan.Zero);
        var records = DemoAppHistoryData.Generate(now, 30, withNetwork: true);
        Assert.Equal(records.Count, DemoAppHistoryData.Generate(now, 30, withNetwork: true).Count);
        Assert.True((now - records.Min(r => r.Start)).TotalDays > 28);
        Assert.All(records, r => Assert.True(DemoAppHistoryData.Identities.ContainsKey(r.AppKey), r.AppKey));

        var rows = AppUsageAggregator.Summarize(records, DemoAppHistoryData.Identities, new AppHistoryRange(now.AddDays(-30), now));
        foreach (var name in new[] { "Google Chrome", "Microsoft Edge", "Discord", "Steam", "Cyberpunk 2077", "Counter-Strike 2", "Windows Update" })
        {
            Assert.Contains(rows, r => r.Name == name);
        }

        var totals = AppUsageAggregator.Totals(rows);
        Assert.Equal("Steam", totals.TopNetwork!.Name);
        Assert.True(totals.TotalMeteredBytes > 0);

        var own = DemoAppHistoryData.Generate(now, 12, withNetwork: false);
        Assert.All(own, r => Assert.Null(r.NetworkBytes));
    }
}
