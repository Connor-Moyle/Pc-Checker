using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

public class UninstallElevationRulesTests
{
    [Fact]
    public void Per_user_installs_never_run_with_administrator_rights()
    {
        var discord = InstalledProgram.Create("Discord", "Discord", source: SoftwareSource.User,
            uninstallString: @"C:\Users\Alex\AppData\Local\Discord\Update.exe --uninstall");
        Assert.NotNull(UninstallElevationRules.UnelevatedReason(discord, @"C:\Users\Alex\AppData\Local\Discord\Update.exe"));

        // Even msiexec: a per-user entry's arguments come from HKCU too.
        var msi = InstalledProgram.Create("{11111111-2222-3333-4444-555555555555}", "Tool", source: SoftwareSource.User, installer: InstallerKind.Msi);
        Assert.NotNull(UninstallElevationRules.UnelevatedReason(msi, "msiexec.exe"));
    }

    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Roaming\Vendor\uninstall.exe")]
    [InlineData(@"%LOCALAPPDATA%\Programs\Vendor\Uninstall Vendor.exe")]
    [InlineData(@"C:\ProgramData\Vendor\unins000.exe")]
    [InlineData(@"C:\Users\Alex\Downloads\setup\uninstall.exe")]
    public void Machine_uninstallers_in_user_writable_folders_run_unelevated(string file)
    {
        var program = InstalledProgram.Create("Vendor", "Vendor", source: SoftwareSource.Machine64);
        Assert.NotNull(UninstallElevationRules.UnelevatedReason(program, file));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Vendor\uninstall.exe")]
    [InlineData(@"C:\Program Files (x86)\Vendor\unins000.exe")]
    [InlineData("msiexec.exe")]
    public void Machine_uninstallers_in_protected_folders_keep_administrator_rights(string file)
    {
        var program = InstalledProgram.Create("Vendor", "Vendor", source: SoftwareSource.Machine32);
        Assert.Null(UninstallElevationRules.UnelevatedReason(program, file));
    }
}
