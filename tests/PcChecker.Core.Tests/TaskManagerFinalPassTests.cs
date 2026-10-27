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
}
