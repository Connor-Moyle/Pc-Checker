using PcChecker.Core.Processes;
using PcChecker.Core.TaskManager;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Tests for the final bug-hunt fixes in Replace Task Manager and the Processes page.</summary>
public class TaskManagerFinalPassTests
{
    private const string ProcExp = @"C:\Tools\procexp64.exe";

    [Fact]
    public void A_backup_whose_program_exists_is_put_back()
    {
        Assert.True(TaskManagerReplacement.ShouldRestoreBackup($"\"{ProcExp}\"", path => path == ProcExp));
    }

    [Fact]
    public void A_backup_whose_program_was_removed_is_dropped()
    {
        Assert.False(TaskManagerReplacement.ShouldRestoreBackup($"\"{ProcExp}\"", _ => false));
        Assert.False(TaskManagerReplacement.ShouldRestoreBackup(ProcExp + " /e", _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_backup_is_never_restored(string? backup)
    {
        Assert.False(TaskManagerReplacement.ShouldRestoreBackup(backup, _ => true));
    }

    [Fact]
    public void An_unquoted_path_with_spaces_is_found_the_way_Windows_reads_it()
    {
        const string Path = @"C:\Program Files\Sysinternals\procexp64.exe";
        Assert.True(TaskManagerReplacement.ShouldRestoreBackup(Path, p => p == Path));
        Assert.True(TaskManagerReplacement.ShouldRestoreBackup(Path + " /e", p => p == Path));
        Assert.False(TaskManagerReplacement.ShouldRestoreBackup(Path, _ => false));
    }

    [Theory]
    [InlineData("procexp64.exe")]
    [InlineData("\"%ProgramFiles%\\Tools\\procexp64.exe\"")]
    public void A_value_that_cannot_be_checked_is_kept(string backup)
    {
        Assert.True(TaskManagerReplacement.ShouldRestoreBackup(backup, _ => false));
    }

    [Fact]
    public void A_file_address_to_a_user_folder_program_is_not_started_as_administrator()
    {
        string[] programFiles = [@"C:\Program Files", @"C:\Program Files (x86)"];
        static string? NotFound(string _) => null;
        static bool NoFolder(string _) => false;

        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch("file:///C:/Users/me/Downloads/tool.exe", @"C:\Windows", programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch("FILE:///C:/Users/me/Down%6Coads/tool.exe", @"C:\Windows", programFiles, NoFolder, NotFound, NotFound).Refusal);

        // A trusted program named by a file address starts from its plain path.
        var trusted = ProcessSafetyRules.DecideElevatedLaunch("file:///C:/Windows/System32/cmd.exe", @"C:\Windows", programFiles, NoFolder, NotFound, NotFound);
        Assert.Null(trusted.Refusal);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", trusted.Target);

        // Other links still open in their usual app.
        Assert.Equal("ms-settings:", ProcessSafetyRules.DecideElevatedLaunch("ms-settings:", @"C:\Windows", programFiles, NoFolder, NotFound, NotFound).Target);
    }
}
