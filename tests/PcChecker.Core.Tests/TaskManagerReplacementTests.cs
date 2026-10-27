using PcChecker.Core.Common;
using PcChecker.Core.TaskManager;
using Xunit;

namespace PcChecker.Core.Tests;

public class TaskManagerReplacementTests
{
    [Fact]
    public void Command_line_taskmgr_switch_is_parsed_and_swallows_the_path()
    {
        var options = CommandLineOptions.Parse(["--taskmgr", @"C:\Windows\System32\Taskmgr.exe"]);
        Assert.True(options.TaskManagerLaunch);
        Assert.Empty(options.UnknownArguments);
    }

    [Fact]
    public void Command_line_without_taskmgr_leaves_the_flag_off()
    {
        Assert.False(CommandLineOptions.Parse(["--demo"]).TaskManagerLaunch);
    }

    private const string Exe = @"C:\Program Files\PC Checker\PcChecker.exe";
    private static readonly string[] ProgramFiles = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Fact]
    public void Build_quotes_the_path_and_adds_the_switch()
    {
        var value = TaskManagerReplacement.BuildDebuggerValue(Exe);
        Assert.Equal($"\"{Exe}\" --taskmgr", value);
    }

    [Fact]
    public void Parse_reads_quoted_path_and_switch()
    {
        var parsed = TaskManagerReplacement.ParseDebuggerValue($"\"{Exe}\" --taskmgr");
        Assert.NotNull(parsed);
        Assert.Equal(Exe, parsed!.Value.ExePath);
        Assert.True(parsed.Value.HasSwitch);
    }

    [Fact]
    public void Parse_reads_unquoted_path_without_switch()
    {
        var parsed = TaskManagerReplacement.ParseDebuggerValue(@"C:\Other\debugger.exe");
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\Other\debugger.exe", parsed!.Value.ExePath);
        Assert.False(parsed.Value.HasSwitch);
    }

    [Fact]
    public void Parse_of_empty_is_null()
    {
        Assert.Null(TaskManagerReplacement.ParseDebuggerValue(null));
        Assert.Null(TaskManagerReplacement.ParseDebuggerValue("  "));
    }

    [Fact]
    public void Round_trip_build_then_parse_is_owned()
    {
        var value = TaskManagerReplacement.BuildDebuggerValue(Exe);
        Assert.True(TaskManagerReplacement.OwnsValue(value, Exe));
    }

    [Fact]
    public void Value_for_a_different_exe_is_not_owned()
    {
        var other = TaskManagerReplacement.BuildDebuggerValue(@"C:\Program Files\PC Checker\old\PcChecker.exe");
        Assert.False(TaskManagerReplacement.OwnsValue(other, Exe));
    }

    [Fact]
    public void A_third_party_debugger_is_not_owned()
    {
        Assert.False(TaskManagerReplacement.OwnsValue(@"C:\Windows\System32\vsjitdebugger.exe", Exe));
    }

    [Fact]
    public void Stale_value_is_our_switch_pointing_at_a_missing_file()
    {
        var value = TaskManagerReplacement.BuildDebuggerValue(Exe);
        Assert.True(TaskManagerReplacement.IsStaleOwnedValue(value, Exe, _ => false));
        Assert.False(TaskManagerReplacement.IsStaleOwnedValue(value, Exe, _ => true));
    }

    [Fact]
    public void Stale_detects_another_missing_pcchecker_copy()
    {
        var value = TaskManagerReplacement.BuildDebuggerValue(@"C:\Program Files\PC Checker\v1\PcChecker.exe");
        Assert.True(TaskManagerReplacement.IsStaleOwnedValue(value, Exe, _ => false));
    }

    [Fact]
    public void A_third_party_debugger_is_never_considered_stale()
    {
        Assert.False(TaskManagerReplacement.IsStaleOwnedValue(@"C:\Windows\System32\vsjitdebugger.exe", Exe, _ => false));
    }

    [Theory]
    [InlineData(@"C:\Program Files\PC Checker\PcChecker.exe", true)]
    [InlineData(@"C:\Program Files (x86)\PC Checker\PcChecker.exe", true)]
    [InlineData(@"C:\Users\Alex\Downloads\PcChecker.exe", false)]
    [InlineData(@"C:\Temp\PcChecker.exe", false)]
    public void Only_program_files_locations_are_eligible(string path, bool eligible)
    {
        Assert.Equal(eligible, TaskManagerReplacement.IsInstalledLocation(path, ProgramFiles));
        Assert.Equal(eligible, TaskManagerReplacement.IneligibleReason(path, ProgramFiles) is null);
    }

    [Fact]
    public void A_folder_whose_name_starts_like_program_files_is_not_mistaken_for_it()
    {
        // "C:\Program FilesX" must not match the "C:\Program Files" root.
        Assert.False(TaskManagerReplacement.IsInstalledLocation(@"C:\Program FilesX\app\PcChecker.exe", ProgramFiles));
    }
}
