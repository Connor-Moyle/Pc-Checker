using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

public class BundleDetectorTests
{
    private static readonly DateTime T0 = new(2025, 3, 1, 10, 0, 0);

    [Fact]
    public void Nested_install_location_groups_child_under_parent()
    {
        var parent = InstalledProgram.Create("p", "Big Suite", "Contoso", installLocation: @"C:\Program Files\BigSuite");
        var child = InstalledProgram.Create("c", "Big Suite Helper", "Contoso", installLocation: @"C:\Program Files\BigSuite\Helper\");
        var unrelated = InstalledProgram.Create("u", "Other", "Fabrikam", installLocation: @"C:\Program Files\Other");

        var groups = BundleDetector.Detect([parent, child, unrelated]);
        var g = Assert.Single(groups);
        Assert.Equal("p", g.Parent.Id);
        Assert.Equal("c", Assert.Single(g.Children).Id);
        Assert.Contains("folder", g.Reason);
    }

    [Fact]
    public void Same_time_different_publisher_groups()
    {
        var a = InstalledProgram.Create("a", "Photo Editor", "Fabrikam", T0);
        var b = InstalledProgram.Create("b", "McAfee WebAdvisor", "McAfee", T0.AddMinutes(1));
        var groups = BundleDetector.Detect([a, b]);
        var g = Assert.Single(groups);
        Assert.Equal("a", g.Parent.Id);
        Assert.Equal("b", g.Children[0].Id);
        Assert.Contains("known bundled extra", g.Reason);
    }

    [Fact]
    public void Known_extra_becomes_child_even_when_installed_first()
    {
        var extra = InstalledProgram.Create("x", "Avast Secure Browser", "Avast", T0);
        var app = InstalledProgram.Create("y", "Video Tool", "Contoso", T0.AddMinutes(2));
        var g = Assert.Single(BundleDetector.Detect([extra, app]));
        Assert.Equal("y", g.Parent.Id);
        Assert.Equal("x", g.Children[0].Id);
    }

    [Fact]
    public void Same_publisher_or_far_apart_do_not_group()
    {
        var a = InstalledProgram.Create("a", "Tool A", "Contoso", T0);
        var b = InstalledProgram.Create("b", "Tool B", "Contoso", T0.AddMinutes(1));
        var c = InstalledProgram.Create("c", "Tool C", "Fabrikam", T0.AddHours(3));
        Assert.Empty(BundleDetector.Detect([a, b, c]));
    }

    [Fact]
    public void Runtime_dependencies_are_labelled_not_bloat()
    {
        var game = InstalledProgram.Create("g", "Space Game", "Studio", T0);
        var vc = InstalledProgram.Create("v", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation", T0.AddSeconds(30));
        var g = Assert.Single(BundleDetector.Detect([game, vc]));
        Assert.Contains("runtime dependency", g.Reason);
        Assert.True(BundleDetector.IsRuntimeDependency(vc));
        Assert.False(BundleDetector.IsKnownBundledExtra(vc));
    }

    [Fact]
    public void Store_apps_without_folders_are_ignored()
    {
        var store = InstalledProgram.Create("s", "Store App", "Contoso", T0, source: SoftwareSource.StoreApp);
        var other = InstalledProgram.Create("o", "Other", "Fabrikam", T0);
        Assert.Empty(BundleDetector.Detect([store, other]));
    }

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Empty(BundleDetector.Detect([]));
    }
}

public class UninstallCommandBuilderTests
{
    [Fact]
    public void Msi_product_code_builds_msiexec()
    {
        var p = InstalledProgram.Create("{12345678-1234-1234-1234-123456789ABC}", "Msi App",
            uninstallString: "MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", installer: InstallerKind.Msi);
        var quiet = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("msiexec.exe", quiet.FileName);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789ABC} /norestart /qn", quiet.Arguments);
        var loud = UninstallCommandBuilder.Build(p, quiet: false)!.Value;
        Assert.DoesNotContain("/qn", loud.Arguments);
    }

    [Fact]
    public void Inno_setup_gets_very_silent_flags()
    {
        var p = InstalledProgram.Create("i", "Inno App", uninstallString: "\"C:\\Program Files\\Inno App\\unins000.exe\"", installer: InstallerKind.InnoSetup);
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal(@"C:\Program Files\Inno App\unins000.exe", file);
        Assert.Contains("/VERYSILENT", args);
        Assert.Contains("/NORESTART", args);
    }

