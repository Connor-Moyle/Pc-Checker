using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Reports;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class SchedulingTests
{
    private const string Exe = @"C:\Program Files\PC Checker\PcChecker.exe";

    // ---- Command line -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--scheduled", "health", ScheduledJobKind.Health)]
    [InlineData("--scheduled", "CLEANUP", ScheduledJobKind.Cleanup)]
    [InlineData("/scheduled", "drivers", ScheduledJobKind.Drivers)]
    [InlineData("-scheduled", "report", ScheduledJobKind.Report)]
    public void Scheduled_switch_selects_a_headless_job(string flag, string value, ScheduledJobKind expected)
    {
        var o = CommandLineOptions.Parse([flag, value]);
        Assert.Equal(expected, o.ScheduledJob);
        Assert.True(o.IsHeadless);
        Assert.Equal(AppMode.Normal, o.Mode);
        Assert.Empty(o.UnknownArguments);
    }

    [Fact]
    public void Scheduled_switch_accepts_inline_value_and_combines_with_demo()
    {
        var o = CommandLineOptions.Parse(["--scheduled=health", "--demo"]);
        Assert.Equal(ScheduledJobKind.Health, o.ScheduledJob);
        Assert.True(o.IsDemo);
    }

    [Fact]
    public void Scheduled_switch_with_unknown_or_missing_job_is_reported_not_run()
    {
        var unknown = CommandLineOptions.Parse(["--scheduled", "defrag"]);
        Assert.Null(unknown.ScheduledJob);
        Assert.False(unknown.IsHeadless);
        Assert.Equal(["--scheduled defrag"], unknown.UnknownArguments);

        var missing = CommandLineOptions.Parse(["--scheduled", "--tray"]);
        Assert.Null(missing.ScheduledJob);
        Assert.True(missing.StartInTray);
        Assert.Equal(["--scheduled"], missing.UnknownArguments);
    }

    [Fact]
    public void Tray_switch_starts_hidden_and_defaults_are_off()
    {
        Assert.True(CommandLineOptions.Parse(["--tray"]).StartInTray);
        var none = CommandLineOptions.Parse([]);
        Assert.False(none.StartInTray);
        Assert.Null(none.ScheduledJob);
    }

    // ---- schtasks commands --------------------------------------------------------------------------------------

    private static readonly System.Xml.Linq.XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static System.Xml.Linq.XDocument JobXml(JobSchedule schedule, string exe = Exe, string? user = @"DESKTOP-1\Alex") =>
        System.Xml.Linq.XDocument.Parse(JobTaskXml.Build(schedule, exe, user, new DateOnly(2026, 10, 27)));

    [Fact]
    public void Weekly_job_task_runs_the_job_elevated_on_the_chosen_day_and_time()
    {
        var doc = JobXml(new JobSchedule { Job = ScheduledJobKind.Health, Frequency = ScheduleFrequency.Weekly, Day = DayOfWeek.Sunday, Time = "9:05" });
        string Value(string name) => doc.Descendants(TaskNs + name).Single().Value;

        Assert.Equal("2026-10-27T09:05:00", Value("StartBoundary"));
        Assert.Equal("1", Value("WeeksInterval"));
        Assert.Equal("Sunday", doc.Descendants(TaskNs + "DaysOfWeek").Single().Elements().Single().Name.LocalName);
        Assert.Empty(doc.Descendants(TaskNs + "ScheduleByDay"));
        Assert.Equal("\"" + Exe + "\"", Value("Command"));
        Assert.Equal("--scheduled health", Value("Arguments"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal(@"DESKTOP-1\Alex", Value("UserId"));
    }

    [Fact]
    public void Job_tasks_run_on_battery_and_have_a_time_limit()
    {
        var doc = JobXml(new JobSchedule { Job = ScheduledJobKind.Report, Frequency = ScheduleFrequency.Daily, Time = "23:30" });
        string Value(string name) => doc.Descendants(TaskNs + name).Single().Value;

        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal(JobTaskXml.ExecutionTimeLimit, Value("ExecutionTimeLimit"));
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Saturday)]
    public void Weekly_days_use_task_scheduler_day_names(DayOfWeek day)
    {
        var doc = JobXml(new JobSchedule { Job = ScheduledJobKind.Drivers, Day = day, Time = "18:00" });
        Assert.Equal(day.ToString(), doc.Descendants(TaskNs + "DaysOfWeek").Single().Elements().Single().Name.LocalName);
    }

    [Fact]
    public void Daily_job_has_no_day_and_escapes_the_path()
    {
        const string odd = @"C:\R&D <Tools>\PcChecker.exe";
        var xml = JobTaskXml.Build(new JobSchedule { Job = ScheduledJobKind.Cleanup, Frequency = ScheduleFrequency.Daily, Time = "23:30" }, odd, null, new DateOnly(2026, 1, 5));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("1", doc.Descendants(TaskNs + "DaysInterval").Single().Value);
        Assert.Empty(doc.Descendants(TaskNs + "ScheduleByWeek"));
        Assert.Empty(doc.Descendants(TaskNs + "UserId"));
        Assert.Equal("2026-01-05T23:30:00", doc.Descendants(TaskNs + "StartBoundary").Single().Value);
        Assert.Equal("\"" + odd + "\"", doc.Descendants(TaskNs + "Command").Single().Value);
        Assert.Equal("--scheduled cleanup", doc.Descendants(TaskNs + "Arguments").Single().Value);
    }

    [Fact]
    public void Job_tasks_are_created_from_xml()
    {
        Assert.Equal(["/Create", "/TN", @"PC Checker\Health", "/XML", @"C:\ProgramData\x\task.xml", "/F"],
            SchtasksCommandBuilder.CreateFromXml(ScheduledJobIds.TaskName(ScheduledJobKind.Health), @"C:\ProgramData\x\task.xml").Arguments);
        Assert.Equal(
            "/Create /TN \"PC Checker\\Health\" /XML \"C:\\Program Data\\task.xml\" /F",
            SchtasksCommandBuilder.CreateFromXml(ScheduledJobIds.TaskName(ScheduledJobKind.Health), @"C:\Program Data\task.xml").CommandLine);
        Assert.Throws<ArgumentException>(() => SchtasksCommandBuilder.CreateFromXml(" ", @"C:\task.xml"));
    }

    [Fact]
    public void Logon_task_is_created_from_xml()
    {
        Assert.Equal(["/Create", "/TN", @"PC Checker\Start", "/XML", @"C:\Temp\logon.xml", "/F"], SchtasksCommandBuilder.CreateLogon(@"C:\Temp\logon.xml").Arguments);
        Assert.Throws<ArgumentException>(() => SchtasksCommandBuilder.CreateLogon(" "));
    }

    [Fact]
    public void Logon_task_xml_has_no_time_limit_runs_on_battery_and_uses_highest_privileges()
    {
        var doc = System.Xml.Linq.XDocument.Parse(LogonTaskXml.Build(Exe, startInTray: true, @"DESKTOP-1\Alex"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => doc.Descendants(ns + name).Single().Value;

        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("PT30S", Value("Delay"));
        Assert.Equal("\"" + Exe + "\"", Value("Command"));
        Assert.Equal("--tray", Value("Arguments"));
        Assert.Equal(2, doc.Descendants(ns + "UserId").Count(e => e.Value == @"DESKTOP-1\Alex"));
        Assert.Single(doc.Descendants(ns + "LogonTrigger"));
    }

    [Fact]
    public void Logon_task_xml_without_tray_or_user_omits_them_and_escapes_the_path()
    {
        const string odd = @"C:\R&D <Tools>\PcChecker.exe";
        var xml = LogonTaskXml.Build(odd, startInTray: false, userId: null);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Empty(doc.Descendants(ns + "Arguments"));
        Assert.Empty(doc.Descendants(ns + "UserId"));
        Assert.Equal("\"" + odd + "\"", doc.Descendants(ns + "Command").Single().Value);
        Assert.Throws<ArgumentException>(() => LogonTaskXml.Build("PcChecker.exe", true, null));
    }

    [Fact]
    public void Query_and_delete_use_csv_verbose_without_header_and_force()
    {
        Assert.Equal(["/Query", "/TN", @"PC Checker\Report", "/FO", "CSV", "/V", "/NH"], SchtasksCommandBuilder.Query(ScheduledJobIds.TaskName(ScheduledJobKind.Report)).Arguments);
        Assert.Equal(["/Delete", "/TN", @"PC Checker\Start", "/F"], SchtasksCommandBuilder.Delete(SchtasksCommandBuilder.LogonTaskName).Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PcChecker.exe")]
    [InlineData(@"tools\PcChecker.exe")]
    [InlineData("C:\\Odd\"Name\\PcChecker.exe")]
    public void Invalid_executable_paths_are_rejected(string? path)
    {
        Assert.NotNull(SchtasksCommandBuilder.ValidateExecutable(path));
        Assert.Throws<ArgumentException>(() => JobTaskXml.Build(new JobSchedule(), path!, null, new DateOnly(2026, 10, 27)));
    }

    [Theory]
    [InlineData(@"PC\Admin", @"PC\Kid", true)]
    [InlineData(@"PC\Kid", @"PC\Kid", false)]
    [InlineData(@"PC\Kid", "kid", false)]
    [InlineData(@"PC\Admin", null, false)]
    [InlineData(null, @"PC\Kid", false)]
    public void Tasks_are_refused_when_the_app_runs_as_another_account(string? process, string? session, bool refused)
    {
        var problem = ScheduleRules.WrongAccountProblem(process, session, "run scheduled jobs");
        Assert.Equal(refused, problem is not null);
        if (refused)
        {
            Assert.Contains(@"PC\Admin", problem);
            Assert.Contains(@"PC\Kid", problem);
            Assert.Contains("run scheduled jobs", problem);
        }
    }

    // ---- Elevated task executable location ----------------------------------------------------------------------

    private const string UserSid = "S-1-5-21-1000-2000-3000-1001";
    private const string UsersSid = "S-1-5-32-545";
    private const string AuthenticatedUsersSid = "S-1-5-11";

    private static TaskPathAccessRule Allow(string sid, System.Security.AccessControl.FileSystemRights rights, bool inheritOnly = false) =>
        new(sid, rights, true, inheritOnly);

    private static TaskPathSecurity ProtectedPath(string path, string owner = TaskExecutableRules.AdministratorsSid, params TaskPathAccessRule[] extra) =>
        new(path, owner,
        [
            Allow(TaskExecutableRules.AdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl),
            Allow(TaskExecutableRules.SystemSid, System.Security.AccessControl.FileSystemRights.FullControl),
            Allow(TaskExecutableRules.TrustedInstallerSid, System.Security.AccessControl.FileSystemRights.FullControl),
            Allow(UsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute),
            .. extra,
        ]);

    private static IReadOnlyList<TaskPathSecurity> ProgramFilesChain() =>
    [
        ProtectedPath(@"C:\Program Files\PC Checker"),
        ProtectedPath(@"C:\Program Files", TaskExecutableRules.TrustedInstallerSid,
            Allow("S-1-3-0", System.Security.AccessControl.FileSystemRights.FullControl, inheritOnly: true)),
        ProtectedPath(@"C:\", TaskExecutableRules.TrustedInstallerSid,
            Allow(AuthenticatedUsersSid, System.Security.AccessControl.FileSystemRights.AppendData),
            Allow(AuthenticatedUsersSid, System.Security.AccessControl.FileSystemRights.Modify, inheritOnly: true)),
    ];

    [Fact]
    public void Elevated_task_executable_under_program_files_is_accepted()
    {
        Assert.Null(TaskExecutableRules.WhyUnsafe(ProtectedPath(Exe), ProgramFilesChain()));
    }

    [Fact]
    public void Elevated_task_executable_the_user_can_change_is_refused()
    {
        var file = ProtectedPath(Exe, TaskExecutableRules.AdministratorsSid, Allow(UserSid, System.Security.AccessControl.FileSystemRights.Modify));
        var problem = TaskExecutableRules.WhyUnsafe(file, ProgramFilesChain());
        Assert.NotNull(problem);
        Assert.Contains("Program Files", problem);

        Assert.NotNull(TaskExecutableRules.WhyUnsafe(ProtectedPath(Exe, UserSid), ProgramFilesChain()));
    }

    [Fact]
    public void Elevated_task_executable_in_a_user_writable_folder_is_refused()
    {
        const string downloads = @"C:\Users\me\Downloads";
        var file = ProtectedPath(downloads + @"\PcChecker.exe", UserSid, Allow(UserSid, System.Security.AccessControl.FileSystemRights.FullControl));
        var chain = new[] { ProtectedPath(downloads, UserSid, Allow(UserSid, System.Security.AccessControl.FileSystemRights.FullControl)) };
        Assert.NotNull(TaskExecutableRules.WhyUnsafe(file, chain));

        // A protected file in a folder where anyone can add files (DLL planting) or delete it is refused too.
        var folder = ProtectedPath(@"C:\Program Files\PC Checker", TaskExecutableRules.AdministratorsSid,
            Allow(UsersSid, System.Security.AccessControl.FileSystemRights.CreateFiles));
        var problem = TaskExecutableRules.WhyUnsafe(ProtectedPath(Exe), [folder, .. ProgramFilesChain().Skip(1)]);
        Assert.NotNull(problem);
        Assert.Contains(@"C:\Program Files\PC Checker", problem);
    }

    [Fact]
    public void Elevated_task_executable_below_a_renameable_folder_is_refused()
    {
        // C:\Tools created by the user inherits Modify for Authenticated Users, so the whole folder can be swapped.
        var tools = ProtectedPath(@"C:\Tools", UserSid, Allow(AuthenticatedUsersSid, System.Security.AccessControl.FileSystemRights.Modify));
        var chain = new[] { ProtectedPath(@"C:\Tools\PC Checker"), tools, ProgramFilesChain()[2] };
        Assert.NotNull(TaskExecutableRules.WhyUnsafe(ProtectedPath(@"C:\Tools\PC Checker\PcChecker.exe"), chain));

        // Deny rules and inherit-only rules do not make a location unsafe; an empty folder chain does.
        Assert.Null(TaskExecutableRules.WhyUnsafe(
            ProtectedPath(Exe, TaskExecutableRules.AdministratorsSid, new TaskPathAccessRule(UserSid, System.Security.AccessControl.FileSystemRights.FullControl, false, false)),
            ProgramFilesChain()));
        Assert.NotNull(TaskExecutableRules.WhyUnsafe(ProtectedPath(Exe), []));
    }

    [Fact]
    public void Invalid_job_time_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => JobTaskXml.Build(new JobSchedule { Time = "25:00" }, Exe, null, new DateOnly(2026, 10, 27)));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\A B\", "\"C:\\A B\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_like_windows_expects(string arg, string expected)
    {
        Assert.Equal(expected, SchtasksCommandBuilder.JoinArguments([arg]));
    }

    // ---- schtasks CSV -------------------------------------------------------------------------------------------

    private static string Row(string next, string status, string last, string result, string run) =>
        $"\"DESKTOP-1\",\"\\PC Checker\\Health\",\"{next}\",\"{status}\",\"Interactive only\",\"{last}\",\"{result}\",\"DESKTOP-1\\Alex\",\"{run.Replace("\"", "\"\"")}\",\"N/A\",\"N/A\",\"Enabled\",\"Disabled\",\"Stop On Battery Mode\",\"Alex\",\"Disabled\",\"72:00:00\",\"Scheduling data is not available in this format.\",\"Weekly\",\"10:00:00\",\"27/10/2026\",\"N/A\",\"SUN\",\"Every 1 week(s)\",\"Disabled\",\"Disabled\",\"Disabled\",\"Disabled\"";

    [Fact]
    public void Query_output_is_read_by_column_position()
    {
        var csv = Row("11/1/2026 10:00:00 AM", "Ready", "10/25/2026 10:00:03 AM", "0", "\"" + Exe + "\" --scheduled health") + "\r\n";
        var status = SchtasksCsvParser.ParseQuery(@"PC Checker\Health", csv, CultureInfo.GetCultureInfo("en-US"));

        Assert.True(status.Exists);
        Assert.Equal(@"\PC Checker\Health", status.TaskName);
        Assert.Equal("Ready", status.Status);
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0), status.NextRun);
        Assert.Equal(new DateTime(2026, 10, 25, 10, 0, 3), status.LastRun);
        Assert.Equal(0, status.LastResult);
        Assert.Equal(Exe, ScheduleRules.ExtractExecutable(status.TaskToRun));
    }

    [Fact]
    public void Localized_output_parses_dates_with_the_user_culture_and_keeps_translated_words()
    {
        var csv = Row("01.11.2026 10:00:00", "Bereit", "30.11.1999 00:00:00", "267011", "\"" + Exe + "\" --scheduled health");
        var status = SchtasksCsvParser.ParseQuery("x", csv, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0), status.NextRun);
        Assert.Null(status.LastRun); // the 1999 placeholder means "never ran"
        Assert.Equal("Bereit", status.Status);
        Assert.Equal("Has not run yet", ScheduleRules.DescribeLastResult(status.LastResult));
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("Nie")]
    [InlineData("")]
    [InlineData(null)]
    public void Placeholder_dates_are_null(string? text)
    {
        Assert.Null(SchtasksCsvParser.ParseDate(text, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Missing_or_header_only_output_means_the_task_does_not_exist()
    {
        Assert.False(SchtasksCsvParser.ParseQuery("t", "").Exists);
        Assert.False(SchtasksCsvParser.ParseQuery("t", "\"HostName\",\"TaskName\",\"Next Run Time\",\"Status\",\"Logon Mode\",\"Last Run Time\",\"Last Result\",\"Author\",\"Task To Run\"").Exists);
        Assert.False(SchtasksCsvParser.ParseQuery("t", "ERROR: The system cannot find the file specified.").Exists);
    }

    [Fact]
    public void Csv_handles_quotes_commas_and_line_endings()
    {
        var rows = SchtasksCsvParser.ParseCsv("\"a,b\",\"say \"\"hi\"\"\",plain\r\n\"x\",,\"\"\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a,b", "say \"hi\"", "plain"], rows[0]);
        Assert.Equal(["x", "", ""], rows[1]);
    }

    // ---- ScheduleRules ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("9:30", 9, 30)]
    [InlineData("09:30", 9, 30)]
    [InlineData("18.45", 18, 45)]
    [InlineData("0700", 7, 0)]
    [InlineData(" 23:59 ", 23, 59)]
    public void Times_parse_in_24_hour_form(string text, int hour, int minute)
    {
        Assert.True(ScheduleRules.TryParseTime(text, out var t));
        Assert.Equal(new TimeOnly(hour, minute), t);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("7pm")]
    [InlineData("1:5")]
    [InlineData("")]
    public void Bad_times_are_rejected(string text)
    {
        Assert.False(ScheduleRules.TryParseTime(text, out _));
        Assert.NotNull(ScheduleRules.Validate(new JobSchedule { Time = text }));
    }

    [Fact]
    public void Next_run_for_daily_and_weekly_schedules()
    {
        var wednesdayNoon = new DateTime(2026, 10, 28, 12, 0, 0); // a Wednesday
        Assert.Equal(DayOfWeek.Wednesday, wednesdayNoon.DayOfWeek);

        var daily = new JobSchedule { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = "18:00" };
        Assert.Equal(new DateTime(2026, 10, 28, 18, 0, 0), ScheduleRules.NextRun(daily, wednesdayNoon));
        daily.Time = "08:00";
        Assert.Equal(new DateTime(2026, 10, 29, 8, 0, 0), ScheduleRules.NextRun(daily, wednesdayNoon));

        var sameDayLater = new JobSchedule { Enabled = true, Day = DayOfWeek.Wednesday, Time = "13:00" };
        Assert.Equal(new DateTime(2026, 10, 28, 13, 0, 0), ScheduleRules.NextRun(sameDayLater, wednesdayNoon));
        var sameDayEarlier = new JobSchedule { Enabled = true, Day = DayOfWeek.Wednesday, Time = "11:00" };
        Assert.Equal(new DateTime(2026, 11, 4, 11, 0, 0), ScheduleRules.NextRun(sameDayEarlier, wednesdayNoon));
        var sunday = new JobSchedule { Enabled = true, Day = DayOfWeek.Sunday, Time = "10:00" };
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0), ScheduleRules.NextRun(sunday, wednesdayNoon));

        Assert.Null(ScheduleRules.NextRun(new JobSchedule { Enabled = false }, wednesdayNoon));
    }

    [Fact]
    public void Descriptions_read_naturally()
    {
        Assert.Equal("Every Sunday at 10:00", ScheduleRules.Describe(new JobSchedule { Day = DayOfWeek.Sunday, Time = "10:00" }));
        var daily = new JobSchedule { Frequency = ScheduleFrequency.Daily, Time = "7:5".Replace(":5", ":05") };
        Assert.Equal("Every day at 07:05", ScheduleRules.Describe(daily));
        Assert.Equal("every day at 07:05", ScheduleRules.DescribeInline(daily));
    }

    [Fact]
    public void Normalize_repairs_missing_duplicate_and_invalid_entries()
    {
        var settings = new SchedulesSettings
        {
            Jobs =
            [
                new JobSchedule { Job = ScheduledJobKind.Cleanup, Enabled = true, Time = "nonsense" },
                new JobSchedule { Job = ScheduledJobKind.Cleanup, Enabled = false, Time = "01:00" },
                new JobSchedule { Job = ScheduledJobKind.Report, KeepReports = 0, Time = "6:30" },
            ],
        };

        ScheduleRules.Normalize(settings);
        Assert.Equal(ScheduledJobIds.All, settings.Jobs.Select(j => j.Job));
        var cleanup = settings.For(ScheduledJobKind.Cleanup);
        Assert.True(cleanup.Enabled); // the first entry wins
        Assert.Equal("10:30", cleanup.Time); // invalid time back to the default
        Assert.Equal(1, settings.For(ScheduledJobKind.Report).KeepReports);
        Assert.Equal("06:30", settings.For(ScheduledJobKind.Report).Time);

        var app = new AppSettings { Schedules = null!, Tray = null! };
        Assert.Equal(4, ScheduleRules.EnsureSettings(app).Jobs.Count);
        Assert.NotNull(app.Tray);
    }

    [Fact]
    public void Moved_executable_is_detected_from_the_task_command()
    {
        var status = new ScheduledTaskStatus("t", true, "Ready", null, null, 0, "\"D:\\Old Place\\PcChecker.exe\" --scheduled health", null, null);
        Assert.True(ScheduleRules.PointsElsewhere(status, Exe));
        Assert.False(ScheduleRules.PointsElsewhere(status, @"d:/old place/PCCHECKER.EXE"));
        Assert.False(ScheduleRules.PointsElsewhere(ScheduledTaskStatus.Missing("t"), Exe));
        Assert.Equal(@"C:\Tools\PcChecker.exe", ScheduleRules.ExtractExecutable(@"C:\Tools\PcChecker.exe --tray"));
        Assert.Null(ScheduleRules.ExtractExecutable("  "));
    }

    [Fact]
    public void Unattended_cleanup_only_touches_safe_categories_and_never_the_recycle_bin()
    {
        var defaults = ScheduleRules.UnattendedCleanupCategories(CleanupCatalog.All, []);
        Assert.NotEmpty(defaults);
        Assert.DoesNotContain("recycle.bin", defaults);
        Assert.All(defaults, id => Assert.Equal(RiskLevel.Safe, CleanupCatalog.Find(id)!.Risk));

        var chosen = ScheduleRules.UnattendedCleanupCategories(CleanupCatalog.All, ["user.temp", "prefetch", "recycle.bin", "windows.old", "crash.dumps"]);
        Assert.Equal(["user.temp"], chosen);
    }

    [Fact]
    public void Report_retention_keeps_the_newest()
    {
        var now = new DateTime(2026, 10, 27);
        var files = Enumerable.Range(0, 5).Select(i => ($"r{i}.html", now.AddDays(-i))).ToList();
        Assert.Equal(["r3.html", "r4.html"], ScheduleRules.ReportsToDelete(files, 3));
        Assert.Empty(ScheduleRules.ReportsToDelete(files, 10));
        Assert.Equal(4, ScheduleRules.ReportsToDelete(files, 0).Count); // clamped to keep at least one
    }

    [Theory]
    [InlineData(0L, "Completed")]
    [InlineData(267011L, "Has not run yet")]
    [InlineData(-2147020576L, "Skipped: Windows refused to start it (on battery or the PC was busy)")]
    [InlineData(2147946720L, "Skipped: Windows refused to start it (on battery or the PC was busy)")]
    [InlineData(5L, "Code 0x00000005")]
    public void Last_result_codes_are_explained(long code, string expected)
    {
        Assert.Equal(expected, ScheduleRules.DescribeLastResult(code));
    }

    // ---- Result summaries ---------------------------------------------------------------------------------------

    private static HealthReport Report(int score, params (HealthStatus Status, string Title)[] items) =>
        new(score, items.Select(i => new HealthItem("Disk", i.Status, i.Title, "detail", null, HealthItem.NoMetrics)).ToList(),
            [], [], [], [], [], [], DateTimeOffset.Now, []);

    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Health_summary_flags_a_score_drop_and_new_critical_items()
    {
        var first = JobResultSummarizer.Health(Report(91), null, T0, T0.AddSeconds(30), "schedule");
        Assert.Equal("Health score 91", first.Title);
        Assert.False(first.NeedsAttention);

        var same = JobResultSummarizer.Health(Report(90, (HealthStatus.Warning, "Low free space on C:")), first, T0, T0, "schedule");
        Assert.Equal("Health score 90 (down 1)", same.Title);
        Assert.False(same.NeedsAttention);
        Assert.Contains("none of them new", same.Summary);

        var dropped = JobResultSummarizer.Health(Report(80), same, T0, T0, "schedule");
        Assert.True(dropped.NeedsAttention);
        Assert.Contains("from 90 to 80", dropped.Summary);

        var critical = JobResultSummarizer.Health(Report(79, (HealthStatus.Critical, "Drive D: is failing")), dropped, T0, T0, "schedule");
        Assert.True(critical.NeedsAttention);
        Assert.StartsWith("New critical problem: Drive D: is failing", critical.Summary);
        Assert.Equal(["Drive D: is failing"], critical.CriticalItems);

        var stillCritical = JobResultSummarizer.Health(Report(79, (HealthStatus.Critical, "Drive D: is failing")), critical, T0, T0, "schedule");
        Assert.False(stillCritical.NeedsAttention); // same problem as last time, score unchanged
        Assert.Equal("Health score 79 (no change)", stillCritical.Title);
    }

    [Fact]
    public void Cleanup_driver_report_and_failure_summaries()
    {
        var dry = JobResultSummarizer.Cleanup(new CleanupRunResult(120, 3L * 1024 * 1024 * 1024, [], [], true), 9, T0, T0, "schedule");
        Assert.StartsWith("Cleanup preview:", dry.Title);
        Assert.True(dry.DryRun);
        Assert.False(dry.NeedsAttention);

        var withErrors = JobResultSummarizer.Cleanup(new CleanupRunResult(1, 10, [], ["Access denied: C:\\x"], false), 1, T0, T0, "tray");
        Assert.True(withErrors.NeedsAttention);

        var update = new DriverUpdateInfo("1", "NVIDIA - Display - 32.0.15.6094", null, null, null, 0, DriverUpdateSource.WindowsUpdate, null, null, null, false, false, false);
        var drivers = JobResultSummarizer.Drivers(new DriverUpdateCheckResult([update], [], [], T0), T0, T0, "schedule");
        Assert.Equal("1 driver update available", drivers.Title);
        Assert.True(drivers.NeedsAttention);
        Assert.Equal(1, drivers.UpdatesFound);
        Assert.False(JobResultSummarizer.Drivers(new DriverUpdateCheckResult([], [], [], T0), T0, T0, "schedule").NeedsAttention);

        var report = JobResultSummarizer.Report(@"C:\r.html", 2, ["drivers"], T0, T0, "schedule");
        Assert.Contains("Removed 2 older reports", report.Summary);
        Assert.Contains("Left out: drivers", report.Summary);

        var timedOut = JobResultSummarizer.Failure(ScheduledJobKind.Report, "", true, T0, T0, "schedule");
        Assert.False(timedOut.Success);
        Assert.Equal("PC report took too long", timedOut.Title);
    }

    [Fact]
    public void Health_after_a_failed_run_does_not_call_known_critical_items_new()
    {
        var failed = JobResultSummarizer.Failure(ScheduledJobKind.Health, "WMI did not answer", false, T0, T0, "schedule");
        var next = JobResultSummarizer.Health(Report(79, (HealthStatus.Critical, "Drive D: is failing")), failed, T0, T0, "schedule");

        Assert.Equal("Health score 79", next.Title);
        Assert.StartsWith("Critical: Drive D: is failing", next.Summary);
        Assert.DoesNotContain("New critical", next.Summary);
        Assert.Equal(["Drive D: is failing"], next.CriticalItems);
    }

    [Fact]
    public void Notifications_respect_the_attention_setting_and_windows_limits()
    {
        var quiet = JobResultSummarizer.Health(Report(95), null, T0, T0, "schedule");
        Assert.False(JobResultSummarizer.ShouldNotify(quiet, onlyWhenAttention: true));
        Assert.True(JobResultSummarizer.ShouldNotify(quiet, onlyWhenAttention: false));
        Assert.True(JobResultSummarizer.ShouldNotify(JobResultSummarizer.Failure(ScheduledJobKind.Health, "x", false, T0, T0, "schedule"), true));

        var noisy = quiet with { Title = new string('t', 100), Summary = new string('s', 400) };
        var (title, message) = JobResultSummarizer.NotificationText(noisy);
        Assert.Equal(JobResultSummarizer.MaxTitleLength, title.Length);
        Assert.EndsWith("…", message);
        Assert.Equal(JobResultSummarizer.MaxMessageLength, message.Length);
    }

    // ---- Tray text ----------------------------------------------------------------------------------------------

    [Fact]
    public void Tray_tooltip_shows_cpu_and_gpu_temperatures()
    {
        Assert.Equal("PC Checker — CPU 62 °C · GPU 58 °C", TrayText.Tooltip(61.6, 58.2));
        Assert.Equal("PC Checker — GPU 58 °C", TrayText.Tooltip(null, 58));
        Assert.Equal("PC Checker", TrayText.Tooltip(double.NaN, null));
        Assert.Equal("PC Checker — CPU 90 °C\n1 alert", TrayText.Tooltip(90, null, TrayText.Alerts(1)));
        Assert.True(TrayText.Tooltip(50, 50, new string('x', 300)).Length <= TrayText.MaxTooltipLength);
    }

    [Fact]
    public void Tray_icon_text_levels_and_alerts()
    {
        Assert.Equal("62", TrayText.IconText(61.7));
        Assert.Equal("99+", TrayText.IconText(104));
        Assert.Equal("--", TrayText.IconText(null));
        Assert.Equal(71.0, TrayText.Hottest(55, null, 71, double.NaN));
        Assert.Null(TrayText.Hottest(null, double.NaN));

        Assert.Equal(HealthStatus.Good, TrayText.Level(70, 85, 95));
        Assert.Equal(HealthStatus.Warning, TrayText.Level(85, 85, 95));
        Assert.Equal(HealthStatus.Critical, TrayText.Level(96, 85, 95));
        Assert.Equal(HealthStatus.Unknown, TrayText.Level(null, 85, 95));

        var monitoring = new MonitoringSettings();
        var readings = new TrayReadings(90, 50, 60, 10, 20, 30);
        Assert.Equal(2, TrayText.ActiveAlerts(readings, monitoring)); // CPU 90 ≥ 85 and drive 60 ≥ 55
        monitoring.AlertsEnabled = false;
        Assert.Equal(0, TrayText.ActiveAlerts(readings, monitoring));
        Assert.Equal("≈ 312 W", TrayText.Watts(311.6, estimated: true));
        Assert.Equal("--", TrayText.Percent(null));
    }

    [Fact]
    public void Tray_and_overlay_limits_match_the_alert_limits()
    {
        var monitoring = new MonitoringSettings();
        const string ryzen = "AMD Ryzen 9 7950X 16-Core Processor";

        // A Ryzen 7000/9000 at its 95 °C design point is "warm" (90/98 °C), as on the Temperatures page, not critical.
        Assert.Equal((90d, 98d), TrayText.LimitsFor(OverlayMetricIds.CpuTemp, monitoring, ryzen));
        Assert.Equal(HealthStatus.Warning, TrayText.LevelFor(OverlayMetricIds.CpuTemp, 95, monitoring, ryzen));
        Assert.Equal(HealthStatus.Critical, TrayText.LevelFor(OverlayMetricIds.CpuTemp, 95, monitoring, "Intel(R) Core(TM) i9-14900K"));

        // The hot spot has its own limits (100/110 °C), not the GPU core's plus 10.
        Assert.Equal((100d, 110d), TrayText.LimitsFor(OverlayMetricIds.GpuHotspot, monitoring, null));
        Assert.Equal(HealthStatus.Good, TrayText.LevelFor(OverlayMetricIds.GpuHotspot, 95, monitoring, null));
        Assert.Null(TrayText.LimitsFor(OverlayMetricIds.CpuLoad, monitoring, null));

        var snapshot = new SensorSnapshot(DateTimeOffset.Now,
        [
            new SensorReading("cpu/t", ryzen, HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", 95, null, null, "°C"),
        ]);
        var readings = TrayReadings.From(snapshot);
        Assert.Equal(ryzen, readings.CpuName);
        Assert.Equal(1, TrayText.ActiveAlerts(readings, monitoring)); // one warning, counted once
    }

    [Fact]
    public void Tray_readings_come_from_the_sensor_snapshot()
    {
        var snapshot = new SensorSnapshot(DateTimeOffset.Now,
        [
            new SensorReading("cpu/t", "Intel Core i7-13700K", HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", 64, null, null, "°C"),
            new SensorReading("gpu/t", "NVIDIA GeForce RTX 4070", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 57, null, null, "°C"),
            new SensorReading("ssd/t", "Samsung 990 PRO", HardwareKind.Storage, SensorKind.Temperature, "Temperature", 41, null, null, "°C"),
            new SensorReading("hdd/t", "WD Blue", HardwareKind.Storage, SensorKind.Temperature, "Temperature", 36, null, null, "°C"),
        ]);
        var r = TrayReadings.From(snapshot);
        Assert.Equal(64, r.CpuTempC);
        Assert.Equal(57, r.GpuTempC);
        Assert.Equal(41, r.DriveTempC);
        Assert.Same(TrayReadings.Empty, TrayReadings.From(null));
    }

    [Fact]
    public void Temperature_alert_notification_text()
    {
        var alert = new TemperatureAlert("cpu/t", "Intel Core i7-13700K", 96.4, 95, Severity.Critical, DateTimeOffset.Now);
        var (title, message) = TrayText.AlertNotification(alert);
        Assert.Equal("Intel Core i7-13700K is running critical", title);
        Assert.StartsWith("96 °C (your limit is 95 °C)", message);
    }

    [Fact]
    public void Notification_throttle_lets_one_through_per_window_and_key()
    {
        var throttle = new NotificationThrottle(TimeSpan.FromMinutes(10));
        Assert.True(throttle.ShouldShow("cpu", T0));
        Assert.False(throttle.ShouldShow("CPU", T0.AddMinutes(5)));
        Assert.True(throttle.ShouldShow("gpu", T0.AddMinutes(5)));
        Assert.True(throttle.ShouldShow("cpu", T0.AddMinutes(10)));
    }

    // ---- Popup placement ----------------------------------------------------------------------------------------

    [Fact]
    public void Popup_sits_above_a_bottom_taskbar_near_the_click()
    {
        var work = new ScreenRect(0, 0, 1920, 1032);
        Assert.Equal(TaskbarEdge.Bottom, TrayPopupPlacement.InferEdge(work, 1800, 1050));
        var (left, top) = TrayPopupPlacement.Compute(work, 1800, 1050, 360, 400, 12);
        Assert.Equal(1920 - 360 - 12, left); // clamped to the right edge
        Assert.Equal(1032 - 400 - 12, top);

        var (midLeft, _) = TrayPopupPlacement.Compute(work, 900, 1050, 360, 400, 12);
        Assert.Equal(720, midLeft); // centred on the click
    }

    [Fact]
    public void Popup_follows_top_left_and_right_taskbars_and_never_leaves_the_work_area()
    {
        var topBar = new ScreenRect(0, 48, 1920, 1032);
        Assert.Equal((1548.0, 60.0), TrayPopupPlacement.Compute(topBar, 1880, 20, 360, 400, 12));

        var leftBar = new ScreenRect(60, 0, 1860, 1080);
        Assert.Equal(TaskbarEdge.Left, TrayPopupPlacement.InferEdge(leftBar, 30, 1000));
        Assert.Equal((72.0, 668.0), TrayPopupPlacement.Compute(leftBar, 30, 1000, 360, 400, 12));

        var rightBar = new ScreenRect(0, 0, 1860, 1080);
        Assert.Equal(TaskbarEdge.Right, TrayPopupPlacement.InferEdge(rightBar, 1890, 100));
        Assert.Equal((1488.0, 12.0), TrayPopupPlacement.Compute(rightBar, 1890, 100, 360, 400, 12));

        // Bigger than the screen: pinned to the start instead of throwing.
        var tiny = new ScreenRect(0, 0, 300, 300);
        Assert.Equal((12.0, 12.0), TrayPopupPlacement.Compute(tiny, 290, 310, 360, 400, 12));
    }

    // ---- Pipe protocol ------------------------------------------------------------------------------------------

    [Fact]
    public void Pipe_requests_round_trip_and_reject_garbage()
    {
        foreach (var job in ScheduledJobIds.All)
        {
            var request = new PipeRequest(PipeCommand.Run, job);
            Assert.Equal(request, ScheduledJobPipe.Parse(ScheduledJobPipe.Format(request)));
        }

        Assert.Equal(new PipeRequest(PipeCommand.Show, null), ScheduledJobPipe.Parse(" SHOW "));
        Assert.Equal(new PipeRequest(PipeCommand.ShowProcesses, null), ScheduledJobPipe.Parse(ScheduledJobPipe.Format(new PipeRequest(PipeCommand.ShowProcesses, null))));
        Assert.Equal("show processes", ScheduledJobPipe.Format(new PipeRequest(PipeCommand.ShowProcesses, null)));
        Assert.Null(ScheduledJobPipe.Parse("show settings"));
        Assert.Null(ScheduledJobPipe.Parse("run"));
        Assert.Null(ScheduledJobPipe.Parse("run format-c"));
        Assert.Null(ScheduledJobPipe.Parse(null));
        Assert.Null(ScheduledJobPipe.Parse("show me everything"));
    }

    // ---- Job ids ------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_job_has_an_id_a_task_name_and_friendly_text()
    {
        foreach (var job in ScheduledJobIds.All)
        {
            Assert.True(ScheduledJobIds.TryParse(ScheduledJobIds.Id(job), out var parsed));
            Assert.Equal(job, parsed);
            Assert.StartsWith(@"PC Checker\", ScheduledJobIds.TaskName(job));
            Assert.False(string.IsNullOrWhiteSpace(ScheduledJobIds.DisplayName(job)));
            Assert.False(string.IsNullOrWhiteSpace(ScheduledJobIds.Description(job)));
        }

        Assert.True(ScheduledJobIds.TryParse("clean", out var alias));
        Assert.Equal(ScheduledJobKind.Cleanup, alias);
    }

    [Theory]
    [InlineData(60, "Files changed in the last hour")]
    [InlineData(120, "Files changed in the last 2 hours")]
    [InlineData(30, "Files changed in the last 30 minutes")]
    [InlineData(1, "Files changed in the last minute")]
    [InlineData(0, "Files in use")]
    public void Cleanup_description_follows_the_skip_recent_setting(int minutes, string expected)
    {
        Assert.Equal(expected, ScheduledJobIds.RecentFilesText(minutes));
        Assert.Contains(expected, ScheduledJobIds.Description(ScheduledJobKind.Cleanup, minutes));
    }

    [Fact]
    public void Job_pipe_name_is_tied_to_the_session_and_user()
    {
        Assert.Equal("PcChecker.Jobs.2.S-1-5-21-1000", ScheduledJobPipe.NameFor(2, "S-1-5-21-1000"));
        Assert.Equal("PcChecker.Jobs.0", ScheduledJobPipe.NameFor(0, null));
        Assert.Equal("PcChecker.Jobs.3.S-1-5", ScheduledJobPipe.NameFor(3, "S-1-5\\..//"));
        Assert.StartsWith(ScheduledJobPipe.PipeName + ".", ScheduledJobPipe.CurrentPipeName);
    }

    // ---- Store, settings and the executor -----------------------------------------------------------------------

    [Fact]
    public void Job_store_round_trips_and_tolerates_corrupt_files()
    {
        var dir = TempDir();
        try
        {
            var store = new JsonScheduledJobStore(null, dir);
            ScheduledJobResult? saved = null;
            store.Saved += (_, r) => saved = r;
            var result = JobResultSummarizer.Health(Report(77, (HealthStatus.Critical, "Battery worn out")), null, T0, T0.AddSeconds(12), "schedule");
            store.Save(result);

            Assert.Same(result, saved);
            Assert.True(File.Exists(Path.Combine(dir, "health-last.json")));
            var loaded = store.Load(ScheduledJobKind.Health);
            Assert.NotNull(loaded);
            Assert.Equal(77, loaded!.HealthScore);
            Assert.Equal(["Battery worn out"], loaded.CriticalItems);
            Assert.Equal(TimeSpan.FromSeconds(12), loaded.Duration);
            Assert.Single(store.LoadAll());

            File.WriteAllText(Path.Combine(dir, "drivers-last.json"), "{ not json");
            Assert.Null(store.Load(ScheduledJobKind.Drivers));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Demo_store_has_a_sample_result_for_every_job()
    {
        var store = new DemoScheduledJobStore();
        Assert.Equal(ScheduledJobIds.All.Count, store.LoadAll().Count);
        Assert.Equal(86, store.Load(ScheduledJobKind.Health)!.HealthScore);
    }

    [Fact]
    public async Task Tray_and_schedule_settings_survive_a_save_and_load()
    {
        var dir = TempDir();
        try
        {
            var service = new JsonSettingsService(dir);
            await service.LoadAsync();
            service.Current.Tray.CloseToTray = true;
            service.Current.Tray.ShowTemperatureInIcon = true;
            var cleanup = service.Current.Schedules.For(ScheduledJobKind.Cleanup);
            cleanup.Enabled = true;
            cleanup.Frequency = ScheduleFrequency.Daily;
            cleanup.DryRun = true;
            service.Current.Schedules.NotifyOnlyWhenAttention = false;
            await service.SaveAsync();

            var reloaded = new JsonSettingsService(dir);
            await reloaded.LoadAsync();
            Assert.True(reloaded.Current.Tray.CloseToTray);
            Assert.True(reloaded.Current.Tray.ShowTemperatureInIcon);
            Assert.True(reloaded.Current.Tray.StartInTray);
            var c = reloaded.Current.Schedules.For(ScheduledJobKind.Cleanup);
            Assert.True(c.Enabled);
            Assert.Equal(ScheduleFrequency.Daily, c.Frequency);
            Assert.True(c.DryRun);
            Assert.False(reloaded.Current.Schedules.NotifyOnlyWhenAttention);
            Assert.Equal(4, reloaded.Current.Schedules.Jobs.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Executor_cleans_only_unattended_safe_categories_and_stores_the_result()
    {
        var dir = TempDir();
        try
        {
            var (executor, scanner, store, settings) = CreateExecutor(dir);
            settings.Current.Cleanup.EnabledCategoryIds = ["user.temp", "recycle.bin", "prefetch", "thumbnails"];
            settings.Current.Schedules.For(ScheduledJobKind.Cleanup).DryRun = true;

            var result = await executor.RunAsync(ScheduledJobKind.Cleanup, "schedule", null, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result!.Success);
            Assert.True(result.DryRun);
            Assert.Equal(["user.temp", "thumbnails"], scanner.RequestedIds);
            Assert.Equal(result.Title, store.Load(ScheduledJobKind.Cleanup)!.Title);
            Assert.False(executor.IsRunning(ScheduledJobKind.Cleanup));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Executor_runs_health_and_report_jobs_and_prunes_old_scheduled_reports()
    {
        var dir = TempDir();
        try
        {
            var (executor, _, _, settings) = CreateExecutor(dir);
            var reports = Path.Combine(dir, "reports");
            executor.ReportsDirectory = reports;
            Directory.CreateDirectory(reports);
            for (int i = 0; i < 3; i++)
            {
                var old = Path.Combine(reports, $"{ScheduledJobExecutor.ScheduledReportPrefix}old-{i}.html");
                File.WriteAllText(old, "old");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10 - i));
            }

            var manual = Path.Combine(reports, "my-own-report.html");
            File.WriteAllText(manual, "keep me");
            settings.Current.Schedules.For(ScheduledJobKind.Report).KeepReports = 2;

            var health = await executor.RunAsync(ScheduledJobKind.Health, "manual", null, CancellationToken.None);
            Assert.NotNull(health!.HealthScore);

            var report = await executor.RunAsync(ScheduledJobKind.Report, "schedule", null, CancellationToken.None);
            Assert.True(report!.Success, report.Summary);
            Assert.True(File.Exists(report.OutputPath));
            Assert.True(File.Exists(manual));
            Assert.Equal(2, Directory.GetFiles(reports, ScheduledJobExecutor.ScheduledReportPrefix + "*.html").Length);
            Assert.Contains("Removed 2 older reports", report.Summary);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static (ScheduledJobExecutor Executor, CapturingScanner Scanner, JsonScheduledJobStore Store, JsonSettingsService Settings) CreateExecutor(string dir, IActionJournal? journal = null)
    {
        var settings = new JsonSettingsService(Path.Combine(dir, "settings"));
        var store = new JsonScheduledJobStore(null, Path.Combine(dir, "schedules"));
        var scanner = new CapturingScanner();
        var executor = new ScheduledJobExecutor(new DemoHealthService(), scanner, new DemoTempFileCleaner(), new DemoDriverService(),
            new DemoHardwareInventoryService(), new DemoPerformanceAnalyzer(new DemoStartupService()), new HtmlReportService(), store, settings,
            NullLogger<ScheduledJobExecutor>.Instance, journal: journal);
        return (executor, scanner, store, settings);
    }

    [Fact]
    public async Task Executor_journals_health_runs_but_leaves_cleanup_to_the_cleaner()
    {
        var dir = TempDir();
        try
        {
            var journal = new CapturingJournal();
            var (executor, _, _, settings) = CreateExecutor(dir, journal);
            settings.Current.Schedules.For(ScheduledJobKind.Cleanup).DryRun = true;

            await executor.RunAsync(ScheduledJobKind.Health, "schedule", null, CancellationToken.None);
            await executor.RunAsync(ScheduledJobKind.Cleanup, "schedule", null, CancellationToken.None);

            var record = Assert.Single(journal.Records);
            Assert.Equal("Schedules", record.Module);
            Assert.Equal(@"PC Checker\Health", record.Target);
            Assert.Equal("Success", record.Outcome);
            Assert.StartsWith("Health check (schedule)", record.Action);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class CapturingJournal : IActionJournal
    {
        public List<ActionRecord> Records { get; } = [];

        public string JournalPath => string.Empty;

        public Task RecordAsync(ActionRecord record)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActionRecord>> ReadAsync(int take = 500) => Task.FromResult<IReadOnlyList<ActionRecord>>(Records);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class CapturingScanner : ITempFileScanner
    {
        private readonly DemoTempFileScanner _inner = new();

        public List<string> RequestedIds { get; } = [];

        public IReadOnlyList<CleanupCategory> Categories => _inner.Categories;

        public Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            var ids = categoryIds.ToList();
            RequestedIds.AddRange(ids);
            return _inner.ScanAsync(ids, progress, cancellationToken);
        }
    }
}
