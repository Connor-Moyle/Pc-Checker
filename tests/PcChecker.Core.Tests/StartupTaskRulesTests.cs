using PcChecker.Core.Performance;
using PcChecker.Core.ScheduledTasks;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class StartupTaskRulesTests
{
    private static StartupItem TaskItem(string path, string? publisher = "Contoso") =>
        new(TaskPaths.NameOf(path), "\"C:\\Program Files\\Contoso\\tool.exe\"", StartupEnumerator.TaskLocationPrefix + path, true, publisher, StartupKinds.Task);

    [Fact]
    public void Task_path_strips_the_location_prefix_and_is_null_for_other_kinds()
    {
        Assert.Equal(@"\Microsoft\Windows\TextServicesFramework\MsCtfMonitor", StartupTaskRules.TaskPath(TaskItem(@"\Microsoft\Windows\TextServicesFramework\MsCtfMonitor")));
        var run = new StartupItem("Steam", "steam.exe", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, "Valve", StartupKinds.Registry);
        Assert.Null(StartupTaskRules.TaskPath(run));
        Assert.Null(StartupTaskRules.RemoveRefusal(run));
        Assert.Null(StartupTaskRules.ToggleRefusal(run));
    }

    [Fact]
    public void Windows_tasks_are_neither_removed_nor_toggled_from_the_startup_page()
    {
        // A COM handler task has no program, so its publisher is the raw resource string, not "Microsoft".
        var ctf = TaskItem(@"\Microsoft\Windows\TextServicesFramework\MsCtfMonitor", "$(@%SystemRoot%\\system32\\MsCtfMonitor.dll,-1000)");
        Assert.True(StartupTaskRules.IsInMicrosoftFolder(ctf));
        Assert.Equal(StartupTaskRules.MicrosoftDeleteReason, StartupTaskRules.RemoveRefusal(ctf));
        Assert.Equal(StartupTaskRules.WindowsReadOnlyReason, StartupTaskRules.ToggleRefusal(ctf));
    }

    [Fact]
    public void Other_microsoft_product_tasks_can_be_turned_off_but_not_removed()
    {
        var office = TaskItem(@"\Microsoft\Office\Office Automatic Updates 2.0", "Microsoft Corporation");
        Assert.Equal(StartupTaskRules.MicrosoftDeleteReason, StartupTaskRules.RemoveRefusal(office));
        Assert.Null(StartupTaskRules.ToggleRefusal(office));
    }

    [Fact]
    public void Third_party_tasks_stay_removable_and_toggleable()
    {
        var asus = TaskItem(@"\ASUS\ArmouryCrate", "ASUSTeK COMPUTER INC.");
        Assert.False(StartupTaskRules.IsInMicrosoftFolder(asus));
        Assert.Null(StartupTaskRules.RemoveRefusal(asus));
        Assert.Null(StartupTaskRules.ToggleRefusal(asus));

        var root = TaskItem(@"\GoogleUpdateTaskMachineCore", "Google LLC");
        Assert.Null(StartupTaskRules.RemoveRefusal(root));
    }

    [Fact]
    public void Pc_checker_own_tasks_are_managed_in_settings()
    {
        var own = TaskItem(@"\PC Checker\Start", "PC Checker");
        Assert.Equal(TaskSafetyRules.OwnTaskReason, StartupTaskRules.RemoveRefusal(own));
        Assert.Equal(TaskSafetyRules.OwnTaskReason, StartupTaskRules.ToggleRefusal(own));
    }

    [Fact]
    public async Task Demo_service_refuses_removing_or_toggling_windows_tasks()
    {
        var demo = new DemoStartupService();
        var ctf = TaskItem(@"\Microsoft\Windows\TextServicesFramework\MsCtfMonitor");
        Assert.False((await demo.RemoveStartupItemAsync(ctf, CancellationToken.None)).Success);
        Assert.False((await demo.SetStartupItemEnabledAsync(ctf, false, CancellationToken.None)).Success);
    }

    [Fact]
    public void System_tools_are_never_looked_up_by_bare_name_on_windows()
    {
        var tool = CommandRunner.SystemTool("bcdedit.exe");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), tool);
        }
        else
        {
            Assert.Equal("bcdedit.exe", tool);
        }
    }
}