    [Fact]
    public void Nsis_and_installshield_flags()
    {
        var nsis = InstalledProgram.Create("n", "Nsis App", uninstallString: "C:\\Apps\\Nsis\\uninstall.exe");
        Assert.Equal("/S", UninstallCommandBuilder.Build(nsis, quiet: true)!.Value.Arguments);

        var shield = InstalledProgram.Create("s", "Shield App", uninstallString: "\"C:\\Apps\\setup.exe\" -runfromtemp -removeonly", installer: InstallerKind.InstallShield);
        var (_, args) = UninstallCommandBuilder.Build(shield, quiet: true)!.Value;
        Assert.Contains("/s /v\"/qn\"", args);
        Assert.Contains("-removeonly", args);
    }

    [Fact]
    public void Quiet_uninstall_string_is_preferred_when_present()
    {
        var p = InstalledProgram.Create("q", "Quiet App", uninstallString: "\"C:\\A\\u.exe\"", quietUninstallString: "\"C:\\A\\u.exe\" /silent");
        var (_, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("/silent", args);
    }

    [Fact]
    public void Store_apps_use_remove_appxpackage()
    {
        var p = InstalledProgram.Create("st", "Store App", source: SoftwareSource.StoreApp, installer: InstallerKind.StoreApp,
            packageFullName: "Contoso.App_1.0.0.0_x64__abc123");
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("powershell.exe", file);
        Assert.Contains("Remove-AppxPackage -Package 'Contoso.App_1.0.0.0_x64__abc123'", args);
    }

    [Fact]
    public void Missing_information_returns_null()
    {
        Assert.Null(UninstallCommandBuilder.Build(InstalledProgram.Create("x", "No uninstall"), quiet: true));
        Assert.Null(UninstallCommandBuilder.Build(InstalledProgram.Create("s", "Store no pkg", source: SoftwareSource.StoreApp), quiet: true));
    }

    [Fact]
    public void SplitCommand_handles_quoted_and_unquoted()
    {
        Assert.Equal((@"C:\x y\u.exe", "/a"), UninstallCommandBuilder.SplitCommand("\"C:\\x y\\u.exe\" /a"));
        Assert.Equal((@"C:\x\u.exe", "/b /c"), UninstallCommandBuilder.SplitCommand(@"C:\x\u.exe /b /c"));
        Assert.Equal(("rundll32", "foo"), UninstallCommandBuilder.SplitCommand("rundll32 foo"));
        Assert.Null(UninstallCommandBuilder.SplitCommand(" "));
    }

    [Theory]
    [InlineData("MsiExec.exe /X{...}", InstallerKind.Msi)]
    [InlineData("\"C:\\P\\unins000.exe\"", InstallerKind.InnoSetup)]
    [InlineData("C:\\P\\uninstall.exe", InstallerKind.Nsis)]
    [InlineData("C:\\Users\\me\\AppData\\Local\\App\\Update.exe --uninstall", InstallerKind.Squirrel)]
    [InlineData("C:\\P\\remove.exe", InstallerKind.Unknown)]
    public void GuessInstaller_recognises_common_tools(string command, InstallerKind expected)
    {
        Assert.Equal(expected, UninstallCommandBuilder.GuessInstaller(command));
    }
}

public class ProtectedSoftwareRulesTests
{
    [Fact]
    public void Edge_and_defender_are_critical()
    {
        var edge = ProtectedSoftwareRules.Evaluate(InstalledProgram.Create("e", "Microsoft Edge"));
        Assert.True(edge.IsProtected);
        Assert.True(edge.Critical);
        Assert.False(ProtectedSoftwareRules.CanUninstall(InstalledProgram.Create("e", "Microsoft Edge"), advancedMode: true).Allowed);
    }

    [Fact]
    public void Runtimes_can_be_removed_in_advanced_mode_only()
    {
        var vc = InstalledProgram.Create("v", "Microsoft Visual C++ 2019 Redistributable");
        Assert.False(ProtectedSoftwareRules.CanUninstall(vc, advancedMode: false).Allowed);
        Assert.True(ProtectedSoftwareRules.CanUninstall(vc, advancedMode: true).Allowed);
    }

    [Fact]
    public void Ordinary_programs_are_not_protected()
    {
        var p = ProtectedSoftwareRules.Apply(InstalledProgram.Create("p", "Notepad++"));
        Assert.False(p.IsProtected);
        Assert.Null(p.ProtectionReason);
        Assert.True(ProtectedSoftwareRules.CanUninstall(p, false).Allowed);
    }

    [Fact]
    public void Store_framework_packages_are_protected()
    {
        var p = InstalledProgram.Create("s", "VCLibs", source: SoftwareSource.StoreApp, packageFullName: "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe");
        Assert.True(ProtectedSoftwareRules.Evaluate(p).IsProtected);
    }
}
