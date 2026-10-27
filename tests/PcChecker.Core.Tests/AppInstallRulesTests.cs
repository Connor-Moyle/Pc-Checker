using PcChecker.Core.Scheduling;
using PcChecker.Core.TaskManager;
using PcChecker.Core.Updates;
using Xunit;

namespace PcChecker.Core.Tests;

public class AppInstallRulesTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Theory]
    [InlineData(@"C:\Program Files", @"C:\Program Files\PC Checker\PcChecker.exe")]
    [InlineData(@"C:\Program Files\", @"C:\Program Files\PC Checker\PcChecker.exe")]
    [InlineData(@"D:\Apps", @"D:\Apps\PC Checker\PcChecker.exe")]
    public void InstallPath_is_the_PC_Checker_folder_under_Program_Files(string root, string expected) =>
        Assert.Equal(expected, AppInstallRules.InstallPath(root));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void InstallPath_is_null_without_a_root(string? root) => Assert.Null(AppInstallRules.InstallPath(root));

    [Theory]
    [InlineData(@"C:\Users\Sam\Downloads\PcChecker.exe", true)]
    [InlineData(@"C:\Users\Sam\Downloads\PcChecker-arm64.exe", true)]
    [InlineData(@"C:\Program Files\PC Checker\PcChecker.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Tools\PcChecker.exe", false)]
    [InlineData(@"C:\Program Files\..\Users\Sam\PcChecker.exe", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    [InlineData(@"C:\src\bin\Debug\Other.exe", false)]
    [InlineData(null, false)]
    public void CanInstall_only_for_a_published_exe_outside_Program_Files(string? processPath, bool expected) =>
        Assert.Equal(expected, AppInstallRules.CanInstall(processPath, Roots));

    [Fact]
    public void The_installed_path_is_eligible_for_Replace_Task_Manager()
    {
        var path = AppInstallRules.InstallPath(Roots[0])!;
        Assert.True(TaskManagerReplacement.IsInstalledLocation(path, Roots));
        Assert.Null(TaskManagerReplacement.IneligibleReason(path, Roots));
        Assert.False(AppInstallRules.CanInstall(path, Roots));
    }

    [Fact]
    public void Refusal_messages_point_at_the_install_action_not_an_installer()
    {
        var reason = TaskManagerReplacement.IneligibleReason(@"C:\Users\Sam\Downloads\PcChecker.exe", Roots)!;
        Assert.Contains("Install in Program Files", reason);
        Assert.DoesNotContain("installer", reason, StringComparison.OrdinalIgnoreCase);

        var unsafeTask = TaskExecutableRules.Unsafe(@"C:\Users\Sam\Downloads\PcChecker.exe", @"C:\Users\Sam\Downloads");
        Assert.Contains("Install in Program Files", unsafeTask);
    }

    [Fact]
    public void Demo_install_service_offers_nothing()
    {
        var demo = new DemoAppInstallService();
        Assert.False(demo.CanInstall);
        Assert.False(demo.InstallAndStart().Success);
        Assert.Equal(@"C:\Program Files\PC Checker\PcChecker.exe", demo.InstallPath);
    }
}
